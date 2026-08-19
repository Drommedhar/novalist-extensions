using System.Runtime.CompilerServices;
using System.Text.Json;
using Novalist.Sdk.Models.Narration;

namespace Novalist.Extensions.Speech;

/// <summary>
/// The speech engine, minus the process.
///
/// Everything that decides anything is here and is tested against a fake
/// channel: what is asked and in what order, what happens when a clip fails,
/// what happens when the sidecar dies part way through a chapter, and what the
/// host is told about all of it. Starting Python is somebody else's problem -
/// <see cref="ProcessSidecarChannel"/>'s - because a test that needs a model on
/// the machine is a test nobody runs.
///
/// The two stages the plan is built on are two different models behind one
/// contributor: a design model that makes a voice from a description, and a
/// delivery model that performs a line in that voice with the emotion supplied
/// separately. The host never learns there are two.
/// </summary>
internal sealed class VoiceEngine : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// How long the sidecar has to say anything at all before it is given up on.
    ///
    /// Applies only until its first word. After that it is trusted for as long
    /// as it likes, because the wait between "loading" and "ready" is a model
    /// download measured in gigabytes and no deadline belongs anywhere near it.
    /// Before it, though, there is nothing but starting an interpreter - so
    /// silence this long means silence for ever, and saying so beats a dialog
    /// that reads "Starting" until the writer kills the app.
    /// </summary>
    private static readonly TimeSpan DefaultFirstWordDeadline = TimeSpan.FromSeconds(120);

    private readonly Func<ISidecarChannel> _channels;
    private readonly string _workingDirectory;
    private readonly TimeSpan _firstWord;
    private ISidecarChannel? _channel;
    private int _requests;
    private string _detail = string.Empty;
    private string? _fault;

    /// <param name="firstWord">How long the sidecar has to say anything at all.
    /// Injected so a test can assert the give-up without waiting two minutes for
    /// it.</param>
    public VoiceEngine(
        Func<ISidecarChannel> channels, string workingDirectory, TimeSpan? firstWord = null)
    {
        _channels = channels;
        _workingDirectory = workingDirectory;
        _firstWord = firstWord ?? DefaultFirstWordDeadline;
    }

    /// <summary>True once the sidecar has answered that its models are loaded.</summary>
    public bool IsReady { get; private set; }

    /// <summary>What the sidecar says it is - the models and the device.</summary>
    public string Detail => _detail;

    /// <summary>Why it cannot run, when it cannot.</summary>
    public string? Fault => _fault;

    /// <summary>
    /// Starts the sidecar and waits for it to say it is ready.
    ///
    /// The wait is the point: loading a speech model takes tens of seconds, and
    /// a host that thought the engine was ready the moment the process existed
    /// would send it a chapter and get nothing back.
    /// </summary>
    public async Task PrepareAsync(
        IProgress<VoiceEnginePrepare>? progress,
        CancellationToken cancellationToken = default)
    {
        if (IsReady && _channel is { IsRunning: true })
            return;

        Stop();
        _fault = null;
        Directory.CreateDirectory(_workingDirectory);

        var channel = _channels();
        _channel = channel;
        await channel.StartAsync(cancellationToken);
        // No fraction: nobody knows how long starting Python and loading a
        // speech model takes, and a bar reporting zero per cent is a bar that
        // has stopped. A moving one says the true thing - something is
        // happening and it is not measurable.
        progress?.Report(new VoiceEnginePrepare { Step = "starting" });

        var id = NextId();
        await SendAsync(new SidecarRequest { Op = "status", Id = id }, cancellationToken);

        // Armed only for the first reply; cancelled the moment one arrives.
        using var mute = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        mute.CancelAfter(_firstWord);
        var heard = false;

        while (true)
        {
            SidecarReply? reply;
            try
            {
                reply = await ReadForAsync(id, heard ? cancellationToken : mute.Token);
            }
            catch (OperationCanceledException) when (!heard && !cancellationToken.IsCancellationRequested)
            {
                _fault = "no-answer";
                Stop();
                return;
            }

            if (reply == null)
                break;
            heard = true;

            switch (reply.Type)
            {
                case "progress":
                    progress?.Report(new VoiceEnginePrepare
                    {
                        Step = reply.Step,
                        Fraction = reply.Fraction,
                        Detail = reply.Detail
                    });
                    continue;
                case "ready":
                    if (reply.Version != SidecarProtocol.Version)
                    {
                        _fault = "version";
                        Stop();
                        return;
                    }
                    IsReady = reply.Ready;
                    _detail = reply.Detail;
                    if (!reply.Ready)
                        _fault = reply.Error ?? "not-ready";
                    progress?.Report(new VoiceEnginePrepare { Step = "ready", Fraction = 1 });
                    return;
                case "error":
                    _fault = reply.Error ?? "error";
                    Stop();
                    return;
                default:
                    continue;
            }
        }

        // The output closed without an answer: the sidecar died on the way up,
        // usually because Python or a dependency is not there.
        _fault = "sidecar-exited";
        Stop();
    }

    /// <summary>The engine's state, without starting anything.</summary>
    public VoiceEngineStatus Status() => new()
    {
        IsReady = IsReady && _channel is { IsRunning: true },
        Error = _fault,
        Detail = _detail
    };

    /// <summary>
    /// Designs a voice and returns the audio that is that voice from now on.
    /// </summary>
    public async Task<VoiceDesignResult> DesignAsync(
        VoiceBrief brief, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);

        var id = NextId();
        await SendAsync(new SidecarRequest
        {
            Op = "design",
            Id = id,
            VoiceId = brief.VoiceId,
            Description = brief.Description,
            SampleLines = [.. brief.SampleLines],
            Language = brief.Language,
            Seed = brief.Seed
        }, cancellationToken);

        while (await ReadForAsync(id, cancellationToken) is { } reply)
        {
            if (reply.Type == "progress")
                continue;
            if (reply.Type == "error")
                throw new InvalidOperationException(reply.Error ?? "design-failed");
            if (reply.Type != "designed" || reply.File == null)
                continue;

            var audio = await ReadClipAsync(reply.File, cancellationToken);
            return new VoiceDesignResult
            {
                VoiceId = brief.VoiceId,
                ReferenceAudio = audio,
                AudioFormat = "wav",
                SampleRate = reply.SampleRate,
                ResolvedDescription = brief.Description,
                Seed = reply.Seed
            };
        }

        // A code rather than a sentence: the host puts this in front of the
        // writer, and "InvalidOperationException" told nobody anything. The
        // sidecar reports its own failures as the exception's type name, so
        // whatever arrives here is already safe to show.
        throw new InvalidOperationException("sidecar-exited-while-designing");
    }

    /// <summary>
    /// Speaks a run of the book, yielding each clip as it is finished.
    ///
    /// Yielded rather than collected, so the host can start playing the first
    /// line while the rest is still being made. A segment the sidecar could not
    /// speak comes back carrying its reason rather than being dropped: a reading
    /// with a silent gap in it sounds like the feature is broken.
    /// </summary>
    public async IAsyncEnumerable<NarrationClip> RenderAsync(
        NarrationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);

        // The reference audio goes to disk once per render rather than into the
        // message. The sidecar is told where, not what.
        var voices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (voiceId, audio) in request.Voices)
        {
            var name = $"voice-{Sanitise(voiceId)}.wav";
            await File.WriteAllBytesAsync(
                Path.Combine(_workingDirectory, name), audio, cancellationToken);
            voices[voiceId] = name;
        }

        // The clips the writer pointed at and said "like that". Written once
        // each however many lines share one, and to a name derived from the
        // audio rather than from the segment - two lines told to sound like the
        // same clip are the same file.
        var like = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in request.Segments)
        {
            if (segment.Direction.ReferenceAudio is not { Length: > 0 } audio)
                continue;
            var name = "like-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(audio))[..16] + ".wav";
            if (!like.ContainsKey(segment.Key))
                like[segment.Key] = name;
            var path = Path.Combine(_workingDirectory, name);
            if (!File.Exists(path))
                await File.WriteAllBytesAsync(path, audio, cancellationToken);
        }

        var id = NextId();
        await SendAsync(new SidecarRequest
        {
            Op = "render",
            Id = id,
            Language = request.Language,
            Rate = request.Rate,
            Voices = voices,
            Segments = [.. request.Segments.Select(s => new SidecarSegment
            {
                Key = s.Key,
                Text = s.Text,
                VoiceId = s.VoiceId,
                IsDialogue = s.IsDialogue,
                Vector = new Dictionary<string, double>(s.Direction.Vector, StringComparer.Ordinal),
                Instruction = s.Direction.Instruction,
                Emotion = s.Direction.Key,
                LikeThis = like.GetValueOrDefault(s.Key)
            })]
        }, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            var reply = await ReadForAsync(id, cancellationToken);
            if (reply == null)
            {
                // The sidecar died mid-chapter. The host stops at the last good
                // clip; saying so beats the reading simply ending.
                yield return new NarrationClip { Key = string.Empty, Error = "sidecar-exited" };
                IsReady = false;
                yield break;
            }

            if (reply.Type == "done")
                yield break;
            if (reply.Type == "progress")
                continue;
            if (reply.Type == "error")
            {
                yield return new NarrationClip { Key = reply.Key, Error = reply.Error ?? "render" };
                continue;
            }
            if (reply.Type != "clip" || reply.File == null)
                continue;

            yield return new NarrationClip
            {
                Key = reply.Key,
                Audio = await ReadClipAsync(reply.File, cancellationToken),
                AudioFormat = "wav",
                SampleRate = reply.SampleRate,
                DurationMs = reply.DurationMs
            };
        }
    }

    /// <summary>
    /// Drops a voice's reference audio from the working directory.
    ///
    /// Nothing to tell the sidecar. It holds no state about a voice between
    /// calls - the reference clip is handed to it with every render - so there
    /// is nothing there to go stale.
    /// </summary>
    public Task ForgetAsync(string voiceId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_workingDirectory, $"voice-{Sanitise(voiceId)}.wav");
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        return Task.CompletedTask;
    }

    public void Stop()
    {
        IsReady = false;
        _channel?.Dispose();
        _channel = null;
    }

    public void Dispose() => Stop();

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!IsReady || _channel is not { IsRunning: true })
            await PrepareAsync(null, cancellationToken);
        if (!IsReady)
            throw new InvalidOperationException(_fault ?? "engine-not-ready");
    }

    private async Task SendAsync(SidecarRequest request, CancellationToken cancellationToken)
    {
        if (_channel == null)
            throw new InvalidOperationException("engine-not-running");
        await _channel.SendAsync(JsonSerializer.Serialize(request, Json), cancellationToken);
    }

    /// <summary>The next id, unique for the life of this engine.</summary>
    private string NextId() => (++_requests).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The next reply that belongs to <paramref name="id"/>.
    ///
    /// Anything stamped with another request is a leftover from a reading that
    /// was stopped, still arriving because the sidecar cannot be interrupted
    /// inside the model. It is dropped here rather than at the call sites, so
    /// no caller can forget - and a clip from it is deleted rather than left to
    /// fill the working directory for the rest of the session.
    /// </summary>
    private async Task<SidecarReply?> ReadForAsync(string id, CancellationToken cancellationToken)
    {
        while (await ReadAsync(cancellationToken) is { } reply)
        {
            if (reply.Id.Length == 0 || reply.Id == id)
                return reply;
            Discard(reply);
        }
        return null;
    }

    /// <summary>Throws away a clip nobody asked for any more.</summary>
    private void Discard(SidecarReply reply)
    {
        if (reply.File == null)
            return;
        try
        {
            var path = Path.Combine(_workingDirectory, Path.GetFileName(reply.File));
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<SidecarReply?> ReadAsync(CancellationToken cancellationToken)
    {
        while (_channel != null)
        {
            var line = await _channel.ReadAsync(cancellationToken);
            if (line == null)
                return null;
            if (line.Length == 0 || line[0] != '{')
                continue;

            SidecarReply? reply;
            try
            {
                reply = JsonSerializer.Deserialize<SidecarReply>(line, Json);
            }
            catch (JsonException)
            {
                // A model that printed to stdout rather than stderr. Skipped
                // rather than fatal: it is noise, not an answer.
                continue;
            }
            if (reply != null)
                return reply;
        }
        return null;
    }

    /// <summary>The bytes of a clip the sidecar wrote, then the file is gone -
    /// the host has its own cache and this directory is scratch.</summary>
    private async Task<byte[]> ReadClipAsync(string file, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_workingDirectory, Path.GetFileName(file));
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        return bytes;
    }

    /// <summary>A voice id as a file name. Ids are host-made, but a file name
    /// built from something another component chose is worth checking.</summary>
    private static string Sanitise(string voiceId)
        => new([.. voiceId.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_')]);
}
