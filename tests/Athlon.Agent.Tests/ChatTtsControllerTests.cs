using System.Text.Json;
using Athlon.Agent.App.Services.Audio;
using Athlon.Agent.App.Services.Chat;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Audio;

namespace Athlon.Agent.Tests;

/// <summary>
/// Covers the read-aloud state machine that the bubble button mirrors. <see cref="PcmAudioPlayer"/>
/// is deliberately not exercised here: it opens a real output device, which is unavailable in CI.
/// </summary>
public sealed class ChatTtsControllerTests
{
    [Fact]
    public async Task PlayAsync_reports_loading_then_playing_then_ended()
    {
        var player = new FakeAudioPlayer();
        var client = new FakeTtsClient
        {
            Chunks = [new TtsAudioChunk(new byte[256], 24_000)]
        };
        var controller = CreateController(client, player);
        var states = new List<string>();
        controller.AttachPostTarget(json => Capture(json, states));

        await controller.PlayAsync("msg-1", "你好");

        Assert.Equal(["loading", "playing", "ended"], states);
        Assert.Equal(1, player.StartCount);
        Assert.Equal(256, player.BytesEnqueued);
        Assert.False(controller.IsActive);
    }

    [Fact]
    public async Task PlayAsync_reports_error_without_throwing_when_the_client_fails()
    {
        var controller = CreateController(
            new FakeTtsClient { Error = new TtsClientException("connection refused") },
            new FakeAudioPlayer());
        var errors = new List<string?>();
        controller.AttachPostTarget(json => CaptureError(json, errors));

        await controller.PlayAsync("msg-1", "你好");

        Assert.Single(errors);
        Assert.Equal("connection refused", errors[0]);
    }

    [Fact]
    public async Task PlayAsync_reports_not_configured_error_when_disabled()
    {
        var settings = new AppSettings();
        settings.Tts.Enabled = false;
        var controller = new ChatTtsController(
            new FakeTtsClient(),
            new FakeAudioPlayer(),
            settings,
            new NoOpLogger());
        var states = new List<string>();
        controller.AttachPostTarget(json => Capture(json, states));

        await controller.PlayAsync("msg-1", "你好");

        Assert.Equal(["error"], states);
    }

    [Fact]
    public async Task PlayAsync_cancels_the_previous_utterance_when_a_new_one_starts()
    {
        var player = new FakeAudioPlayer();
        var client = new FakeTtsClient
        {
            // Block on the first chunk so the second PlayAsync call arrives mid-stream.
            Gate = new TaskCompletionSource(),
            Chunks = [new TtsAudioChunk(new byte[64], 24_000)]
        };
        var controller = CreateController(client, player);

        var first = controller.PlayAsync("msg-1", "第一条");
        await player.Started.Task;

        await controller.PlayAsync("msg-2", "第二条");
        client.Gate.TrySetResult();
        await first;

        // The first utterance must not keep the device busy, and only the newest id stays active.
        Assert.True(player.StopCount >= 1);
        Assert.False(controller.IsActive);
    }

    [Fact]
    public async Task Stop_halts_playback_and_reports_ended()
    {
        var player = new FakeAudioPlayer();
        var client = new FakeTtsClient
        {
            Gate = new TaskCompletionSource(),
            Chunks = [new TtsAudioChunk(new byte[64], 24_000)]
        };
        var controller = CreateController(client, player);
        var states = new List<string>();
        controller.AttachPostTarget(json => Capture(json, states));

        var play = controller.PlayAsync("msg-1", "你好");
        await player.Started.Task;

        controller.Stop();
        client.Gate.TrySetResult();
        await play;

        Assert.True(player.StopCount >= 1);
        Assert.Contains("ended", states, StringComparer.Ordinal);
        Assert.False(controller.IsActive);
    }

    [Fact]
    public async Task PlayAsync_ignores_empty_text_and_reports_ended()
    {
        var player = new FakeAudioPlayer();
        var controller = CreateController(new FakeTtsClient(), player);
        var states = new List<string>();
        controller.AttachPostTarget(json => Capture(json, states));

        await controller.PlayAsync("msg-1", "   ");

        Assert.Equal(["ended"], states);
        Assert.Equal(0, player.StartCount);
    }

    private static ChatTtsController CreateController(FakeTtsClient client, FakeAudioPlayer player)
    {
        var settings = new AppSettings();
        settings.Tts.Enabled = true;
        return new ChatTtsController(client, player, settings, new NoOpLogger());
    }

    private static Task Capture(string json, List<string> states)
    {
        states.Add(ReadState(json));
        return Task.CompletedTask;
    }

    private static Task CaptureError(string json, List<string?> errors)
    {
        using var document = JsonDocument.Parse(json);
        errors.Add(document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null);
        return Task.CompletedTask;
    }

    private static string ReadState(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("state").GetString() ?? string.Empty;
    }

    /// <summary>Stands in for the NAudio-backed player so no output device is opened.</summary>
    private sealed class FakeAudioPlayer : IPcmAudioPlayer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int BytesEnqueued { get; private set; }

        public void Start(int sampleRate)
        {
            StartCount++;
            Started.TrySetResult();
        }

        public void Enqueue(byte[] pcm) => BytesEnqueued += pcm.Length;

        public Task WaitForDrainAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Stop() => StopCount++;
    }

    private sealed class FakeTtsClient : ITtsClient
    {
        public IReadOnlyList<TtsAudioChunk> Chunks { get; init; } = [];

        public Exception? Error { get; init; }

        public TaskCompletionSource? Gate { get; init; }

        public async IAsyncEnumerable<TtsAudioChunk> StreamAsync(
            TtsRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Error is not null)
            {
                throw Error;
            }

            foreach (var chunk in Chunks)
            {
                if (Gate is not null)
                {
                    await Gate.Task.WaitAsync(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
            }
        }

        public Task<TtsProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TtsProbeResult(null, 24_000, false, [], ["pcm"]));
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
