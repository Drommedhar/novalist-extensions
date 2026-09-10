using System.Diagnostics;

namespace Novalist.Extensions.Speech;

/// <summary>
/// Finds a Python and builds the environment the sidecar needs.
///
/// Its own virtual environment under the extension's settings folder, never the
/// machine's Python. A speech stack pulls in torch and a pile of native wheels,
/// and installing those into whatever interpreter happened to be on PATH is how
/// you break somebody's unrelated work with a writing application.
///
/// Excluded from coverage with the rest of the interop: what it does is start
/// processes and look for files. What it decides - which interpreter, whether
/// the environment is already built - is small and stated plainly here.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
    Justification = "Process and filesystem interop for a Python install.")]
internal sealed class PythonEnvironment
{
    /// <summary>
    /// The newest Python worth asking for.
    ///
    /// Qwen-TTS publishes for Python 3.9 through 3.13. We keep 3.10 as the floor
    /// because one of its pinned runtime dependencies requires it, and stop at
    /// 3.13 until the upstream stack declares a newer interpreter supported.
    /// </summary>
    private const int NewestSupportedMinor = 13;

    /// <summary>The oldest worth trying.</summary>
    private const int OldestSupportedMinor = 10;

    /// <summary>
    /// Interpreters worth trying, best first.
    ///
    /// On Windows the launcher goes first and is asked for specific versions,
    /// newest supported downwards: it is the only thing on the machine that
    /// knows what is installed besides whatever happens to be on PATH. A bare
    /// request would hand back the newest, which is exactly the one that does
    /// not work.
    /// </summary>
    private static IEnumerable<(string Executable, string[] Prefix)> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            for (var minor = NewestSupportedMinor; minor >= OldestSupportedMinor; minor--)
                yield return ("py", [$"-3.{minor}"]);
        }

        foreach (var minor in Enumerable.Range(OldestSupportedMinor,
                     NewestSupportedMinor - OldestSupportedMinor + 1).Reverse())
        {
            yield return ($"python3.{minor}", []);
        }

        // Whatever is on PATH, last. A version outside the preferred range is
        // still tried at this point rather than refused: it very often works,
        // and refusing a machine that would have been fine is worse than an
        // install that reports its own failure.
        yield return ("python3", []);
        yield return ("python", []);
        if (OperatingSystem.IsWindows())
            yield return ("py", ["-3"]);
    }

    private readonly string _root;
    private readonly bool _useMlx;
    private readonly string _gpu;
    private readonly Func<string, string[], CancellationToken, Action<string>?,
        Task<(int ExitCode, string Output, string Error)>> _run;

    public PythonEnvironment(string root, bool useMlx = false, string? gpu = null,
        Func<string, string[], CancellationToken, Action<string>?,
            Task<(int ExitCode, string Output, string Error)>>? run = null)
    {
        _root = root;
        _useMlx = useMlx;
        _gpu = useMlx ? "cpu" : gpu ?? WindowsGpu.Detect();
        _run = run ?? RunAsync;
    }

    /// <summary>Where the environment lives.</summary>
    public string VenvPath => Path.Combine(_root, _useMlx ? "venv-mlx" : _gpu == "rocm" ? "venv-rocm" : "venv");

    /// <summary>The interpreter inside it.</summary>
    public string VenvPython => OperatingSystem.IsWindows()
        ? Path.Combine(VenvPath, "Scripts", "python.exe")
        : Path.Combine(VenvPath, "bin", "python");

    /// <summary>Where the sidecar writes its clips. Scratch: the host has its own
    /// cache, and this is emptied whenever the engine restarts.</summary>
    public string WorkPath => Path.Combine(_root, "work");

    /// <summary>
    /// True once the environment exists and holds <em>these</em> requirements.
    ///
    /// The recipe, not merely the fact that something was once installed. The
    /// marker used to be a timestamp, so an environment built for an older
    /// release counted as built for ever - and a version that changed the
    /// packages shipped a sidecar importing something the environment did not
    /// have. The writer saw an import error and no way to reach the install that
    /// would have fixed it, because as far as this was concerned there was
    /// nothing left to do.
    /// </summary>
    public bool IsBuiltFor(string requirements)
    {
        var recipe = Recipe(requirements);
        // No readable recipe is not a match with a blank marker - it is a
        // question that cannot be answered, and answering "already built" to it
        // is how an environment nobody can fix gets one.
        return recipe.Length > 0 && File.Exists(VenvPython) && ReadMarker() == recipe;
    }

    private string Marker => Path.Combine(_root, _useMlx ? "installed-mlx.txt" : _gpu == "rocm" ? "installed-rocm.txt" : "installed.txt");

    /// <summary>What the marker says was installed, or empty when nothing has
    /// been - including when it cannot be read, which comes to the same thing
    /// for anybody waiting on a working environment.</summary>
    private string ReadMarker()
    {
        try
        {
            return File.Exists(Marker) ? File.ReadAllText(Marker).Trim() : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// A fingerprint of the requirements file, so a release that changes the
    /// packages rebuilds rather than running against the last one's.
    ///
    /// Of the file's own contents, so somebody who edited theirs for their own
    /// card keeps their environment until they change it again.
    /// </summary>
    internal string Recipe(string requirements)
    {
        try
        {
            var content = File.ReadAllBytes(requirements);
            // Installer changes must invalidate old CPU-only installs too.
            // MLX keeps its existing environment and marker.
            if (!_useMlx)
                content = [.. content, .. System.Text.Encoding.UTF8.GetBytes(
                    "\nwindows-gpu-v1:" + _gpu + string.Join('\n', GpuPackages(_gpu)))];
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>What the last attempt actually went wrong with, for the writer
    /// and for whoever they show it to. Kept beside the environment rather than
    /// only in a log, because the log is opt-in and this is the moment somebody
    /// needs it.</summary>
    public string FailurePath => Path.Combine(_root, "install-failed.txt");

    /// <summary>
    /// Builds the environment: a venv, then the requirements.
    ///
    /// Reports progress because this is minutes rather than seconds - torch
    /// alone is a couple of gigabytes - and a writer owed that wait is owed
    /// knowing it is happening.
    /// </summary>
    public async Task<string?> BuildAsync(
        string requirements,
        IProgress<(string Step, double? Fraction, string Detail)>? progress,
        CancellationToken cancellationToken = default)
    {
        // Said before the search, not after: trying several interpreters is the
        // first thing that takes a noticeable moment, and a dialog that has not
        // changed since it opened reads as one that never will.
        progress?.Report(("looking-for-python", null, string.Empty));
        var gpu = _gpu;
        if (!_useMlx && !OperatingSystem.IsWindows() && await HasNvidiaAsync(cancellationToken))
            gpu = "cuda";
        var python = await FindPythonAsync(cancellationToken);

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(WorkPath);

        // A half-built environment from a previous attempt is worse than none.
        // The first run here left a virtual environment on a Python the install
        // then failed on, and every attempt afterwards reused it and failed the
        // same way - the writer pressing Prepare again could not get out of it,
        // because the thing that was wrong was the part being kept.
        //
        // An environment built for a different recipe goes the same way, and for
        // a sharper reason: installing this release's packages on top of the
        // last one's leaves both, with two sets of pins arguing about which
        // torch is installed. The weights are not in here - they live in the
        // model cache and are kept - so what this costs is the wheels.
        if (File.Exists(VenvPython) && !IsBuiltFor(requirements))
        {
            progress?.Report(("creating-environment", null, string.Empty));
            Discard(VenvPath);
        }

        if (!File.Exists(VenvPython))
        {
            progress?.Report(("creating-environment", null, string.Empty));
            // The machine's own Python where it has a usable one, and an
            // interpreter fetched for the purpose where it does not. The second
            // half is what turns "install Python 3.12 and try again" - which is
            // homework, not an installer - into a download the writer has
            // already agreed to.
            var failure = python == null
                ? await new PortablePython(_root).BuildVenvAsync(VenvPath, progress, cancellationToken)
                : await SystemVenvAsync(python.Value, cancellationToken);
            if (failure != null)
                return failure;
        }

        // The long one - a couple of gigabytes of wheels. pip's own output is
        // streamed into the dialog rather than collected, because a status line
        // that has not changed for four minutes is indistinguishable from a
        // hang, and this step legitimately takes that long.
        progress?.Report(("downloading", null, string.Empty));
        // Resolve the model dependencies and the chosen GPU wheels together;
        // do not download a CPU torch first or let a later pip upgrade replace
        // the selected runtime. ROCm uses AMD's CPython 3.12 Windows wheels.
        var install = await _run(
            VenvPython,
            ["-m", "pip", "install", "--disable-pip-version-check", "-r", requirements, .. GpuPackages(gpu)],
            cancellationToken,
            line =>
            {
                if (Interesting(line) is not { } said)
                    return;
                // pip stops talking once it starts writing files, and that is
                // minutes of it. Naming the phase is the least the dialog can do
                // when it is about to have nothing else to say.
                var step = said.Text.StartsWith("Installing", StringComparison.Ordinal)
                    ? "installing"
                    : "downloading";
                progress?.Report((step, said.Fraction, said.Text));
            });
        if (install.ExitCode != 0)
        {
            // Written down, whole, where it can be read. The dialog cannot show
            // two hundred lines of pip and the log must not carry a path, but
            // somebody trying to get this working needs the actual words.
            await WriteFailureAsync(install.Output + "\n" + install.Error, cancellationToken);
            return "install-failed: " + Short(install.Error);
        }

        if (!_useMlx && gpu != "cpu")
        {
            var probe = await _run(VenvPython, ["-c", GpuProbe(gpu)], cancellationToken, null);
            if (probe.ExitCode != 0)
            {
                await WriteFailureAsync(probe.Output + "\n" + probe.Error, cancellationToken);
                return "gpu-unavailable";
            }
        }

        Discard(FailurePath);
        await File.WriteAllTextAsync(Marker, Recipe(requirements), cancellationToken);
        progress?.Report(("installed", null, string.Empty));
        return null;
    }

    /// <summary>A virtual environment on an interpreter the machine already
    /// had. Null on success, a fault code otherwise.</summary>
    private async Task<string?> SystemVenvAsync(
        (string Executable, string[] Prefix) python, CancellationToken cancellationToken)
    {
        var (code, _, error) = await _run(
            python.Executable,
            [.. python.Prefix, "-m", "venv", VenvPath],
            cancellationToken, null);
        return code == 0 ? null : "venv-failed: " + Short(error);
    }

    /// <summary>
    /// Matched torch/torchaudio builds. CUDA 12.8 covers RTX 40/50-series
    /// without imposing CUDA 13's driver floor. Versions are explicit so pip
    /// cannot prefer a newer CPU build. AMD's official Windows 7.2.1 recipe:
    /// https://rocm.docs.amd.com/projects/radeon-ryzen/en/docs-7.2.1/docs/install/installrad/windows/install-pytorch.html
    /// </summary>
    internal static string[] GpuPackages(string gpu) => gpu switch
    {
        "cuda" => ["--extra-index-url", "https://download.pytorch.org/whl/cu128",
            "torch==2.9.1+cu128", "torchaudio==2.9.1+cu128"],
        "rocm" => [.. new[] {
            "rocm_sdk_core-7.2.1-py3-none-win_amd64.whl",
            "rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl",
            "rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl",
            "rocm-7.2.1.tar.gz",
            "torch-2.9.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl",
            "torchaudio-2.9.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl"
        }.Select(file => "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/" + file)],
        _ => []
    };

    internal static string GpuProbe(string gpu) =>
        "import torch; assert torch.cuda.is_available(), 'GPU unavailable; check the graphics driver'; "
        + (gpu == "rocm" ? "assert torch.version.hip, 'Expected AMD ROCm torch'; "
            : "assert torch.version.cuda, 'Expected NVIDIA CUDA torch'; ")
        + "x = torch.ones((2, 2), device='cuda', dtype=torch.bfloat16); "
        + "y = x @ x; torch.cuda.synchronize(); assert torch.isfinite(y).all().item(); "
        + "print(torch.cuda.get_device_name(0))";

    /// <summary>Whether this machine has an NVIDIA card worth fetching a CUDA
    /// build for. Asked of the driver rather than inferred from the platform.</summary>
    internal async Task<bool> HasNvidiaAsync(CancellationToken cancellationToken)
    {
        var (code, output, _) = await _run(
            "nvidia-smi", ["--query-gpu=name", "--format=csv,noheader"], cancellationToken, null);
        return code == 0 && output.Trim().Length > 0;
    }

    /// <summary>
    /// The best interpreter on the machine, or null when none of them is a
    /// version the speech stack can install into.
    /// </summary>
    internal async Task<(string Executable, string[] Prefix)?> FindPythonAsync(
        CancellationToken cancellationToken)
    {
        foreach (var (executable, prefix) in Candidates())
        {
            var (code, output, _) = await _run(
                executable, [.. prefix, "--version"], cancellationToken, null);
            if (code != 0 || !IsUsable(output, _gpu == "rocm"))
                continue;
            if (OperatingSystem.IsWindows() && _gpu != "cpu")
            {
                var (bitsCode, bits, _) = await _run(executable,
                    [.. prefix, "-c", "import struct; print(struct.calcsize('P') * 8)"], cancellationToken, null);
                if (bitsCode != 0 || bits.Trim() != "64")
                    continue;
            }
            if (_useMlx)
            {
                // An Intel interpreter under Rosetta cannot load MLX's ARM64
                // wheels. Keep looking, then fetch a native private Python.
                var (archCode, probe, _) = await _run(executable,
                    [.. prefix, "-c", "import os, platform, sys; print(platform.machine()); print(os.path.realpath(sys.executable))"], cancellationToken, null);
                using var lines = new StringReader(probe);
                if (archCode != 0 || !IsNativeMlxPython(lines.ReadLine() ?? string.Empty))
                    continue;
                // uv-managed Python may be exposed through a PATH symlink.
                // CPython's venv then records the symlink directory as its
                // home and cannot find the standard library. Use its real path.
                var nativePython = lines.ReadLine();
                if (!string.IsNullOrWhiteSpace(nativePython) && File.Exists(nativePython))
                    return (nativePython, []);
                continue;
            }
            return (executable, prefix);
        }
        // Nothing in the range. This used to fall back to whatever answered, and
        // the install then failed minutes later with a wall of pip output about
        // a wheel that does not exist for it. Now there is a real alternative -
        // an interpreter fetched for the purpose - and taking it beats spending
        // a two-gigabyte download to find out this one will not do.
        return null;
    }

    /// <summary>
    /// Whether a `--version` line names a Python the speech stack can install
    /// into.
    ///
    /// A rule rather than a preference, now that an interpreter can be fetched
    /// for a machine that has nothing suitable. It follows the supported Qwen
    /// package classifiers while retaining the dependency-imposed 3.10 floor.
    /// </summary>
    internal static bool IsUsable(string versionOutput, bool rocm = false)
    {
        var text = versionOutput.Trim();
        var at = text.IndexOf("Python 3.", StringComparison.Ordinal);
        if (at < 0)
            return false;

        var rest = text[(at + "Python 3.".Length)..];
        var digits = new string([.. rest.TakeWhile(char.IsDigit)]);
        if (!int.TryParse(digits, out var minor))
            return false;

        return rocm ? minor == 12 : minor >= OldestSupportedMinor && minor <= NewestSupportedMinor;
    }

    internal static bool IsNativeMlxPython(string architecture)
        => architecture.Trim() is "arm64" or "aarch64";

    /// <summary>
    /// The pip output worth putting in front of somebody, and how far through it
    /// says the current download is.
    ///
    /// pip says a great deal and almost none of it means anything to a novelist.
    /// What does is which package it is on and how much of it has arrived - the
    /// two things that actually change while somebody waits.
    ///
    /// The download bar is rewritten in place with a carriage return rather than
    /// printed as new lines, which is why the caller splits on both: waiting for
    /// a newline during a two-gigabyte download means waiting minutes for the
    /// next word.
    /// </summary>
    internal static (string Text, double? Fraction)? Interesting(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return null;

        foreach (var prefix in new[] { "Collecting ", "Downloading ", "Installing ", "Building ", "Using cached " })
        {
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var percent = PercentIn(trimmed);
            // "Collecting torch==2.6.0 (from qwen-tts==0.1.1->-r C:/Users/…)"
            // is four fifths path. The package is the part that changes and the
            // part anybody reads; the rest is pip explaining itself to itself.
            var at = trimmed.IndexOf(" (from ", StringComparison.Ordinal);
            if (at > 0)
                trimmed = trimmed[..at];
            var text = trimmed.Length <= 90 ? trimmed : trimmed[..90];
            return (text, percent);
        }
        return null;
    }

    /// <summary>The percentage in a pip progress line, as a fraction. Null where
    /// the line carries none.</summary>
    private static double? PercentIn(string line)
    {
        var at = line.IndexOf('%');
        if (at <= 0)
            return null;

        var start = at;
        while (start > 0 && (char.IsDigit(line[start - 1]) || line[start - 1] == '.'))
            start--;
        if (start == at)
            return null;

        return double.TryParse(
            line[start..at],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value) && value is >= 0 and <= 100
            ? value / 100.0
            : null;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string executable, string[] args, CancellationToken cancellationToken,
        Action<string>? onLine = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(info);
            if (process == null) return (-1, string.Empty, "did not start");
            using var cancelled = cancellationToken.Register(() =>
            {
                // Cancel must stop pip too, before another Prepare can rebuild
                // the environment it is still writing into.
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            });

            if (onLine == null)
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                var output = await outputTask;
                var error = await stderrTask;
                await process.WaitForExitAsync(cancellationToken);
                // Some builds print the version to stderr.
                return (process.ExitCode, output + error, error);
            }

            // Character by character, splitting on carriage returns as well as
            // newlines. pip rewrites its download bar in place with \r, so a
            // reader waiting for a newline hears nothing for the whole of a
            // two-gigabyte download - which is exactly the stretch somebody most
            // needs to see moving.
            var tail = new Queue<string>();
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var buffer = new System.Text.StringBuilder(160);
            var chunk = new char[512];
            int read;
            while ((read = await process.StandardOutput.ReadAsync(chunk, cancellationToken)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var ch = chunk[i];
                    if (ch != '\n' && ch != '\r')
                    {
                        buffer.Append(ch);
                        continue;
                    }
                    if (buffer.Length == 0)
                        continue;

                    var line = buffer.ToString();
                    buffer.Clear();
                    onLine(line);
                    tail.Enqueue(line);
                    if (tail.Count > 20) tail.Dequeue();
                }
            }
            if (buffer.Length > 0)
                onLine(buffer.ToString());
            var stderr = await errorTask;
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, string.Join('\n', tail), stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return (-1, string.Empty, ex.GetType().Name);
        }
    }

    /// <summary>Removes a file or folder that is in the way, and says nothing
    /// when it cannot - the caller is about to try what it wanted to do anyway,
    /// and will report that failure instead.</summary>
    private static void Discard(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task WriteFailureAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_root);
            await File.WriteAllTextAsync(FailurePath, text, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The tail of a failure, short enough to log. Never shown to the
    /// writer whole: pip quotes paths, and a diagnostic log must not.</summary>
    private static string Short(string error)
    {
        var trimmed = error.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[^200..];
    }
}
