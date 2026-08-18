using System.Text.Json;
using Novalist.Extensions.Speech;
using Novalist.Sdk.Models.Narration;
using Xunit;

namespace Novalist.Extensions.Tests;

/// <summary>
/// Covers the speech engine without a model on the machine.
///
/// Everything that decides anything is in <c>VoiceEngine</c>: what is asked and
/// in what order, what the sidecar is told about the emotion, what happens when
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

    private VoiceEngine Engine(params string[] replies)
        => new(() => new FakeChannel(replies), _work);

    private VoiceEngine Engine(FakeChannel channel) => new(() => channel, _work);

    private static string Ready(bool ready = true, int version = 1) => JsonSerializer.Serialize(
        new { type = "ready", version, ready, detail = "IndexTTS-2 on cuda:0" });

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
            JsonSerializer.Serialize(new { type = "progress", step = "loading-delivery", fraction = 0.7 }),
            Ready());
        var steps = new List<string>();

        await engine.PrepareAsync(new Progress<VoiceEnginePrepare>(p => steps.Add(p.Step)));

        Assert.True(engine.IsReady);
        Assert.Equal("IndexTTS-2 on cuda:0", engine.Detail);
        Assert.Contains("loading-delivery", steps);
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
        Assert.Equal("IndexTTS-2 on cuda:0", status.Detail);
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
                new { type = "designed", key = "mira", file = "design-1.wav", sampleRate = 24000 })
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
        Assert.Equal(24000, designed.SampleRate);
        // The brief went across as the instrument, with no emotion in it - the
        // host stripped that before it ever reached here.
        var asked = channel.Sent.Last();
        Assert.Contains("\"op\":\"design\"", asked);
        Assert.Contains("wiry", asked);
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
    public async Task Render_SendsTheDirectionBesideTheWordsAndNeverInsideThem()
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
        Assert.Equal("angry", segment.GetProperty("emotion").GetString());
        Assert.True(segment.GetProperty("vector").GetProperty("angry").GetDouble() > 0);
        // The words carry none of it.
        Assert.DoesNotContain("angry", segment.GetProperty("text").GetString()!);

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
            "chatterbox", File.ReadAllText(Path.Combine(directory, "requirements.txt")));
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
        Assert.True(extension.Features.HasFlag(VoiceEngineFeatures.EmotionVector));
        Assert.True(extension.Features.HasFlag(VoiceEngineFeatures.EmotionInstruction));
        Assert.True(extension.Features.HasFlag(VoiceEngineFeatures.RunsOnCpu));
        // No cloning: there is no recording to clone from, which is also what
        // keeps a real person's voice out of this entirely.
        Assert.False(extension.Features.HasFlag(VoiceEngineFeatures.CloneFromSample));
        // And it does not claim to read affect off the script: it performs what
        // the writer directed, which is the point of being able to overrule a
        // line.
        Assert.False(extension.Features.HasFlag(VoiceEngineFeatures.EmotionInferred));
    }

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
    [InlineData("installing")]
    [InlineData("looking-for-python")]
    [InlineData("loading-delivery")]
    [InlineData("loading-design")]
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
    [InlineData("Collecting torch==2.6.0 (from chatterbox-tts>=0.1.7->-r C:/Users/x/reqs.txt (line 12))",
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
    // The commonest failure there is: the newest release is always the one
    // somebody just installed and always the one torch does not support yet. A
    // venv builds on it happily and the install dies seconds later.
    [InlineData("Python 3.14.6", false)]
    [InlineData("Python 3.15.0", false)]
    // And too old to be worth trying.
    [InlineData("Python 3.8.10", false)]
    [InlineData("Python 2.7.18", false)]
    [InlineData("", false)]
    [InlineData("not a version at all", false)]
    public void APythonIsUsableOnlyWhereTheStackHasWheels(string version, bool usable)
        => Assert.Equal(usable, PythonEnvironment.IsUsable(version));

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

    private static NarrationRequest Request() => new()
    {
        Language = "en",
        Rate = 1.0,
        Voices = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["mira-voice"] = [9, 9, 9]
        },
        Segments =
        [
            new NarrationSegment
            {
                Key = "d:1",
                Text = "You are late,",
                VoiceId = "mira-voice",
                IsDialogue = true,
                Direction = new VoiceDirection
                {
                    Key = "angry",
                    Vector = new Dictionary<string, double> { ["angry"] = 0.9 },
                    Instruction = "Read this angry, as though snapped.",
                    Source = "Verb"
                }
            }
        ]
    };
}
