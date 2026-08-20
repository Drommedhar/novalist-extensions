using System.Diagnostics;
using System.Text;

namespace Novalist.Extensions.Speech;

/// <summary>
/// A line-delimited conversation with a process.
///
/// An interface so the engine can be tested without Python: the whole of the
/// interesting behaviour - what is asked, in what order, what happens when a
/// clip fails, what happens when the process dies mid-render - is in the engine,
/// and none of it should need a model on the machine to exercise.
/// </summary>
internal interface ISidecarChannel : IDisposable
{
    /// <summary>True while the process is up.</summary>
    bool IsRunning { get; }

    /// <summary>Starts the process. Does not wait for it to be ready.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends one request line.</summary>
    Task SendAsync(string line, CancellationToken cancellationToken = default);

    /// <summary>The next reply line, or null when the process has closed its
    /// output - which is how a sidecar that died is noticed rather than waited
    /// on for ever.</summary>
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the process.</summary>
    void Stop();
}

/// <summary>
/// The real thing: a Python process, spoken to over its own standard streams.
///
/// Excluded from coverage for the same reason the host excludes its speech
/// interop - starting a real process and pumping its pipes is not a unit test.
/// Everything that decides anything lives in <see cref="VoiceEngine"/>, which is
/// tested against a fake channel.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
    Justification = "Process interop; the decisions are in VoiceEngine.")]
internal sealed class ProcessSidecarChannel : ISidecarChannel
{
    private readonly string _executable;
    private readonly string _script;
    private readonly string _workingDirectory;
    private readonly string _huggingFaceToken;
    private Process? _process;

    public ProcessSidecarChannel(
        string executable,
        string script,
        string workingDirectory,
        string? huggingFaceToken = null)
    {
        _executable = executable;
        _script = script;
        _workingDirectory = workingDirectory;
        _huggingFaceToken = huggingFaceToken ?? string.Empty;
    }

    public bool IsRunning => _process is { HasExited: false };

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Without a byte-order mark, and that is the whole of it.
            //
            // Encoding.UTF8 is a UTF8Encoding built to emit one, so the first
            // thing ever written to the sidecar's input was EF BB BF and then
            // the request. Python read a line beginning with U+FEFF, could not
            // parse it as JSON, and dropped it - so the very first request of
            // every session was swallowed, both sides waited for the other for
            // ever, and the writer watched a dialog that said "Starting" until
            // they gave up. Three bytes.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false)
        };
        // -u so the sidecar's replies are not sat on in a buffer while the host
        // waits for a line that was written a minute ago.
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add(_script);
        info.ArgumentList.Add("--work");
        info.ArgumentList.Add(_workingDirectory);

        // Belt and braces for the same thing the sidecar sets on itself: on a
        // machine whose locale is a code page, Python's standard streams are
        // that code page unless told otherwise, and the payload here is
        // somebody's novel.
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        // Keep large model weights with the extension. Apart from making the
        // disk estimate truthful, deleting the extension's data then removes
        // the speech stack completely instead of leaving a second cache under
        // the user's profile.
        info.Environment["HF_HOME"] = Path.Combine(
            Path.GetDirectoryName(_workingDirectory) ?? _workingDirectory, "models");
        // Hugging Face reads this directly. It never enters a protocol message,
        // command-line argument or diagnostic where it could be displayed.
        if (_huggingFaceToken.Length > 0)
            info.Environment["HF_TOKEN"] = _huggingFaceToken;

        _process = Process.Start(info)
            ?? throw new InvalidOperationException("the speech sidecar did not start");

        // Its stderr is the model's own chatter - progress bars, warnings about
        // kernels. It goes to the debugger and nowhere else: it can quote a
        // path, and a diagnostic log the writer may send us must never carry
        // one.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync() is { } line)
                    Debug.WriteLine("[Speech] " + line);
            }
            catch (IOException)
            {
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public async Task SendAsync(string line, CancellationToken cancellationToken = default)
    {
        if (_process == null) return;
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
        => _process == null ? null : await _process.StandardOutput.ReadLineAsync(cancellationToken);

    public void Stop()
    {
        if (_process == null) return;
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose() => Stop();
}
