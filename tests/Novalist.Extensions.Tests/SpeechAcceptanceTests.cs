using System.Text.Json;
using System.Threading.Channels;
using NSubstitute;
using Novalist.Extensions.Speech;
using Novalist.Sdk.Models.Narration;
using Novalist.Sdk.Services;
using Xunit;

namespace Novalist.Extensions.Tests;

public sealed class SpeechAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novalist-speech-acceptance-" + Guid.NewGuid().ToString("N"));

    public SpeechAcceptanceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("missing", true)]
    [InlineData("partial", true)]
    [InlineData("complete", false)]
    public async Task EngineListingRequiresCompleteModelsEvenWhenPackagesAreInstalled(string cache, bool needsDownload)
    {
        var host = Substitute.For<IHostServices>();
        host.GetExtensionSettingsPath("com.novalist.speech").Returns(_root);
        using var extension = new SpeechExtension();
        extension.Initialize(host);
        var python = new PythonEnvironment(_root, SpeechRuntime.UseMlx);
        Directory.CreateDirectory(Path.GetDirectoryName(python.VenvPython) ?? throw new InvalidOperationException());
        File.WriteAllText(python.VenvPython, "This interpreter must never run during engine listing.");
        var variant = SpeechRuntime.UseMlx ? "-mlx" : python.VenvPath.EndsWith("venv-rocm", StringComparison.Ordinal) ? "-rocm" : "";
        var requirements = Path.Combine(_root, "python", SpeechRuntime.UseMlx ? "requirements-macos.txt" : "requirements.txt");
        File.WriteAllText(Path.Combine(_root, "installed" + variant + ".txt"), python.Recipe(requirements));
        Assert.True(python.IsBuiltFor(requirements));
        if (cache != "missing")
        {
            File.WriteAllBytes(Path.Combine(_root, "weights.bin"), cache == "complete" ? [1, 2, 3] : [1]);
            var model = new { path = _root, files = new Dictionary<string, long> { ["weights.bin"] = 3 } };
            File.WriteAllText(Path.Combine(_root, "models-ready.json"), JsonSerializer.Serialize(new
            {
                backend = SpeechRuntime.UseMlx ? "mlx" : "torch",
                models = new Dictionary<string, object> { ["design"] = model, ["clone"] = model }
            }));
        }

        var status = await extension.GetStatusAsync();

        Assert.False(status.IsReady);
        Assert.Equal(needsDownload, status.DownloadBytes.HasValue);
        Assert.Null(status.Error);
        Assert.False(Directory.Exists(python.WorkPath));
    }

    [Fact]
    public async Task CancellationAfterFirstPassageStopsLaterGenerationAndServesNextRequest()
    {
        using var first = new BarrierModelChannel(_root, pauseAfterFirst: true);
        using var next = new BarrierModelChannel(_root, pauseAfterFirst: false);
        var channels = new Queue<ISidecarChannel>([first, next]);
        using var engine = new VoiceEngine(() => channels.Dequeue(), _root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using (var reader = engine.RenderAsync(Request(3), cancel.Token).GetAsyncEnumerator())
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal("passage-1", reader.Current.Key);
            await first.BarrierReached.Task.WaitAsync(deadline.Token);
            var pending = reader.MoveNextAsync().AsTask();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        await first.ModelCompletion.WaitAsync(deadline.Token);
        first.ReleaseBarrier.TrySetResult();
        Assert.Equal(["passage-1"], first.Generated);
        Assert.False(first.IsRunning);
        Assert.False(engine.IsReady);

        var clips = new List<NarrationClip>();
        await foreach (var clip in engine.RenderAsync(Request(1), deadline.Token)) clips.Add(clip);
        Assert.Equal("passage-1", Assert.Single(clips).Key);
        Assert.Null(clips[0].Error);
        Assert.Empty(channels);
    }

    private static NarrationRequest Request(int passages) => new()
    {
        Language = "en",
        Segments = [.. Enumerable.Range(1, passages).Select(index => new NarrationSegment { Key = "passage-" + index, Text = "Synthetic passage." })]
    };

    private sealed class BarrierModelChannel : ISidecarChannel
    {
        private readonly string _work;
        private readonly bool _pauseAfterFirst;
        private readonly Channel<string> _replies = Channel.CreateUnbounded<string>();
        private readonly CancellationTokenSource _stop = new();
        public TaskCompletionSource BarrierReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBarrier { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Generated { get; } = [];
        public Task ModelCompletion { get; private set; } = Task.CompletedTask;
        public bool IsRunning { get; private set; }

        public BarrierModelChannel(string work, bool pauseAfterFirst) => (_work, _pauseAfterFirst) = (work, pauseAfterFirst);
        public Task StartAsync(CancellationToken cancellationToken = default) { IsRunning = true; return Task.CompletedTask; }
        public async Task<string?> ReadAsync(CancellationToken cancellationToken = default) => await _replies.Reader.ReadAsync(cancellationToken);

        public Task SendAsync(string line, CancellationToken cancellationToken = default)
        {
            using var request = JsonDocument.Parse(line);
            if (request.RootElement.GetProperty("op").GetString() == "status")
                _replies.Writer.TryWrite("{\"type\":\"ready\",\"ready\":true,\"version\":4}");
            else
                ModelCompletion = GenerateAsync(request.RootElement.GetProperty("segments").EnumerateArray()
                    .Select(segment => segment.GetProperty("key").GetString() ?? throw new InvalidOperationException()).ToArray());
            return Task.CompletedTask;
        }

        private async Task GenerateAsync(string[] keys)
        {
            try
            {
                foreach (var key in keys)
                {
                    if (_pauseAfterFirst && Generated.Count > 0)
                    {
                        BarrierReached.TrySetResult();
                        await ReleaseBarrier.Task.WaitAsync(_stop.Token);
                    }
                    _stop.Token.ThrowIfCancellationRequested();
                    var file = Guid.NewGuid().ToString("N") + ".wav";
                    await File.WriteAllBytesAsync(Path.Combine(_work, file), [1, 2, 3], _stop.Token);
                    Generated.Add(key);
                    _replies.Writer.TryWrite(JsonSerializer.Serialize(new { type = "clip", key, file, sampleRate = 24000, durationMs = 1 }));
                }
                _replies.Writer.TryWrite("{\"type\":\"done\"}");
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
        }

        public void Stop() { IsRunning = false; _stop.Cancel(); }
        public void Dispose() => Stop();
    }
}
