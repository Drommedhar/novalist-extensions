using Novalist.Sdk;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Models.Narration;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.Speech;

/// <summary>
/// Gives Novalist a voice.
///
/// The host assembles the reading entirely offline - who speaks each line, how
/// it should be said, and in whose voice - and hands it here to be spoken. This
/// extension is the only part of the arrangement that loads a model, which is
/// why it is an extension: a writer who wants none of this installs none of it,
/// and the application itself carries no speech dependency at all.
///
/// Two models behind one contributor, and keeping them apart is the whole point:
///
/// <list type="bullet">
/// <item>a <b>design</b> model turns a description of a character into a voice,
/// once, and what comes back is stored as audio because designing is not
/// reproducible;</item>
/// <item>a <b>delivery</b> model performs each line in that fixed voice, with
/// the emotion supplied per line as a parameter beside the words.</item>
/// </list>
///
/// A character is therefore one identity and many performances - furious in
/// chapter three, grieving in chapter twenty, recognisably the same person.
///
/// <b>Nothing here opens a socket.</b> The models run on this machine, in a
/// Python environment under the extension's own settings folder, and the only
/// thing that ever reaches the network is the one-off download when the writer
/// presses Prepare. Novalist's read-aloud promises that listening to your book
/// sends nothing anywhere, and an engine is not entitled to break that on the
/// application's behalf.
/// </summary>
public sealed class SpeechExtension : IExtension, IVoiceEngineContributor, IDisposable
{
    private IHostServices? _host;
    private IExtensionLocalization? _loc;
    private PythonEnvironment? _python;
    private VoiceEngine? _engine;
    private string? _fault;
    private string _sidecarDir = string.Empty;

    public string Id => "com.novalist.speech";

    public string DisplayName => "Speech";

    public string Description =>
        "Designs a voice for each character from their Codex entry and reads your book "
        + "in it, with every line performed as the prose directs. Runs on your machine.";

    public string Version => "1.0.0";

    public string Author => "Novalist Team";

    public void Initialize(IHostServices host)
    {
        _host = host;
        _loc = host.GetLocalization(Id);
        var root = host.GetExtensionSettingsPath(Id);
        _sidecarDir = Path.Combine(root, "python");
        Unpack(_sidecarDir);
        _python = new PythonEnvironment(root);
        _engine = new VoiceEngine(
            () => new ProcessSidecarChannel(_python.VenvPython, SidecarScript(), _python.WorkPath),
            _python.WorkPath);
    }

    public void Shutdown() => Dispose();

    public void Dispose()
    {
        _engine?.Dispose();
        _engine = null;
    }

    // ── IVoiceEngineContributor ─────────────────────────────────────

    public string EngineId => "com.novalist.speech.local";

    public string EngineName => "Novalist Speech (local)";

    /// <summary>
    /// What this can be asked for.
    ///
    /// Both kinds of direction, because the delivery model takes an emotion
    /// vector and can also be given the sentence - and the host sends whichever
    /// it is told to. Not <c>EmotionInferred</c>: this engine performs what it
    /// is directed to perform rather than deciding for itself, which is the
    /// point of the writer being able to overrule a line.
    ///
    /// No <c>CloneFromSample</c>: there is no recording to clone from, and an
    /// engine that accepted a call it cannot honour is worse than one that says
    /// so. That is also what keeps a real person's voice out of this entirely.
    /// </summary>
    public VoiceEngineFeatures Features =>
        VoiceEngineFeatures.DesignFromDescription
        | VoiceEngineFeatures.EmotionVector
        | VoiceEngineFeatures.EmotionInstruction
        | VoiceEngineFeatures.Streaming
        | VoiceEngineFeatures.ContinuousContext
        | VoiceEngineFeatures.RunsOnCpu;

    public Task<VoiceEngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_engine == null || _python == null)
            return Task.FromResult(new VoiceEngineStatus { Error = "not-initialised" });

        var status = _engine.Status();
        if (status.IsReady)
            return Task.FromResult(status);

        // Not ready yet, and the reason the writer can act on is whether the
        // environment has been built at all - a download they have not started
        // is a different thing from a model that failed to load.
        return Task.FromResult(new VoiceEngineStatus
        {
            IsReady = false,
            // Null, not empty: "no fault" and "a fault with no words" are
            // different states, and a host filtering on one was how this engine
            // came to be left out of a list it belonged in.
            Error = NullIfBlank(Explain(_fault ?? status.Error)),
            Detail = status.Detail,
            DownloadBytes = _python.IsBuilt ? null : ApproximateDownloadBytes
        });
    }

    public async Task PrepareAsync(
        IProgress<VoiceEnginePrepare>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_engine == null || _python == null)
            throw new InvalidOperationException("the speech extension is not initialised");

        // The writer is owed something on screen for this. It is minutes on a
        // first run - an environment, a couple of gigabytes of torch, then the
        // models - and a button that goes quiet for that long is indisputably
        // broken however well it is working.
        using var dialog = _host?.ShowBusyProgress(new BusyProgressOptions
        {
            Title = T("speech.preparing", "Preparing the speech engine"),
            InitialStatus = T("speech.step.starting", "Starting"),
            // Starts moving, and switches to a real bar the moment there is a
            // real number. Standing still is the one thing it must not do.
            IsIndeterminate = true,
            AllowCancel = true,
            IsModal = false
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            dialog?.CancellationToken ?? CancellationToken.None);
        var token = linked.Token;

        // Deliberately not Progress<T>: that marshals every report to whatever
        // SynchronizationContext happened to be current when it was built, and
        // if nothing is pumping that context the callbacks never run at all -
        // which is a dialog that says "Starting" for four minutes while the work
        // goes on behind it. Called straight through, a report is a report.
        var report = new Inline<VoiceEnginePrepare>(p =>
        {
            // The detail is what changes minute to minute while pip works; the
            // step alone would sit unchanged long enough to look stuck.
            dialog?.SetStatus(p.Detail.Length > 0
                ? StepName(p.Step) + " — " + p.Detail
                : StepName(p.Step));
            // A fraction where the step has one, a moving bar where it does
            // not. A download whose size nobody knows yet is better shown as
            // "something is happening" than as a bar pinned at 2%.
            if (p.Fraction is { } fraction)
            {
                dialog?.SetIndeterminate(false);
                dialog?.SetProgress(fraction);
            }
            else
            {
                dialog?.SetIndeterminate(true);
            }
            progress?.Report(p);
        });

        _fault = null;
        try
        {
            if (!_python.IsBuilt)
            {
                var failure = await _python.BuildAsync(
                    RequirementsPath(),
                    new Progress<(string Step, double? Fraction, string Detail)>(p =>
                        ((IProgress<VoiceEnginePrepare>)report).Report(new VoiceEnginePrepare
                        {
                            Step = p.Step,
                            Fraction = p.Fraction,
                            Detail = p.Detail
                        })),
                    token);

                if (failure != null)
                {
                    _fault = failure;
                    throw new InvalidOperationException(Explain(failure));
                }
            }

            await _engine.PrepareAsync(report, token);
            if (!_engine.IsReady)
            {
                _fault = _engine.Fault;
                throw new InvalidOperationException(Explain(_engine.Fault));
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled from the dialog. Half an environment is not a fault to
            // report; it is a download the writer changed their mind about, and
            // pressing Prepare again carries on from what is already there.
            _fault = null;
            _engine.Stop();
        }
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that just calls the handler.
    ///
    /// <see cref="Progress{T}"/> posts to the SynchronizationContext captured
    /// when it was constructed. On a background thread with no context that is
    /// the thread pool and works; on one whose context is not being pumped, the
    /// reports are queued and never delivered. Nothing here needs marshalling -
    /// the busy dialog is safe to call from any thread and says so.
    /// </summary>
    private sealed class Inline<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public Inline(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }

    /// <summary>
    /// A step key as words.
    ///
    /// The sidecar reports keys rather than sentences so they can be translated
    /// here; one this does not know is shown as it came, which is better than a
    /// dialog that says nothing while something is plainly happening.
    /// </summary>
    internal string StepName(string step) => step switch
    {
        "starting" => T("speech.step.starting", step),
        "looking-for-python" => T("speech.step.python", step),
        "creating-environment" => T("speech.step.environment", step),
        "downloading" => T("speech.step.downloading", step),
        "downloading-cuda" => T("speech.step.cuda", step),
        "installing" => T("speech.step.installing", step),
        "installed" => T("speech.step.installed", step),
        "importing" => T("speech.step.importing", step),
        "loading-delivery" => T("speech.step.loadingDelivery", step),
        "loading-design" => T("speech.step.loadingDesign", step),
        "ready" => T("speech.step.ready", step),
        _ => step
    };

    /// <summary>
    /// A fault code as something the writer can act on.
    ///
    /// The codes are for the log; a person reading the cast rail is owed a
    /// sentence in their own language saying what to do about it. Anything this
    /// has no words for is passed through rather than swallowed - an untranslated
    /// reason beats no reason.
    /// </summary>
    internal string Explain(string? fault) => fault switch
    {
        null => string.Empty,
        "no-python" => T("speech.noPython", fault),
        "sidecar-exited" or "version" => T("speech.notReady", fault),
        "no-answer" => T("speech.noAnswer", fault),
        _ when fault.StartsWith("venv-failed", StringComparison.Ordinal)
            || fault.StartsWith("install-failed", StringComparison.Ordinal)
            => T("speech.installFailed", fault),
        _ => fault
    };

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>A translated string, or the raw code where no locale is loaded -
    /// which is the case in a unit test and must not be a crash.</summary>
    private string T(string key, string fallback)
    {
        var said = _loc?.T(key);
        return string.IsNullOrWhiteSpace(said) || said == key ? fallback : said;
    }

    public Task<VoiceDesignResult> DesignVoiceAsync(
        VoiceBrief brief, CancellationToken cancellationToken = default)
        => Engine().DesignAsync(brief, cancellationToken);

    public IAsyncEnumerable<NarrationClip> RenderAsync(
        NarrationRequest request, CancellationToken cancellationToken = default)
        => Engine().RenderAsync(request, cancellationToken);

    public Task ForgetVoiceAsync(string voiceId, CancellationToken cancellationToken = default)
        => Engine().ForgetAsync(voiceId, cancellationToken);

    /// <summary>
    /// Roughly what pressing Prepare will fetch, so the number is on screen
    /// before the wait rather than during it. Approximate on purpose: the exact
    /// figure depends on the machine's CUDA build, and a precise-looking number
    /// that is wrong is worse than an honest estimate.
    /// </summary>
    private const long ApproximateDownloadBytes = 6L * 1024 * 1024 * 1024;

    private VoiceEngine Engine()
        => _engine ?? throw new InvalidOperationException("the speech extension is not initialised");

    private string SidecarScript() => Path.Combine(_sidecarDir, "sidecar.py");

    private string RequirementsPath() => Path.Combine(_sidecarDir, "requirements.txt");

    /// <summary>
    /// Writes the sidecar out where it can be run.
    ///
    /// Updated when we ship a new one, left alone when somebody has edited
    /// theirs. Both halves matter and the first was missing: writing only what
    /// was absent meant a machine that had run the extension once kept its
    /// original sidecar for ever, so a fix shipped in a later version reached
    /// everybody except the people who had already hit the bug.
    ///
    /// Told apart by remembering what we wrote. A file whose contents still
    /// match what we last put there is ours to replace; one that does not has
    /// been changed on purpose, and is left exactly as it is. Delete it to be
    /// given ours back.
    /// </summary>
    internal static void Unpack(string directory)
    {
        Directory.CreateDirectory(directory);
        var stampPath = Path.Combine(directory, ".shipped");
        var shipped = ReadStamp(stampPath);
        // Before the stamp existed we always wrote these ourselves and editing
        // was never offered, so a folder without one holds our files rather than
        // somebody's work. Without this an install that predates the stamp keeps
        // its first sidecar for ever - which is the exact bug the stamp was
        // added to end.
        var firstRun = !File.Exists(stampPath);
        var written = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in new[] { "sidecar.py", "requirements.txt" })
        {
            using var source = typeof(SpeechExtension).Assembly.GetManifestResourceStream(name);
            if (source == null)
                continue;

            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var ours = buffer.ToArray();
            var oursHash = Hash(ours);
            written[name] = oursHash;

            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                var theirs = Hash(File.ReadAllBytes(path));
                if (theirs == oursHash)
                    continue;
                // Changed by somebody, rather than merely older than ours.
                if (!firstRun && (!shipped.TryGetValue(name, out var last) || last != theirs))
                    continue;
            }

            File.WriteAllBytes(path, ours);
        }

        WriteStamp(stampPath, written);
    }

    /// <summary>What this wrote last time, by file name.</summary>
    private static Dictionary<string, string> ReadStamp(string path)
    {
        var stamp = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return stamp;

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var at = line.IndexOf(' ');
                if (at > 0)
                    stamp[line[..at]] = line[(at + 1)..];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return stamp;
    }

    private static void WriteStamp(string path, Dictionary<string, string> written)
    {
        try
        {
            File.WriteAllLines(path, written.Select(pair => $"{pair.Key} {pair.Value}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
}
