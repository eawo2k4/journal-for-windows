// OpenAIAudioClient and LiveAudioTranscriptionSession are marked obsolete in favor of AudioSession, which handles
// whole requests; live word-by-word streaming (what makes dictation feel natural) isn't clearly supported there yet.
// The SDK package is pinned, so these keep working; move over together with the chat client (see AIService).
#pragma warning disable CS0618

using Microsoft.AI.Foundry.Local;
using NAudio.Wave;
using WindowsJournal.Helpers;

namespace WindowsJournal.Services;

/// <summary>The microphone is off for Journal (Windows privacy settings) or there isn't one.</summary>
public sealed class MicrophoneUnavailableException(string message) : Exception(message);

/// <summary>
/// Dictation: speech is turned into text on this PC by a small streaming speech model (via the Foundry
/// Local SDK, the same engine as Journal's other AI). Audio never leaves the PC and isn't kept.
/// </summary>
public static class VoiceService
{
    private const string ModelAlias = "nemotron-speech-streaming-en-0.6b";

    /// <summary>Approximate download size, for the setup prompt.</summary>
    public const string ModelSize = "about 700 MB";

    private static IModel? _model;
    private static OpenAIAudioClient? _audio;

    /// <summary>True once the voice model is on this PC.</summary>
    public static async Task<bool> IsModelReadyAsync()
    {
        await AIService.EnsureManagerAsync();
        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync();
        var model = await catalog.GetModelAsync(ModelAlias);
        return model is not null && await model.IsCachedAsync();
    }

    public static async Task DownloadModelAsync(Action<double> progress, CancellationToken cancellationToken)
    {
        await AIService.EnsureManagerAsync();
        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync();
        var model = await catalog.GetModelAsync(ModelAlias)
                    ?? throw new InvalidOperationException("The voice model isn't available right now. Try again later.");
        await model.DownloadAsync(percent => progress(percent), cancellationToken);
    }

    /// <summary>
    /// Starts listening. <paramref name="onText"/> receives the whole transcript so far each time it grows
    /// (on a background thread). Dispose or call <see cref="DictationSession.StopAsync"/> to finish.
    /// </summary>
    public static async Task<DictationSession> StartAsync(Action<string> onText)
    {
        await EnsureMicrophoneAccessAsync();

        if (_audio is null)
        {
            await AIService.EnsureManagerAsync();
            var catalog = await FoundryLocalManager.Instance.GetCatalogAsync();
            _model = await catalog.GetModelAsync(ModelAlias) ?? throw new InvalidOperationException("The voice model isn't set up.");
            await _model.LoadAsync();
            _audio = await _model.GetAudioClientAsync();
        }

        var session = new DictationSession(_audio.CreateLiveTranscriptionSession(), onText);
        await session.StartAsync();
        return session;
    }

    /// <summary>Asks Windows for microphone access (the installed app gets the standard consent prompt).</summary>
    private static async Task EnsureMicrophoneAccessAsync()
    {
        if (WaveIn.DeviceCount == 0 && TestAudioFile is null)
        {
            throw new MicrophoneUnavailableException("No microphone was found. Connect one and try again.");
        }

        if (!AppInfo.IsPackaged) return;

        var capability = Windows.Security.Authorization.AppCapabilityAccess.AppCapability.Create("microphone");
        var status = await capability.RequestAccessAsync();
        if (status != Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed)
        {
            throw new MicrophoneUnavailableException("Journal doesn't have access to your microphone. Turn it on in Windows Settings > Privacy & security > Microphone.");
        }
    }

    // WINDOWSJOURNAL_TEST_AUDIO plays a 16 kHz mono WAV file instead of the microphone (development testing only).
    internal static string? TestAudioFile =>
        !AppInfo.IsPackaged && Environment.GetEnvironmentVariable("WINDOWSJOURNAL_TEST_AUDIO") is { Length: > 0 } path && File.Exists(path)
            ? path
            : null;
}

/// <summary>One stretch of dictation: microphone audio in, transcript out.</summary>
public sealed class DictationSession : IAsyncDisposable
{
    // The model expects 16 kHz, 16-bit, mono PCM.
    private static readonly WaveFormat Format = new(16000, 16, 1);

    private readonly Microsoft.AI.Foundry.Local.OpenAI.LiveAudioTranscriptionSession _session;
    private readonly Action<string> _onText;
    private readonly CancellationTokenSource _stop = new();
    private WaveIn? _microphone;
    private Task? _reader;
    private Task? _testFeeder;
    private string _committed = "";
    private string _interim = "";
    private string _lastSegment = "";
    private bool _stopped;

    internal DictationSession(Microsoft.AI.Foundry.Local.OpenAI.LiveAudioTranscriptionSession session, Action<string> onText)
    {
        _session = session;
        _onText = onText;
        _session.Settings.SampleRate = Format.SampleRate;
        _session.Settings.Channels = Format.Channels;
        _session.Settings.BitsPerSample = Format.BitsPerSample;
    }

    public string Transcript => (_committed + _interim).Trim();

    internal async Task StartAsync()
    {
        await _session.StartAsync(CancellationToken.None);
        _reader = Task.Run(ReadAsync);

        if (VoiceService.TestAudioFile is { } file)
        {
            _testFeeder = Task.Run(() => FeedTestFileAsync(file));
            return;
        }

        _microphone = new WaveIn { WaveFormat = Format, BufferMilliseconds = 100 };
        _microphone.DataAvailable += (_, e) =>
        {
            if (!_stopped) _ = _session.AppendAsync(e.Buffer.AsMemory(0, e.BytesRecorded).ToArray(), CancellationToken.None);
        };
        _microphone.StartRecording();
    }

    /// <summary>Stops listening and returns the finished transcript.</summary>
    public async Task<string> StopAsync()
    {
        if (_stopped) return Transcript;
        _stopped = true;

        _microphone?.StopRecording();
        _stop.Cancel();
        if (_testFeeder is not null) await _testFeeder;
        await _session.StopAsync(CancellationToken.None); // flushes the last words as a final result
        if (_reader is not null) await _reader;
        return Transcript;
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var result in _session.GetStream(CancellationToken.None))
            {
                var text = string.Concat(result.Content?.Select(c => c.Text) ?? []);

                // Interim results arrive as small pieces; a final result is the whole finished segment.
                if (result.IsFinal)
                {
                    // The model can report a finished segment twice, for example on a pause and again on stop. A sentence
                    // really said twice would have new interim pieces first, so this only drops true repeats.
                    var segment = text.Trim();
                    if (_interim.Length > 0 || segment != _lastSegment)
                    {
                        _committed += (_committed.Length > 0 ? " " : "") + segment;
                        _lastSegment = segment;
                    }

                    _interim = "";
                }
                else
                {
                    _interim += text;
                }

                _onText(Transcript);
            }
        }
        catch (Exception ex)
        {
            AIService.LogError(ex);
        }
    }

    private async Task FeedTestFileAsync(string file)
    {
        var pcm = (await File.ReadAllBytesAsync(file))[44..];
        const int chunk = 3200; // 100 ms
        for (var i = 0; i < pcm.Length && !_stop.IsCancellationRequested; i += chunk)
        {
            await _session.AppendAsync(pcm.AsMemory(i, Math.Min(chunk, pcm.Length - i)), CancellationToken.None);
            try { await Task.Delay(100, _stop.Token); } catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        catch (Exception ex)
        {
            AIService.LogError(ex);
        }

        _microphone?.Dispose();
        await _session.DisposeAsync();
        _stop.Dispose();
    }
}
