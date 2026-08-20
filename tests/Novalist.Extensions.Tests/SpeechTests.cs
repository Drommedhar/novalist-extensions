using System.Runtime.InteropServices;
using System.Text.Json;
using Novalist.Extensions.Speech;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Models;
using Novalist.Sdk.Models.Narration;
using Xunit;

namespace Novalist.Extensions.Tests;

/// <summary>
/// Covers the speech engine without a model on the machine.
///
/// Everything that decides anything is in <c>VoiceEngine</c>: what is asked and
/// in what order, what the sidecar is told about the voice, what happens when
/// a line fails and what happens when the sidecar dies part way through a
/// chapter. A test that needed torch and six gigabytes of weights is a test
/// nobody runs, so the process is a fake and the decisions are real.
/// </summary>
public sealed class SpeechTests : IDisposable
{
    private readonly string _work = Path.Combine(
        Path.GetTempPath(), "nl-speech-" + Guid.NewGuid().ToString("N"));

    public SpeechTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    /// <summary>A sidecar that says whatever the test wants, in order.</summary>
    private sealed class FakeChannel : ISidecarChannel
    {
        private readonly Queue<string> _replies;

        public FakeChannel(IEnumerable<string> replies) => _replies = new(replies);

        public List<string> Sent { get; } = [];
        public bool IsRunning { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(string line, CancellationToken cancellationToken = default)
        {
            Sent.Add(line);
            return Task.CompletedTask;
        }

        public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : null);

        public void Stop()
        {
            IsRunning = false;
            Stopped = true;
        }

        public void Dispose() => Stop();
    }

    /// <summary>A sidecar that starts and then says nothing, ever.</summary>
    private sealed class SilentChannel : ISidecarChannel
    {
        public bool IsRunning { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(string line, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // Never answers, and never returns of its own accord - which is exactly
        // what a sidecar that dropped the request does. Cancelling throws, the
        // way a real cancelled read off the process's output does; returning
        // null instead would be the fake claiming the process had closed.
        public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }

        public void Stop()
        {
            IsRunning = false;
            Stopped = true;
        }

        public void Dispose() => Stop();
    }

    /// <summary>The protocol the sidecar that ships speaks. A reply carrying
    /// any other number is a stale sidecar and is refused rather than used.</summary>
    private const int SidecarProtocolVersion = 3;

    private VoiceEngine Engine(params string[] replies)
        => new(() => new FakeChannel(replies), _work);

    private VoiceEngine Engine(FakeChannel channel) => new(() => channel, _work);

    private static string Ready(bool ready = true, int version = SidecarProtocolVersion) => JsonSerializer.Serialize(
        new { type = "ready", version, ready, detail = "Qwen3-TTS VoiceDesign + Base on cuda" });

    /// <summary>Writes a clip where the sidecar would have, and names it.</summary>
    private string Clip(string name, string key, double durationMs = 250)
    {
        File.WriteAllBytes(Path.Combine(_work, name), [0x52, 0x49, 0x46, 0x46, 1, 2, 3]);
        return JsonSerializer.Serialize(
            new { type = "clip", key, file = name, sampleRate = 24000, durationMs });
    }

    // ── Preparing ──

    [Fact]
    public async Task Prepare_WaitsForTheSidecarToSayItsModelsAreLoaded()
    {
        // The wait is the point: a host that thought the engine was ready the
        // moment the process existed would send it a chapter and get nothing.
        var engine = Engine(
            JsonSerializer.Serialize(new { type = "progress", step = "loading-model", fraction = 0.7 }),
            Ready());
        var steps = new List<string>();

        await engine.PrepareAsync(new Progress<VoiceEnginePrepare>(p => steps.Add(p.Step)));

        Assert.True(engine.IsReady);
        Assert.Equal("Qwen3-TTS VoiceDesign + Base on cuda", engine.Detail);
        Assert.Contains("loading-model", steps);
        Assert.Contains("ready", steps);
    }

    [Fact]
    public async Task Prepare_ASidecarThatDiesOnTheWayUpIsSaidSoRatherThanWaitedOn()
    {
        // No replies at all: the process closed its output, which is what a
        // missing dependency looks like from here.
        var engine = Engine();

        await engine.PrepareAsync(null);

        Assert.False(engine.IsReady);
        Assert.Equal("sidecar-exited", engine.Fault);
    }

    [Fact]
    public async Task Prepare_ASidecarSpeakingAnotherProtocolIsRefused()
    {
        // Rather than letting fields go quietly missing later.
        var engine = Engine(Ready(version: 99));

        await engine.PrepareAsync(null);

        Assert.False(engine.IsReady);
        Assert.Equal("version", engine.Fault);
    }

    [Fact]
    public async Task Prepare_AnErrorOnTheWayUpIsKeptAsTheReason()
    {
        var engine = Engine(JsonSerializer.Serialize(new { type = "error", error = "OutOfMemory" }));

        await engine.PrepareAsync(null);

        Assert.False(engine.IsReady);
        Assert.Equal("OutOfMemory", engine.Fault);
    }

    [Fact]
    public async Task Prepare_ASidecarThatSaysItIsNotReadyIsBelieved()
    {
        var engine = Engine(Ready(ready: false));

        await engine.PrepareAsync(null);

        Assert.False(engine.IsReady);
        Assert.NotNull(engine.Fault);
    }

    [Fact]
    public async Task Status_ReportsWhatTheSidecarSaidAboutItself()
    {
        var engine = Engine(Ready());
        await engine.PrepareAsync(null);

        var status = engine.Status();

        Assert.True(status.IsReady);
        Assert.Equal("Qwen3-TTS VoiceDesign + Base on cuda", status.Detail);
        Assert.Null(status.Error);
    }

    // ── Designing ──

    [Fact]
    public async Task Design_AsksForTheInstrumentAndReturnsTheAudioAsTheVoice()
    {
        File.WriteAllBytes(Path.Combine(_work, "design-1.wav"), [1, 2, 3, 4]);
        var channel = new FakeChannel([
            Ready(),
            JsonSerializer.Serialize(
                new
                {
                    type = "designed", key = "mira", file = "design-1.wav",
                    text = "You are late,", sampleRate = 24000
                })
        ]);
        var engine = Engine(channel);
        await engine.PrepareAsync(null);

        var designed = await engine.DesignAsync(new VoiceBrief
        {
            VoiceId = "mira",
            DisplayName = "Mira",
            Description = "Age: 34. Build: wiry.",
            SampleLines = ["You are late,"],
            Language = "en"
        });

        Assert.Equal("mira", designed.VoiceId);
        Assert.Equal([1, 2, 3, 4], designed.ReferenceAudio);
        Assert.Equal("You are late,", designed.ReferenceText);
        Assert.Equal(24000, designed.SampleRate);
        // The brief went across as the instrument, with no emotion in it - the
        // host stripped that before it ever reached here.
        var asked = channel.Sent.Last();
        Assert.Contains("\"op\":\"design\"", asked);
        Assert.Contains("wiry", asked);
        Assert.DoesNotContain("sampleLines", asked);
        // And the scratch file is gone: the host has its own store.
        Assert.False(File.Exists(Path.Combine(_work, "design-1.wav")));
    }

    [Fact]
    public async Task Design_ASidecarThatFailsSaysWhyRatherThanReturningSilence()
    {
        var engine = Engine(
            Ready(), JsonSerializer.Serialize(new { type = "error", error = "OutOfMemory" }));
        await engine.PrepareAsync(null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.DesignAsync(new VoiceBrief { VoiceId = "mira" }));
    }

    [Fact]
    public async Task Design_ASidecarThatStopsMidWayIsNotWaitedOnForEver()
    {
        var engine = Engine(Ready());
        await engine.PrepareAsync(null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.DesignAsync(new VoiceBrief { VoiceId = "mira" }));
    }

    // ── Rendering ──

    [Fact]
    public async Task Render_SendsPlainProseAndTheExactReferenceTranscript()
    {
        var channel = new FakeChannel([
            Ready(),
            Clip("clip-0.wav", "d:1"),
            JsonSerializer.Serialize(new { type = "done" })
        ]);
        var engine = Engine(channel);
        await engine.PrepareAsync(null);

        var clips = new List<NarrationClip>();
        await foreach (var clip in engine.RenderAsync(Request()))
            clips.Add(clip);

        var asked = JsonDocument.Parse(channel.Sent.Last()).RootElement;
        var segment = asked.GetProperty("segments")[0];
        Assert.Equal("You are late,", segment.GetProperty("text").GetString());
        Assert.False(segment.TryGetProperty("emotion", out _));
        Assert.False(segment.TryGetProperty("vector", out _));
        Assert.False(segment.TryGetProperty("instruction", out _));
        Assert.Equal(
            "This is the exact reference.",
            asked.GetProperty("voiceTexts").GetProperty("mira-voice").GetString());

        var only = Assert.Single(clips);
        Assert.Equal("d:1", only.Key);
        Assert.NotEmpty(only.Audio);
        Assert.Equal(250, only.DurationMs);
    }

    [Fact]
    public async Task Render_WritesEachVoicesReferenceWhereTheSidecarCanReadIt()
    {
        // Not into the message: a chapter of reference audio through a JSON
        // parser is the wrong transport twice over.
        var channel = new FakeChannel([Ready(), JsonSerializer.Serialize(new { type = "done" })]);
        var engine = Engine(channel);
        await engine.PrepareAsync(null);

        await foreach (var _ in engine.RenderAsync(Request())) { }

        Assert.True(File.Exists(Path.Combine(_work, "voice-mira-voice.wav")));
        var asked = JsonDocument.Parse(channel.Sent.Last()).RootElement;
        Assert.Equal(
            "voice-mira-voice.wav",
            asked.GetProperty("voices").GetProperty("mira-voice").GetString());
    }

    [Fact]
    public async Task Render_ALineTheSidecarCouldNotSpeakComesBackCarryingItsReason()
    {
        // Rather than being dropped: a reading with a silent gap in it sounds
        // like the feature is broken.
        var engine = Engine(
            Ready(),
            JsonSerializer.Serialize(new { type = "error", key = "d:1", error = "unknown voice" }),
            JsonSerializer.Serialize(new { type = "done" }));
        await engine.PrepareAsync(null);

        var clips = new List<NarrationClip>();
        await foreach (var clip in engine.RenderAsync(Request()))
            clips.Add(clip);

        var only = Assert.Single(clips);
        Assert.Equal("d:1", only.Key);
        Assert.Equal("unknown voice", only.Error);
    }

    [Fact]
    public async Task Render_ASidecarThatDiesMidChapterSaysSoAndStopsBeingReady()
    {
        var engine = Engine(Ready(), Clip("clip-0.wav", "d:1"));
        await engine.PrepareAsync(null);

        var clips = new List<NarrationClip>();
        await foreach (var clip in engine.RenderAsync(Request()))
            clips.Add(clip);

        Assert.Equal(2, clips.Count);
        Assert.Equal("sidecar-exited", clips[1].Error);
        Assert.False(engine.IsReady);
    }

    [Fact]
    public async Task Render_NoiseOnTheOutputIsSkippedRatherThanFatal()
    {
        // A model that printed a progress bar to stdout instead of stderr.
        var engine = Engine(
            Ready(),
            "Loading checkpoint shards:  50%|#####     |",
            "{ not json",
            Clip("clip-0.wav", "d:1"),
            JsonSerializer.Serialize(new { type = "done" }));
        await engine.PrepareAsync(null);

        var clips = new List<NarrationClip>();
        await foreach (var clip in engine.RenderAsync(Request()))
            clips.Add(clip);

        Assert.Single(clips);
        Assert.Null(clips[0].Error);
    }

    [Fact]
    public async Task Render_StartsTheSidecarWhenNobodyPreparedItFirst()
    {
        var engine = Engine(
            Ready(), Clip("clip-0.wav", "d:1"), JsonSerializer.Serialize(new { type = "done" }));

        var clips = new List<NarrationClip>();
        await foreach (var clip in engine.RenderAsync(Request()))
            clips.Add(clip);

        Assert.Single(clips);
    }

    [Fact]
    public async Task Render_WithAnEngineThatWillNotStartFailsRatherThanHangs()
    {
        var engine = Engine();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in engine.RenderAsync(Request())) { }
        });
    }

    [Fact]
    public async Task Forget_DropsTheVoicesReferenceAudio()
    {
        var engine = Engine(Ready(), JsonSerializer.Serialize(new { type = "done" }));
        await engine.PrepareAsync(null);
        await foreach (var _ in engine.RenderAsync(Request())) { }
        Assert.True(File.Exists(Path.Combine(_work, "voice-mira-voice.wav")));

        await engine.ForgetAsync("mira-voice");

        Assert.False(File.Exists(Path.Combine(_work, "voice-mira-voice.wav")));
    }

    [Fact]
    public async Task Forget_AVoiceThatIsNotThereIsNotAFailure()
    {
        var engine = Engine(Ready());
        await engine.PrepareAsync(null);

        await engine.ForgetAsync("never-designed");
    }

    [Fact]
    public async Task Stop_ShutsTheSidecarDown()
    {
        var channel = new FakeChannel([Ready()]);
        var engine = Engine(channel);
        await engine.PrepareAsync(null);

        engine.Stop();

        Assert.True(channel.Stopped);
        Assert.False(engine.IsReady);
    }

    // ── What the extension itself advertises ──

    [Fact]
    public void TheEnvironment_IsRebuiltWhenTheRecipeChanges()
    {
        // A release that changes the packages used to count as already
        // installed, because the marker recorded only that something once had
        // been. So the new sidecar ran against the old environment and failed on
        // an import the writer could do nothing about: as far as the extension
        // was concerned there was nothing left to install.
        var root = Path.Combine(_work, "env");
        var requirements = Path.Combine(_work, "requirements.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(
            Path.Combine(root, OperatingSystem.IsWindows() ? "Scripts" : "bin", "x"))!);
        File.WriteAllText(Path.Combine(root, "installed.txt"), "whatever was there before");
        File.WriteAllText(requirements, "qwen-tts==0.1.0\n");

        var python = new PythonEnvironment(root);
        // Pretend the interpreter is there; what is being tested is the verdict,
        // not the venv.
        var interpreter = python.VenvPython;
        Directory.CreateDirectory(Path.GetDirectoryName(interpreter)!);
        File.WriteAllText(interpreter, string.Empty);

        Assert.False(python.IsBuiltFor(requirements));
    }

    [Fact]
    public void TheEnvironment_IsLeftAloneWhenTheRecipeHasNotChanged()
    {
        var root = Path.Combine(_work, "env2");
        var requirements = Path.Combine(_work, "requirements2.txt");
        Directory.CreateDirectory(root);
        File.WriteAllText(requirements, "qwen-tts==0.1.1\nnumpy\n");

        var python = new PythonEnvironment(root);
        var interpreter = python.VenvPython;
        Directory.CreateDirectory(Path.GetDirectoryName(interpreter)!);
        File.WriteAllText(interpreter, string.Empty);
        File.WriteAllText(
            Path.Combine(root, "installed.txt"),
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(requirements))));

        Assert.True(python.IsBuiltFor(requirements));

        // And an edit to the file is a different recipe, so somebody who changed
        // theirs for their own card gets it installed rather than ignored.
        File.WriteAllText(requirements, "qwen-tts==0.1.1\nnumpy\ntorch==2.9.1\n");
        Assert.False(python.IsBuiltFor(requirements));
    }

    [Fact]
    public void TheEnvironment_WithNoRequirementsFileIsNotBuilt()
    {
        var root = Path.Combine(_work, "env3");
        Directory.CreateDirectory(root);
        var python = new PythonEnvironment(root);
        var interpreter = python.VenvPython;
        Directory.CreateDirectory(Path.GetDirectoryName(interpreter)!);
        File.WriteAllText(interpreter, string.Empty);
        File.WriteAllText(Path.Combine(root, "installed.txt"), string.Empty);

        Assert.False(python.IsBuiltFor(Path.Combine(_work, "not-there.txt")));
    }

    [Fact]
    public void TheSidecar_IsWrittenOutRatherThanLookedFor()
    {
        // The host loads extension assemblies from memory so no file lock is
        // held, which leaves Assembly.Location empty - so an extension that
        // builds a path from it hands pip a relative path rather than a real
        // one. Embedded, there is nothing to find.
        var directory = Path.Combine(_work, "unpacked");

        SpeechExtension.Unpack(directory);

        Assert.True(File.Exists(Path.Combine(directory, "sidecar.py")));
        Assert.True(File.Exists(Path.Combine(directory, "requirements.txt")));
        Assert.Contains(
            "qwen-tts==0.1.1", File.ReadAllText(Path.Combine(directory, "requirements.txt")));
        Assert.Contains(
            "PROTOCOL_VERSION", File.ReadAllText(Path.Combine(directory, "sidecar.py")));
    }

    [Fact]
    public void TheSidecar_LeavesAnEditedCopyAlone()
    {
        // Somebody's change for their own card is not ours to throw away on the
        // next start. Deleting the file is how you ask for ours back.
        var directory = Path.Combine(_work, "edited");
        SpeechExtension.Unpack(directory);
        File.WriteAllText(Path.Combine(directory, "sidecar.py"), "# mine");

        SpeechExtension.Unpack(directory);

        Assert.Equal("# mine", File.ReadAllText(Path.Combine(directory, "sidecar.py")));
    }

    [Fact]
    public void TheSidecar_ReplacesAnOldCopyNobodyTouched()
    {
        // The half that was missing. Writing only what was absent meant a
        // machine that had run this once kept its first sidecar for ever - so a
        // fix shipped later reached everybody except the people who had already
        // hit the bug it fixed.
        var directory = Path.Combine(_work, "stale");
        SpeechExtension.Unpack(directory);
        var ours = File.ReadAllText(Path.Combine(directory, "sidecar.py"));
        // An older version of ours: written by us, then superseded.
        File.WriteAllText(Path.Combine(directory, "sidecar.py"), "# an older one");
        File.WriteAllLines(
            Path.Combine(directory, ".shipped"),
            ["sidecar.py " + Sha(Path.Combine(directory, "sidecar.py"))]);

        SpeechExtension.Unpack(directory);

        Assert.Equal(ours, File.ReadAllText(Path.Combine(directory, "sidecar.py")));
    }

    [Fact]
    public void TheSidecar_UpdatesAnInstallFromBeforeThereWasAStamp()
    {
        // The machines that most need a fix are the ones that already ran the
        // broken version, and they are exactly the ones with no stamp.
        var directory = Path.Combine(_work, "premarker");
        SpeechExtension.Unpack(directory);
        var ours = File.ReadAllText(Path.Combine(directory, "sidecar.py"));
        File.WriteAllText(Path.Combine(directory, "sidecar.py"), "# an old one");
        File.Delete(Path.Combine(directory, ".shipped"));

        SpeechExtension.Unpack(directory);

        Assert.Equal(ours, File.ReadAllText(Path.Combine(directory, "sidecar.py")));
    }

    private static string Sha(string path)
        => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void TheExtension_SaysWhatItCanAndCannotDo()
    {
        var extension = new SpeechExtension();

        Assert.Equal("com.novalist.speech", extension.Id);
        Assert.True(extension.Features.HasFlag(VoiceEngineFeatures.DesignFromDescription));
        Assert.True(extension.Features.HasFlag(VoiceEngineFeatures.EmotionInferred));
        Assert.True(extension.Features.HasFlag(VoiceEngineFeatures.Streaming));
        Assert.True(extension.Features.HasFlag(VoiceEngineFeatures.RunsOnCpu));
        // Base clones the reference the design checkpoint created internally,
        // but the extension deliberately offers no user-recording clone path.
        Assert.False(extension.Features.HasFlag(VoiceEngineFeatures.CloneFromSample));
        Assert.False(extension.Features.HasFlag(VoiceEngineFeatures.EmotionVector));
        Assert.False(extension.Features.HasFlag(VoiceEngineFeatures.EmotionInstruction));
        Assert.False(extension.Features.HasFlag(VoiceEngineFeatures.EmotionReference));
        // Several sentences are grouped into each call, but state is not carried
        // from one model call to the next, so the host must still smooth joins.
        Assert.False(extension.Features.HasFlag(VoiceEngineFeatures.ContinuousContext));
    }

    [Fact]
    public void TheExtension_OffersAnOptionalMaskedHuggingFaceToken()
    {
        var settings = Assert.IsAssignableFrom<ISettingsSchemaContributor>(new SpeechExtension());

        var field = Assert.Single(settings.GetSettingsSchema().Fields);

        Assert.Equal("huggingFaceToken", field.Key);
        Assert.Equal(SettingsFieldType.Password, field.Type);
        Assert.Equal(string.Empty, field.Value);
        Assert.Contains("rate", field.Help!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AHubToken_IsTrimmedWithoutEverBeingParsedOrLogged()
        => Assert.Equal("hf_secret", SpeechExtension.NormalizeHubToken("  hf_secret\r\n"));

    [Theory]
    [InlineData("no-python")]
    [InlineData("sidecar-exited")]
    [InlineData("version")]
    [InlineData("venv-failed: pip said no")]
    [InlineData("install-failed: no wheel")]
    public void TheExtension_TurnsAFaultCodeIntoSomethingAWriterCanActOn(string fault)
    {
        // With no locale loaded the code passes through rather than being
        // swallowed: an untranslated reason beats no reason.
        Assert.False(string.IsNullOrWhiteSpace(new SpeechExtension().Explain(fault)));
    }

    [Fact]
    public void TheExtension_HasNothingToExplainWhenNothingIsWrong()
        => Assert.Equal(string.Empty, new SpeechExtension().Explain(null));

    [Fact]
    public void TheExtension_PassesThroughAFaultItHasNoWordsFor()
        => Assert.Equal("OutOfMemory", new SpeechExtension().Explain("OutOfMemory"));

    [Theory]
    [InlineData("starting")]
    [InlineData("creating-environment")]
    [InlineData("downloading")]
    [InlineData("installed")]
    [InlineData("importing")]
    [InlineData("downloading-model")]
    [InlineData("installing")]
    [InlineData("looking-for-python")]
    [InlineData("loading-model")]
    [InlineData("ready")]
    public void TheExtension_HasWordsForEveryStepItReports(string step)
    {
        // A dialog that says nothing while something is plainly happening is the
        // thing this exists to prevent.
        Assert.False(string.IsNullOrWhiteSpace(new SpeechExtension().StepName(step)));
    }

    [Fact]
    public void TheExtension_ShowsAStepItDoesNotKnowRatherThanNothing()
        => Assert.Equal("some-new-step", new SpeechExtension().StepName("some-new-step"));

    [Theory]
    // What pip actually writes while it works. The download bar is the one that
    // matters: it is rewritten in place for minutes at a time and is the only
    // thing moving during the longest part of the wait.
    [InlineData("Collecting torch", "Collecting torch", null)]
    [InlineData("  Downloading torch-2.6.0-cp312-win_amd64.whl (2.5 GB)",
        "Downloading torch-2.6.0-cp312-win_amd64.whl (2.5 GB)", null)]
    [InlineData("   ---------------- 1.2/2.5 GB 45.3 MB/s eta 0:00:29", null, null)]
    [InlineData("  Downloading torch.whl (2.5 GB)  47%", "Downloading torch.whl (2.5 GB)  47%", 0.47)]
    [InlineData("Installing collected packages: torch", "Installing collected packages: torch", null)]
    [InlineData("Using cached numpy-1.26.4.whl", "Using cached numpy-1.26.4.whl", null)]
    // pip explains where a dependency came from with the whole requirements
    // path. The package is what changes and what anybody reads.
    [InlineData("Collecting torch==2.6.0 (from qwen-tts==0.1.1->-r C:/Users/x/reqs.txt (line 12))",
        "Collecting torch==2.6.0", null)]
    // Noise nobody needs to read.
    [InlineData("Requirement already satisfied: idna", null, null)]
    [InlineData("", null, null)]
    [InlineData("   ", null, null)]
    public void PipOutput_IsFilteredToWhatChanges(string line, string? text, double? fraction)
    {
        var said = PythonEnvironment.Interesting(line);

        if (text == null)
        {
            Assert.Null(said);
            return;
        }
        Assert.NotNull(said);
        Assert.Equal(text, said!.Value.Text);
        Assert.Equal(fraction, said.Value.Fraction);
    }

    [Theory]
    // The versions the speech stack has wheels for.
    [InlineData("Python 3.10.14", true)]
    [InlineData("Python 3.12.7", true)]
    [InlineData("Python 3.13.1", true)]
    // Not declared supported by the pinned Qwen stack yet.
    [InlineData("Python 3.14.6", false)]
    [InlineData("Python 3.15.0", false)]
    // And too old to be worth trying.
    [InlineData("Python 3.8.10", false)]
    [InlineData("Python 2.7.18", false)]
    [InlineData("", false)]
    [InlineData("not a version at all", false)]
    public void APythonIsUsableOnlyWhereTheStackHasWheels(string version, bool usable)
        => Assert.Equal(usable, PythonEnvironment.IsUsable(version));

    [Theory]
    // The triples the uv release actually publishes under. Getting one of them
    // wrong is not a loud failure - it is a 404 that reads as "no interpreter
    // available" on a machine that could have had one.
    [InlineData(Architecture.X64, true, false, "uv-x86_64-pc-windows-msvc.zip")]
    [InlineData(Architecture.Arm64, true, false, "uv-aarch64-pc-windows-msvc.zip")]
    [InlineData(Architecture.X64, false, true, "uv-x86_64-apple-darwin.tar.gz")]
    [InlineData(Architecture.Arm64, false, true, "uv-aarch64-apple-darwin.tar.gz")]
    [InlineData(Architecture.X64, false, false, "uv-x86_64-unknown-linux-gnu.tar.gz")]
    [InlineData(Architecture.Arm64, false, false, "uv-aarch64-unknown-linux-gnu.tar.gz")]
    // A machine nobody publishes for. Saying so beats downloading a 404 and
    // reporting it as a corrupt archive.
    [InlineData(Architecture.X86, true, false, null)]
    public void TheInterpreterFetched_IsTheOneBuiltForThisMachine(
        Architecture architecture, bool windows, bool mac, string? expected)
        => Assert.Equal(expected, PortablePython.AssetName(architecture, windows, mac));

    [Fact]
    public void EveryStepAndEveryFault_HasASentenceInEveryLanguageWeShip()
    {
        // The failure this catches is silent and permanent: a step or a fault
        // gains a key, nobody adds the string, and the writer is shown a bare
        // code - "python-fetch-failed" - at the one moment they most need words.
        // Without a locale loaded the extension answers with the code itself, so
        // this reads the files rather than the extension.
        var locales = LocalesDir();
        var keys = new[]
        {
            "speech.preparing", "speech.noPython", "speech.pythonFetchFailed",
            "speech.installFailed", "speech.notReady", "speech.noAnswer",
            "speech.step.starting", "speech.step.python", "speech.step.fetchingPython",
            "speech.step.environment", "speech.step.downloading", "speech.step.cuda",
            "speech.step.installing", "speech.step.installed", "speech.step.importing",
            "speech.step.loadingModel", "speech.step.ready"
        };

        foreach (var language in new[] { "en", "de", "zh-CN" })
        {
            var said = Flatten(JsonDocument.Parse(
                File.ReadAllText(Path.Combine(locales, language + ".json"))).RootElement);
            foreach (var key in keys)
            {
                Assert.True(
                    said.TryGetValue(key, out var text) && text.Trim().Length > 0,
                    $"{language}.json has nothing to say for {key}");
            }
        }
    }

    /// <summary>The locale files, found by walking up from the test assembly -
    /// they are not embedded, because the host reads them off disk.</summary>
    /// <remarks>
    /// "Locales" with the capital the folder actually has. Spelled in lower case
    /// this found it on Windows and nowhere else, so the check ran green on the
    /// machine it was written on and threw on the build machine - where the run
    /// that packages a release also runs, which is a release that does not
    /// happen over a folder name.
    /// </remarks>
    private static string LocalesDir()
    {
        var at = new DirectoryInfo(AppContext.BaseDirectory);
        while (at != null)
        {
            var wanted = Path.Combine(at.FullName, "Novalist.Extensions.Speech", "Locales");
            if (Directory.Exists(wanted))
                return wanted;
            at = at.Parent;
        }
        throw new DirectoryNotFoundException("the Speech locales are not above the test assembly");
    }

    /// <summary>The locale files mix flat "a.b.c" keys with nested objects, and
    /// a key spelled either way is the same key to the host.</summary>
    private static Dictionary<string, string> Flatten(JsonElement element, string prefix = "")
    {
        var said = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var key = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var (nested, text) in Flatten(property.Value, key))
                    said[nested] = text;
            }
            else if (property.Value.ValueKind == JsonValueKind.String)
            {
                said[key] = property.Value.GetString() ?? string.Empty;
            }
        }
        return said;
    }

    [Fact]
    public void TheEngine_DoesNotSwapItsIdentityReferencePerLine()
    {
        // The approved reference is both identity and ICL context. Replacing it
        // with a rendered line to imitate that line's prosody would also replace
        // the conditioning chosen to keep the speaker stable.
        Assert.False(new SpeechExtension().Features.HasFlag(VoiceEngineFeatures.EmotionReference));
    }

    [Fact]
    public void TheExtension_ReportsNoFaultAsNothingRatherThanAsAnEmptyString()
    {
        // A host filtering on "has no error" is entitled to test for null. An
        // engine that answered with an empty string was left out of a list it
        // belonged in, and the writer saw a different engine's name instead.
        Assert.Equal(string.Empty, new SpeechExtension().Explain(null));
    }

    [Fact]
    public async Task TheExtension_BeforeItIsInitialisedSaysSoRatherThanThrowing()
    {
        var extension = new SpeechExtension();

        var status = await extension.GetStatusAsync();

        Assert.False(status.IsReady);
        Assert.Equal("not-initialised", status.Error);
    }

    /// <param name="lines">How many lines the run is, all in the one voice.</param>
    private static NarrationRequest Request(int lines = 1) => new()
    {
        Language = "en",
        Rate = 1.0,
        Voices = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["mira-voice"] = [9, 9, 9]
        },
        VoiceReferenceTexts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mira-voice"] = "This is the exact reference."
        },
        Segments =
        [
            .. Enumerable.Range(1, lines).Select(at => new NarrationSegment
            {
                Key = "d:" + at,
                Text = at == 1 ? "You are late," : "And you know it.",
                VoiceId = "mira-voice",
                IsDialogue = true,
                Direction = new VoiceDirection
                {
                    Key = "angry",
                    Vector = new Dictionary<string, double> { ["angry"] = 0.9 },
                    Instruction = "Read this angry, as though snapped.",
                    Source = "Verb"
                }
            })
        ]
    };

    [Fact]
    public async Task ASidecarThatNeverAnswers_IsGivenUpOnRatherThanWaitedForForEver()
    {
        // The failure this ends: a stray byte on the front of the first request
        // meant the sidecar dropped it and went back to listening, the host
        // waited for a reply that was never coming, and the writer watched a
        // dialog that said "Starting" until they killed the application.
        var channel = new SilentChannel();
        var engine = new VoiceEngine(() => channel, _work, TimeSpan.FromMilliseconds(80));

        await engine.PrepareAsync(null);

        Assert.False(engine.IsReady);
        Assert.Equal("no-answer", engine.Fault);
        Assert.True(channel.Stopped);
    }

    [Fact]
    public async Task ASidecarThatIsMerelySlow_IsNotGivenUpOn()
    {
        // Everything after the first word is a model download measured in
        // gigabytes, and no deadline belongs anywhere near it.
        var channel = new SlowChannel(TimeSpan.FromMilliseconds(200));
        var engine = new VoiceEngine(() => channel, _work, TimeSpan.FromMilliseconds(80));

        await engine.PrepareAsync(null);

        Assert.True(engine.IsReady);
        Assert.Null(engine.Fault);
    }

    /// <summary>Speaks at once, then takes its time over the rest.</summary>
    private sealed class SlowChannel(TimeSpan pause) : ISidecarChannel
    {
        private int _step;

        public bool IsRunning { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(string line, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
        {
            _step++;
            if (_step == 1)
                return JsonSerializer.Serialize(new { type = "progress", step = "importing" });

            // Longer than the first-word deadline would have allowed.
            await Task.Delay(pause, cancellationToken);
            return JsonSerializer.Serialize(
                new { type = "ready", version = SidecarProtocolVersion, ready = true, detail = "on cuda" });
        }

        public void Stop() => IsRunning = false;

        public void Dispose() => Stop();
    }

    [Fact]
    public async Task RepliesFromAReadingThatWasStopped_AreNotReadAsAnswersToTheNextOne()
    {
        // The failure this ends: the sidecar cannot be interrupted inside the
        // model, so a stopped reading goes on being spoken. Its replies arrived
        // in the middle of the next request, naming files that had since been
        // deleted or written over - FileNotFoundException, a dead reading, and
        // no sound at all, every time Play was pressed.
        var stale = Path.Combine(_work, "clip-stale.wav");
        await File.WriteAllBytesAsync(stale, [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(_work, "clip-live.wav"), [4, 5, 6]);

        var channel = new FakeChannel([
            Ready(),
            // A leftover from request 1, arriving while request 2 is listening.
            JsonSerializer.Serialize(
                new { type = "clip", id = "1", key = "old", file = "clip-stale.wav", durationMs = 10.0 }),
            JsonSerializer.Serialize(
                new { type = "clip", id = "2", key = "new", file = "clip-live.wav", durationMs = 20.0 }),
            JsonSerializer.Serialize(new { type = "done", id = "2" })
        ]);
        var engine = Engine(channel);
        await engine.PrepareAsync(null);

        var clips = new List<NarrationClip>();
        await foreach (var clip in engine.RenderAsync(Request()))
            clips.Add(clip);

        var only = Assert.Single(clips);
        Assert.Equal("new", only.Key);
        Assert.Null(only.Error);
        // And the abandoned clip is cleaned up rather than left to fill the
        // working directory for the rest of the session.
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public async Task EveryRequestIsStamped_SoARepliesOwnerIsNeverInDoubt()
    {
        var channel = new FakeChannel([Ready()]);
        var engine = Engine(channel);

        await engine.PrepareAsync(null);

        var sent = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(channel.Sent[0]);
        Assert.NotNull(sent);
        Assert.False(string.IsNullOrEmpty(sent!["id"].GetString()));
    }
}
