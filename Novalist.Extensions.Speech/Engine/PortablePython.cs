using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Novalist.Extensions.Speech;

/// <summary>
/// Fetches an interpreter for machines that have none.
///
/// Python 3.10 to 3.12 is a real ceiling - the model publishes no wheel above
/// 3.12 - and the honest consequence was that a writer whose machine had 3.13,
/// or no Python at all, was told to go and install one. That is a reasonable
/// thing to ask a developer and an unreasonable thing to ask a novelist, and it
/// is the difference between an extension that installs itself and one that
/// hands somebody a homework assignment about PATH.
///
/// So: uv, which is a single self-contained executable that can fetch a
/// standalone CPython and build a virtual environment from it. Nothing is
/// installed system-wide, nothing is put on PATH, and nothing touches whatever
/// Python the machine already has - the interpreter lands under this
/// extension's own settings folder beside the environment it is for, and
/// deleting that folder undoes all of it.
///
/// Tried second, not first. A machine with a suitable Python already on it is
/// left alone, because a writer who has one has not asked us to download
/// another.
///
/// Excluded from coverage with the rest of the interop: it downloads a file and
/// starts processes. What it decides - which build to ask for - is
/// <see cref="AssetName"/>, which is tested.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
    Justification = "Network and process interop for fetching an interpreter.")]
internal sealed class PortablePython
{
    /// <summary>
    /// The Python asked for.
    ///
    /// The top of the supported range rather than the bottom: it is the one
    /// still getting wheels built for it, and the one a writer's machine is
    /// least likely to already have and be left arguing with.
    /// </summary>
    public const string Version = "3.12";

    /// <summary>
    /// Which uv is fetched.
    ///
    /// A pinned release rather than "latest": a build that changes under an
    /// installed application is a build nobody tested it against, and this runs
    /// once on a writer's machine with several gigabytes riding on it.
    /// </summary>
    private const string UvVersion = "0.9.7";

    private readonly string _root;

    public PortablePython(string root) => _root = root;

    /// <summary>Where the interpreter and uv's own downloads live. Under the
    /// extension's settings folder, never in the project and never on PATH.</summary>
    private string Home => Path.Combine(_root, "python");

    private string UvPath => Path.Combine(
        Home, "uv", OperatingSystem.IsWindows() ? "uv.exe" : "uv");

    /// <summary>
    /// The uv build for this machine, or null for a platform it does not
    /// publish one for.
    ///
    /// Named by target triple. Getting this wrong is not a download that fails
    /// loudly - it is a 404 that reads as "no interpreter available" on a
    /// machine that could have had one.
    /// </summary>
    internal static string? AssetName(Architecture architecture, bool windows, bool mac)
    {
        var cpu = architecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            _ => null
        };
        if (cpu == null)
            return null;

        if (windows)
            return $"uv-{cpu}-pc-windows-msvc.zip";
        return mac
            ? $"uv-{cpu}-apple-darwin.tar.gz"
            : $"uv-{cpu}-unknown-linux-gnu.tar.gz";
    }

    /// <summary>
    /// Builds a virtual environment at <paramref name="venvPath"/> on an
    /// interpreter fetched for the purpose. Returns a fault code, or null on
    /// success.
    /// </summary>
    /// <param name="progress">Told about the download, which is the part that
    /// takes long enough to need saying.</param>
    public async Task<string?> BuildVenvAsync(
        string venvPath,
        IProgress<(string Step, double? Fraction, string Detail)>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(("fetching-python", null, string.Empty));

        var uv = await EnsureUvAsync(cancellationToken);
        if (uv == null)
            return "no-python";

        Directory.CreateDirectory(Home);

        // uv keeps its interpreters and its cache wherever it is told. Told
        // here, so nothing lands in the writer's home directory and removing
        // this extension removes all of it.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(Home, "interpreters"),
            ["UV_CACHE_DIR"] = Path.Combine(Home, "cache"),
            // Never the machine's own Python, even where there is one. The point
            // of coming down this path at all is that it was not suitable.
            ["UV_PYTHON_PREFERENCE"] = "only-managed"
        };

        var installed = await RunAsync(
            uv, ["python", "install", Version], environment, cancellationToken);
        if (installed.ExitCode != 0)
            return "python-fetch-failed: " + Short(installed.Error);

        // --seed puts pip in the environment. Everything downstream installs
        // with pip and reads pip's own progress, and a venv without it would
        // mean re-teaching that whole path a second package manager's output.
        var made = await RunAsync(
            uv, ["venv", "--seed", "--python", Version, venvPath], environment, cancellationToken);
        return made.ExitCode == 0 ? null : "venv-failed: " + Short(made.Error);
    }

    /// <summary>uv on this machine, fetching it if it is not here yet.</summary>
    private async Task<string?> EnsureUvAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(UvPath))
            return UvPath;

        var asset = AssetName(
            RuntimeInformation.ProcessArchitecture,
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS());
        if (asset == null)
            return null;

        var into = Path.Combine(Home, "uv");
        Directory.CreateDirectory(into);
        var archive = Path.Combine(into, asset);

        try
        {
            var url = $"https://github.com/astral-sh/uv/releases/download/{UvVersion}/{asset}";
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var response = await http.GetAsync(
                       url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                    return null;
                await using var file = File.Create(archive);
                await response.Content.CopyToAsync(file, cancellationToken);
            }

            Unpack(archive, into);
            File.Delete(archive);

            if (!OperatingSystem.IsWindows() && File.Exists(UvPath))
            {
                // The tarball carries the bit, but .NET's tar reader does not
                // apply it on its own - and a downloaded file nobody may execute
                // is the same as a download that failed.
                File.SetUnixFileMode(
                    UvPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                       or UnauthorizedAccessException or TaskCanceledException
                                       or InvalidDataException)
        {
            return null;
        }

        return File.Exists(UvPath) ? UvPath : null;
    }

    /// <summary>
    /// Unpacks the archive, flattening it.
    ///
    /// The tarballs hold a single directory with the binary inside; the zip
    /// holds the binary at the root. Flattening makes both look the same to
    /// everything downstream, which is one path to get right rather than two.
    /// </summary>
    private static void Unpack(string archive, string into)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                if (entry.Name.Length == 0)
                    continue;
                entry.ExtractToFile(Path.Combine(into, entry.Name), overwrite: true);
            }
            return;
        }

        var staging = Path.Combine(into, "staging");
        Directory.CreateDirectory(staging);
        using (var file = File.OpenRead(archive))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        {
            TarFile.ExtractToDirectory(gzip, staging, overwriteFiles: true);
        }

        foreach (var found in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            File.Move(found, Path.Combine(into, Path.GetFileName(found)), overwrite: true);
        try
        {
            Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<(int ExitCode, string Error)> RunAsync(
        string executable,
        string[] args,
        Dictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        foreach (var (key, value) in environment) info.Environment[key] = value;

        try
        {
            using var process = Process.Start(info);
            if (process == null)
                return (-1, "did not start");

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await output;
            return (process.ExitCode, await error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return (-1, ex.GetType().Name);
        }
    }

    private static string Short(string error)
    {
        var trimmed = error.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[^200..];
    }
}
