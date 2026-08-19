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
    /// A real ceiling rather than a preference: the speech model states Python
    /// 3.10 to 3.12, and 3.13 has no wheel for it. The old value here was 13,
    /// which on a machine with a current Python picked exactly the interpreter
    /// the install then failed on - and failed with a wall of pip output rather
    /// than with the one sentence that would have explained it.
    /// </summary>
    private const int NewestSupportedMinor = 12;

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

    public PythonEnvironment(string root) => _root = root;

    /// <summary>Where the environment lives.</summary>
    public string VenvPath => Path.Combine(_root, "venv");

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

    private string Marker => Path.Combine(_root, "installed.txt");

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
    private static string Recipe(string requirements)
    {
        try
        {
            return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(requirements)));
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
        var install = await RunAsync(
            VenvPython,
            ["-m", "pip", "install", "--disable-pip-version-check", "-r", requirements],
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

        // PyPI's torch on Windows is the CPU build, and pip has no way to know
        // there is a graphics card in the machine. Left alone, somebody whose
        // card could read a chapter in a minute waits an hour instead and is
        // told only "on cpu" - which reads as a decision we made rather than a
        // wheel we failed to ask for.
        if (await HasNvidiaAsync(cancellationToken))
        {
            progress?.Report(("downloading-cuda", null, string.Empty));
            var cuda = await RunAsync(
                VenvPython,
                [
                    "-m", "pip", "install", "--disable-pip-version-check",
                    // Without this pip sees a torch already installed, decides
                    // the requirement is met, and leaves the CPU build in place -
                    // reporting success while changing nothing.
                    "--upgrade",
                    "--index-url", CudaIndex, "torch", "torchaudio"
                ],
                cancellationToken,
                line =>
                {
                    if (Interesting(line) is { } said)
                        progress?.Report(("downloading-cuda", said.Fraction, said.Text));
                });

            // Not fatal. A machine that cannot fetch the CUDA build still has a
            // working CPU one, and a slow reading beats no reading - but the
            // reason is written down, because "why is this on cpu" is the
            // question it will raise.
            if (cuda.ExitCode != 0)
                await WriteFailureAsync(cuda.Output + "\n" + cuda.Error, cancellationToken);
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
        var (code, _, error) = await RunAsync(
            python.Executable,
            [.. python.Prefix, "-m", "venv", VenvPath],
            cancellationToken);
        return code == 0 ? null : "venv-failed: " + Short(error);
    }

    /// <summary>
    /// Where the CUDA builds of torch live.
    ///
    /// A specific CUDA release rather than "latest": the index is per release,
    /// and which one to ask for is decided by what the newest cards need.
    /// Blackwell parts - the 50-series - are compute capability 12.0, and only
    /// builds from an index this new carry kernels for them. An older one
    /// installs happily and then fails at the first matrix multiply with "no
    /// kernel image is available for execution on the device".
    ///
    /// It also has to be an index somebody is still publishing to. The previous
    /// value, cu128, stopped at torch 2.9.1 for Windows while the current
    /// indexes carry 2.13 - so "upgrade torch from this index" quietly moved
    /// torch *backwards* off whatever the model had installed, and reported
    /// success. Checked against the live index before it is used.
    /// </summary>
    private const string CudaIndex = "https://download.pytorch.org/whl/cu130";

    /// <summary>Whether this machine has an NVIDIA card worth fetching a CUDA
    /// build for. Asked of the driver rather than inferred from the platform.</summary>
    internal async Task<bool> HasNvidiaAsync(CancellationToken cancellationToken)
    {
        var (code, output, _) = await RunAsync(
            "nvidia-smi", ["--query-gpu=name", "--format=csv,noheader"], cancellationToken);
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
            var (code, output, _) = await RunAsync(
                executable, [.. prefix, "--version"], cancellationToken);
            if (code == 0 && IsUsable(output))
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
    /// for a machine that has nothing suitable. The model publishes no wheel
    /// above 3.12, and an interpreter outside the range builds a virtual
    /// environment happily and then fails the install minutes later.
    /// </summary>
    internal static bool IsUsable(string versionOutput)
    {
        var text = versionOutput.Trim();
        var at = text.IndexOf("Python 3.", StringComparison.Ordinal);
        if (at < 0)
            return false;

        var rest = text[(at + "Python 3.".Length)..];
        var digits = new string([.. rest.TakeWhile(char.IsDigit)]);
        if (!int.TryParse(digits, out var minor))
            return false;

        return minor >= OldestSupportedMinor && minor <= NewestSupportedMinor;
    }

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
            // "Collecting torch==2.6.0 (from chatterbox-tts>=0.1.7->-r C:/Users/…)"
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

            if (onLine == null)
            {
                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
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
