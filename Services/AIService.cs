using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Text;
using WindowsJournal.Helpers;
using WindowsJournal.Models;

namespace WindowsJournal.Services;

public enum FormatStyle
{
    Simple,
    Expressive,
}

public enum AIProvider
{
    None,
    WindowsAI,
    LocalModel,
}

/// <summary>Where the built-in model is: still checking, not downloaded, downloading, or ready.</summary>
public enum ModelState
{
    Checking,
    Unsupported,
    NotDownloaded,
    Downloading,
    Ready,
}

/// <summary>
/// On-device AI only. Copilot+ PCs use Windows AI (Phi Silica), which is built into Windows.
/// Other PCs run a small open model inside Journal itself through the Foundry Local SDK; it's
/// downloaded once from Settings. Journal text is never sent to a cloud service.
/// </summary>
public static partial class AIService
{
    private const string DefaultModelAlias = "phi-4-mini";
    private const string DefaultModelName = "Phi-4 mini";

    // AI files live outside the journal data folder: they're big, and test runs that point the
    // journal elsewhere shouldn't download them again.
    // WINDOWSJOURNAL_AI points this elsewhere for testing the download flow from scratch.
    private static string? AiFolderOverride => Environment.GetEnvironmentVariable("WINDOWSJOURNAL_AI") is { Length: > 0 } custom ? custom : null;

    // Installed: the app's cache folder (removed on uninstall, never roamed or backed up).
    private static string AiFolder => AiFolderOverride
        ?? (Helpers.AppInfo.IsPackaged
            ? Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "AI")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsJournal", "AI"));

    // If Foundry Local is already on this PC, share its model cache instead of downloading a second copy.
    private static string ModelCacheFolder
    {
        get
        {
            var foundry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "cache", "models");
            return AiFolderOverride is null && Directory.Exists(foundry) ? foundry : Path.Combine(AiFolder, "models");
        }
    }

    // The chat client's settings are shared, so run one request at a time.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static LanguageModel? _windowsModel;
    private static IModel? _model;

    // OpenAIChatClient is marked obsolete in favor of ChatSession, but in SDK 2.2.1 ChatSession took ~47s
    // for a reply this client returns in ~2s (tested on an RTX 3060). The package is pinned, so this keeps
    // working; move to ChatSession when a later SDK fixes its speed.
#pragma warning disable CS0618
    private static OpenAIChatClient? _chat;
#pragma warning restore CS0618
    private static bool _detecting;
    private static bool _accelerationReady;

    public static AIProvider Provider { get; private set; }

    public static ModelState ModelState { get; private set; } = ModelState.Checking;

    public static string StatusText { get; private set; } = "Checking for on-device AI…";

    public static bool IsAvailable => Provider != AIProvider.None && SettingsService.Current.AIEnabled;

    public static string ProviderName => Provider switch
    {
        AIProvider.WindowsAI => "Windows AI",
        AIProvider.LocalModel => DefaultModelName,
        _ => "AI",
    };

    /// <summary>Raised on the UI thread when availability, download state, or the setting changes.</summary>
    public static event EventHandler? StatusChanged;

    public static void NotifySettingsChanged() => StatusChanged?.Invoke(null, EventArgs.Empty);

    #region Detection and download

    public static async Task DetectAsync()
    {
        if (_detecting || ModelState == ModelState.Downloading) return;
        _detecting = true;
        try
        {
            if (TryWindowsAI())
            {
                Provider = AIProvider.WindowsAI;
                ModelState = ModelState.Ready;
            }
            else
            {
                await DetectLocalModelAsync();
            }
        }
        finally
        {
            _detecting = false;
        }

        StatusChanged?.Invoke(null, EventArgs.Empty);
    }

    private static bool TryWindowsAI()
    {
        try
        {
            var state = LanguageModel.GetReadyState();
            if (state is not (AIFeatureReadyState.Ready or AIFeatureReadyState.NotReady))
            {
                return false;
            }

            // NotReady means Windows still has to fetch its model; that happens on first use.
            StatusText = state == AIFeatureReadyState.Ready
                ? "Using Windows AI (Phi Silica), built into this Copilot+ PC."
                : "Using Windows AI. Windows will finish setting it up the first time you use it.";
            return true;
        }
        catch (Exception)
        {
            // Not a Copilot+ PC, no package identity, or an older Windows build.
            return false;
        }
    }

    private static async Task DetectLocalModelAsync()
    {
        Provider = AIProvider.None;
        try
        {
            await EnsureManagerAsync();
            var catalog = await FoundryLocalManager.Instance.GetCatalogAsync();
            var cached = (await catalog.GetCachedModelsAsync()).Where(m => !NonChatModel().IsMatch(m.Id)).ToList();
            _model = cached.FirstOrDefault(m => m.Id.Contains("phi-4-mini-instruct", StringComparison.OrdinalIgnoreCase))
                     ?? cached.FirstOrDefault();

            if (_model is null)
            {
                ModelState = ModelState.NotDownloaded;
                StatusText = "Download the AI model once to use Format, Reflect, and personalized suggestions.";
                return;
            }

            Provider = AIProvider.LocalModel;
            ModelState = ModelState.Ready;
            StatusText = $"Ready. {DefaultModelName} runs on this PC, so your entries never leave it.";
            if (SettingsService.Current.AIEnabled) _ = PrepareLocalModelAsync();
        }
        catch (Exception ex)
        {
            LogError(ex);
            ModelState = ModelState.Unsupported;
            StatusText = "On-device AI isn't available on this PC.";
        }
    }

    private static CancellationTokenSource? _downloadCancel;

    /// <summary>What the download is doing right now, for whichever page is showing it.</summary>
    public static string DownloadStage { get; private set; } = "";

    public static double DownloadPercent { get; private set; }

    /// <summary>Raised on the UI thread as the download progresses.</summary>
    public static event EventHandler? DownloadProgressChanged;

    public static void CancelDownload() => _downloadCancel?.Cancel();

    /// <summary>
    /// Downloads the built-in model (and the GPU/NPU support it needs). Call from the UI thread; it keeps
    /// going if the user leaves Settings, and reports through <see cref="DownloadProgressChanged"/>.
    /// </summary>
    public static async Task DownloadModelAsync()
    {
        if (ModelState == ModelState.Downloading) return;

        // Progress<T> posts back to the thread that created it (the UI thread).
        var progress = new Progress<(string Stage, double Percent)>(p =>
        {
            (DownloadStage, DownloadPercent) = p;
            DownloadProgressChanged?.Invoke(null, EventArgs.Empty);
        });
        _downloadCancel = new CancellationTokenSource();
        var cancellationToken = _downloadCancel.Token;

        ModelState = ModelState.Downloading;
        StatusText = "Downloading the AI model. You can keep using Journal while it downloads.";
        ((IProgress<(string, double)>)progress).Report(("Getting ready…", 0));
        StatusChanged?.Invoke(null, EventArgs.Empty);
        try
        {
            await EnsureManagerAsync();
            await EnsureAccelerationAsync(percent => ((IProgress<(string, double)>)progress).Report(("Getting your PC ready", percent)));
            cancellationToken.ThrowIfCancellationRequested();

            var catalog = await FoundryLocalManager.Instance.GetCatalogAsync();
            var model = await catalog.GetModelAsync(DefaultModelAlias)
                        ?? throw new InvalidOperationException("The AI model isn't available right now. Try again later.");

            await model.DownloadAsync(percent => ((IProgress<(string, double)>)progress).Report(("Downloading the AI model", percent)), cancellationToken);

            _model = model;
            _chat = null;
            Provider = AIProvider.LocalModel;
            ModelState = ModelState.Ready;
            StatusText = $"Ready. {DefaultModelName} runs on this PC, so your entries never leave it.";
        }
        catch (OperationCanceledException)
        {
            ModelState = ModelState.NotDownloaded;
            StatusText = "Download canceled. You can start it again anytime.";
        }
        catch (Exception ex)
        {
            LogError(ex);
            ModelState = ModelState.NotDownloaded;
            StatusText = "The download didn't finish. Check your internet connection and try again.";
        }
        finally
        {
            StatusChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Deletes the downloaded model to free up space.</summary>
    public static async Task RemoveModelAsync()
    {
        if (_model is null) return;

        await Gate.WaitAsync();
        try
        {
            if (_chat is not null) await _model.UnloadAsync();
            await _model.RemoveFromCacheAsync();
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
        finally
        {
            _chat = null;
            Gate.Release();
        }

        await DetectAsync();
    }

    /// <summary>Releases the model's memory when Journal closes.</summary>
    public static void Shutdown()
    {
        try
        {
            if (FoundryLocalManager.IsInitialized) FoundryLocalManager.Instance.Dispose();
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    internal static async Task EnsureManagerAsync()
    {
        if (FoundryLocalManager.IsInitialized) return;

        Directory.CreateDirectory(AiFolder);
        await FoundryLocalManager.CreateAsync(new Configuration
        {
            AppName = "WindowsJournal",
            AppDataDir = AiFolder,
            ModelCacheDir = ModelCacheFolder,
            LogsDir = Path.Combine(AiFolder, "logs"),
        }, NullLogger.Instance);
    }

    // Acceleration runtimes that failed to load on this PC, so later launches don't fetch them again.
    private static string UnusableEpsFile => Path.Combine(AiFolder, "unusable-eps.txt");

    /// <summary>
    /// Registers the hardware acceleration (GPU/NPU) runtimes. They're downloaded the first time and
    /// reused after that. If none work, the CPU is always available.
    /// </summary>
    private static async Task EnsureAccelerationAsync(Action<double>? progress = null)
    {
        if (_accelerationReady) return;

        // The SDK otherwise re-downloads runtimes that can never load here on every launch (for example
        // CUDA without NVIDIA's CUDA toolkit), which cost ~20s per launch and could stall indefinitely.
        var manager = FoundryLocalManager.Instance;
        var unusable = File.Exists(UnusableEpsFile) ? File.ReadAllLines(UnusableEpsFile).ToHashSet() : [];
        // CUDA goes last: it's the largest download and needs NVIDIA's separate toolkit, so it's the most likely to fail.
        var wanted = manager.DiscoverEps()
            .Where(ep => !ep.IsRegistered && !unusable.Contains(ep.Name))
            .Select(ep => ep.Name)
            .OrderBy(name => name.StartsWith("CUDA", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // One at a time, each with its own time limit, so one stuck download can't block the others.
        for (var i = 0; i < wanted.Length; i++)
        {
            var name = wanted[i];
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await manager.DownloadAndRegisterEpsAsync(
                    [name],
                    (_, percent) => progress?.Invoke((i * 100 + percent) / wanted.Length),
                    timeout.Token);
            }
            catch (OperationCanceledException ex)
            {
                // Timed out (slow or stuck download): skip it this launch and try again next time.
                LogError(ex);
                continue;
            }

            // It finished but still isn't usable (e.g. CUDA without NVIDIA's toolkit): don't fetch it again.
            if (!manager.DiscoverEps().Any(ep => ep.Name == name && ep.IsRegistered))
            {
                File.AppendAllLines(UnusableEpsFile, [name]);
            }
        }

        _accelerationReady = true;
    }

    [GeneratedRegex("whisper|embedding|asr|speech|parakeet", RegexOptions.IgnoreCase)]
    private static partial Regex NonChatModel();

    #endregion

    #region Generation

    private static async Task<string> GenerateAsync(string system, string user, double temperature)
    {
        var text = Provider switch
        {
            AIProvider.WindowsAI => await GenerateWindowsAIAsync(system, user, temperature),
            AIProvider.LocalModel => await GenerateLocalAsync(system, user, temperature),
            _ => throw new InvalidOperationException("On-device AI isn't set up. You can download it in Settings."),
        };

        // Reasoning models wrap their thinking in <think> tags; never show that.
        return ThinkRegex().Replace(text, "").Trim();
    }

    private static async Task<string> GenerateWindowsAIAsync(string system, string user, double temperature)
    {
        if (_windowsModel is null)
        {
            if (LanguageModel.GetReadyState() == AIFeatureReadyState.NotReady)
            {
                var ready = await LanguageModel.EnsureReadyAsync();
                if (ready.Status != AIFeatureReadyResultState.Success)
                {
                    throw new InvalidOperationException("Windows AI couldn't finish setting up. Try again later.");
                }
            }

            _windowsModel = await LanguageModel.CreateAsync();
        }

        var options = new LanguageModelOptions { Temperature = (float)temperature };
        var result = await _windowsModel.GenerateResponseAsync($"{system}\n\n{user}", options);
        return result.Status == LanguageModelResponseStatus.Complete
            ? result.Text
            : throw new InvalidOperationException($"Windows AI couldn't respond ({result.Status}).");
    }

    /// <summary>
    /// Gets the model onto the GPU in the background right after launch (~20-25s, mostly registering GPU
    /// support), so it's usually ready by the time someone asks for something.
    /// </summary>
    private static async Task PrepareLocalModelAsync()
    {
        await Gate.WaitAsync();
        try
        {
            await EnsureLocalModelLoadedAsync();
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    // Call while holding Gate.
    private static async Task EnsureLocalModelLoadedAsync()
    {
        if (_chat is not null) return;

        await EnsureAccelerationAsync();
        await _model!.LoadAsync();
#pragma warning disable CS0618 // See the note on _chat.
        _chat = await _model.GetChatClientAsync();
#pragma warning restore CS0618
    }

    private static async Task<string> GenerateLocalAsync(string system, string user, double temperature)
    {
        await Gate.WaitAsync();
        try
        {
            await EnsureLocalModelLoadedAsync();

            _chat!.Settings.Temperature = (float)temperature;
            _chat.Settings.MaxTokens = 1500;
            var response = await _chat.CompleteChatAsync(new[]
            {
                ChatMessage.FromSystem(system),
                ChatMessage.FromUser(user),
            });
            return response.Choices?.FirstOrDefault()?.Message.Content ?? "";
        }
        finally
        {
            Gate.Release();
        }
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkRegex();

    #endregion

    #region Journal features

    /// <summary>
    /// Simple fixes errors and paragraphs without changing what the writer said.
    /// Expressive also polishes the wording, keeping the writer's voice and every fact.
    /// </summary>
    public static async Task<string> FormatEntryAsync(string body, FormatStyle style)
    {
        const string simple =
            "You are a careful copy editor for a private journal. Fix spelling, grammar, punctuation and capitalization, " +
            "and split the text into readable paragraphs. Keep the writer's exact words, voice, tone, slang and meaning. " +
            "Do not add, remove, summarize or rephrase ideas. Do not add a title, headings or commentary. " +
            "Reply with only the corrected entry.";

        const string expressive =
            "You are an editor for a private journal. Polish this entry so it reads expressively and flows well: fix errors, " +
            "smooth awkward sentences, choose more vivid words where it helps, and organize it into paragraphs. " +
            "Keep it in the writer's own first-person voice and keep every fact, feeling and detail. " +
            "Never invent events, people, places or feelings. Do not add a title, headings or commentary. " +
            "Reply with only the revised entry.";

        var (instructions, temperature) = style == FormatStyle.Simple ? (simple, 0.1) : (expressive, 0.5);
        var (min, max) = style == FormatStyle.Simple ? (0.7, 1.4) : (0.6, 2.0);

        // Guard against the model rewriting or truncating someone's journal.
        bool Acceptable(string text)
        {
            var ratio = (double)text.Length / Math.Max(1, body.Length);
            return text.Length > 0 && ratio >= min && ratio <= max;
        }

        var result = StripWrapping(await GenerateAsync(instructions, body, temperature));
        if (Acceptable(result)) return result;

        // Short entries are the easiest to overdo, so ask once more, closer to what's there.
        result = StripWrapping(await GenerateAsync(instructions + " Keep it about the same length as the original.", body, Math.Min(temperature, 0.2)));
        return Acceptable(result) ? result : throw new InvalidOperationException("The AI changed too much, so your text was left as it was.");
    }

    /// <summary>
    /// Formats an entry that has formatting, one line at a time. A small model can't reliably keep markers across
    /// a whole document, so headings and lists stay out of its hands entirely, and each line is checked on its own:
    /// a line the model changes too much, or whose bold/italic/underline it loses, keeps its original text.
    /// Returns the Markdown and how many lines were changed.
    /// </summary>
    public static async Task<(string Markdown, int Changed)> FormatMarkdownAsync(string markdown, FormatStyle style, Action<int, int>? progress = null)
    {
        const string simple =
            "You are a careful copy editor for a private journal. You are given one line of an entry. Fix its spelling, grammar, punctuation " +
            "and capitalization. Keep the writer's exact words, voice, tone and meaning. Reply with only the corrected line, on one line.";
        const string expressive =
            "You are an editor for a private journal. You are given one line of an entry. Polish it so it reads expressively: fix errors and " +
            "choose more vivid words where it helps. Keep the writer's first-person voice and every fact and detail, and never invent anything. " +
            "Reply with only the revised line, on one line.";
        const string keepMarkers =
            " The line uses Markdown: **bold**, *italic*, __underline__. Keep each marker around the same words, and add no new markers.";

        var lines = markdown.Split('\n');
        var total = lines.Count(l => !string.IsNullOrWhiteSpace(RichText.SplitLine(l).Inline));
        var done = 0;
        var changed = 0;
        var (instructions, temperature) = style == FormatStyle.Simple ? (simple, 0.1) : (expressive, 0.5);
        var (min, max) = style == FormatStyle.Simple ? (0.7, 1.4) : (0.6, 2.0);

        for (var i = 0; i < lines.Length; i++)
        {
            var (marker, inline) = RichText.SplitLine(lines[i]);
            if (string.IsNullOrWhiteSpace(inline)) continue;
            progress?.Invoke(++done, total);

            // Lines with no styling go as plain text, so the model never sees escape characters.
            var plain = RichText.PlainTextOf(inline);
            var input = plain ?? inline;
            var reply = StripWrapping(await GenerateAsync(plain is null ? instructions + keepMarkers : instructions, input, temperature))
                .ReplaceLineEndings(" ").Trim();

            var ratio = (double)reply.Length / Math.Max(1, input.Length);
            if (reply.Length == 0 || ratio < min || ratio > max) continue;

            if (plain is not null)
            {
                reply = RichText.PlainToInline(reply, marker);
            }
            else if (!RichText.SameStyling(inline, reply))
            {
                continue;
            }

            if (reply != inline) changed++;
            lines[i] = marker + reply;
        }

        return (string.Join('\n', lines), changed);
    }

    /// <summary>Reads an entry's mood and main topic. Anything the model answers that isn't on the lists comes back as None.</summary>
    public static async Task<(Mood Mood, EntryCategory Category)> DetectMoodAndTopicAsync(string title, string body)
    {
        var topics = string.Join(", ", CategoryOption.All.Select(c => c.Label));
        var system =
            "You label private journal entries. Decide how the writer feels and what the entry is mainly about.\n" +
            "Moods: Rough, Low, Okay, Good, Great.\n" +
            $"Topics: {topics}, None.\n" +
            "Relationships means family, friends, partners or anyone the writer cares about. " +
            "Use Work only when the entry is about a job, career or workplace, not just because something feels stressful. " +
            "If no topic clearly fits, answer None.\n" +
            "Reply with exactly two lines and nothing else:\nMood: <one mood>\nTopic: <one topic>\n\n" +
            "Examples:\n" +
            "Entry: Back at the gym after a month off. Sore but it felt good to move.\nMood: Good\nTopic: Health and fitness\n" +
            "Entry: My dad goes in for surgery on Monday and I am scared for him.\nMood: Low\nTopic: Relationships\n" +
            "Entry: My boss cut the budget and I am worried about my job.\nMood: Low\nTopic: Work\n" +
            "Entry: Nothing much today, did laundry and made soup.\nMood: Okay\nTopic: Daily reflection";

        var input = $"Entry: {title}. {Truncate(body, 3000)}";
        var reply = await GenerateAsync(system, input, 0.1);

        // Small models occasionally drop the labels; a reply like that can't be trusted, so ask once more.
        if (!MoodLine().IsMatch(reply) || !TopicLine().IsMatch(reply)) reply = await GenerateAsync(system, input, 0.3);

        var moodLine = MoodLine().Match(reply).Groups[1].Value;
        var topicLine = TopicLine().Match(reply).Groups[1].Value;

        // The model sometimes hedges, like "Mixed (Good and Low)" or "Work and Personal growth".
        // Mixed feelings are Okay; otherwise the first thing it named wins.
        var mood = moodLine.Contains("mixed", StringComparison.OrdinalIgnoreCase)
            ? Mood.Okay
            : FirstNamed(moodLine, MoodOption.All, m => m.Label)?.Value ?? Mood.None;
        var category = FirstNamed(topicLine, CategoryOption.All, c => c.Label)?.Value ?? EntryCategory.None;
        return (mood, category);
    }

    private static T? FirstNamed<T>(string text, IEnumerable<T> options, Func<T, string> label) where T : class =>
        options
            .Select(o => (Option: o, Position: text.IndexOf(label(o), StringComparison.OrdinalIgnoreCase)))
            .Where(x => x.Position >= 0)
            .OrderBy(x => x.Position)
            .Select(x => x.Option)
            .FirstOrDefault();

    [GeneratedRegex(@"(?im)^\W*mood\W*:(.*)$")]
    private static partial Regex MoodLine();

    [GeneratedRegex(@"(?im)^\W*topic\W*:(.*)$")]
    private static partial Regex TopicLine();

    /// <summary>Bump when the reflection style changes, so existing reflections get rewritten once.</summary>
    public const int ReflectionStyle = 4;

    // A reflection holds up a mirror; it isn't a chatbot with feelings about the writer's day.
    private const string ReflectionVoice =
        "Begin with \"It sounds like\" and write in a warm, calm voice, like a thoughtful friend noticing something. " +
        "Name the specific thing from the writing, and the feeling or meaning it seems to hold for the writer. " +
        "Never refer to yourself: do not use \"I\", \"me\", \"my\" or \"we\", and do not say you are glad, happy, thrilled, proud or impressed. " +
        "No exclamation marks, congratulations, pep talk, or advice. ";

    private const string ReflectionCare =
        "Never diagnose or lecture. If the writer mentions wanting to hurt themselves or being in danger, " +
        "gently encourage them to reach out to someone they trust or a local crisis line.";

    /// <summary>A short note reflecting one entry back to the writer.</summary>
    public static async Task<string> ReflectAsync(JournalEntry entry, string body)
    {
        const string system =
            "Write a brief note reflecting a private journal entry back to its writer, in 1 or 2 short sentences. " + ReflectionVoice +
            "Only say what the entry actually says; do not assume anything has happened that the writer hasn't said.\n" +
            "Examples of the style:\n" +
            "Entry: Ran five kilometers and hit a new best. Sleeping better too.\n" +
            "Note: It sounds like that new best has brought a real sense of momentum, with better sleep adding to it.\n" +
            "Entry: Argued with my brother again. I said things I regret and I keep replaying it.\n" +
            "Note: It sounds like what you said to your brother is still weighing on you, and that getting this right matters to you.\n" +
            "Entry: Got the keys to the new apartment. It is empty and echoey and I love it.\n" +
            "Note: It sounds like the empty apartment already feels full of possibility.\n" +
            ReflectionCare + " Reply with only the note.";

        var mood = entry.Mood == Mood.None ? "" : $"(Their mood: {entry.Mood}.)\n";
        var input = $"{mood}{entry.Title}\n{Truncate(body, 4000)}";

        // Small models don't always follow the rules, so check the result; one more try usually gets a usable note.
        var reflection = CleanReflection(await GenerateAsync(system, input, 0.5), 3);
        if (reflection.Length == 0) reflection = CleanReflection(await GenerateAsync(system, input, 0.2), 3);
        return reflection.Length > 0 ? reflection : throw new InvalidOperationException("The AI didn't come up with a reflection. Try again.");
    }

    /// <summary>
    /// Keeps only sentences about the writer, without the model talking about itself, praising, or giving advice.
    /// Crisis guidance is exempt, because it matters more than the style.
    /// </summary>
    private static string CleanReflection(string text, int maxSentences)
    {
        var cleaned = NoteLabel().Replace(StripWrapping(text).Trim('"', '“', '”').ReplaceLineEndings(" "), "");
        var sentences = SentenceSplit().Split(cleaned)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && (CrisisGuidance().IsMatch(s)
                || ((SpeaksToYou().IsMatch(s) || ReflectsBack().IsMatch(s)) && !FirstPerson().IsMatch(s) && !Cheerleading().IsMatch(s))))
            .Take(maxSentences)
            .Select(s => s.Replace('!', '.'));
        return string.Join(" ", sentences);
    }

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"^\s*note\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex NoteLabel();

    [GeneratedRegex(@"\b(you|your|you're|you've|yourself)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SpeaksToYou();

    // "It sounds like the new schedule has brought a sense of clarity" is about the writer without saying "you".
    [GeneratedRegex(@"^(it|this|that) (sounds|seems|feels|looks) ", RegexOptions.IgnoreCase)]
    private static partial Regex ReflectsBack();

    [GeneratedRegex(@"\b(crisis|hotline|988|someone you trust)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CrisisGuidance();

    [GeneratedRegex(@"\b(congrat\w*|impress\w*|thrill\w*|fantastic|wonderful|amazing|great job|well done|proud|glad|keep (up|going|focusing|embracing|nurturing)|you (should|could|might want|may want)|consider|try to|remember to|good luck|don't forget|(might|may|could|would|can|will) help|reach(ing)? out|talk(ing)? to|discuss(ing)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Cheerleading();

    [GeneratedRegex(@"\b(I|I'm|I’m|I've|I’ve|I'd|I’d|me|my|myself)\b")]
    private static partial Regex FirstPerson();

    /// <summary>A gentle look back across the last week of entries.</summary>
    public static async Task<string> WeeklyReflectionAsync(IEnumerable<JournalEntry> entries)
    {
        const string system =
            "Below are the writer's journal entries from the past week. Write a brief note reflecting the week back to them in 3 or 4 sentences. " +
            ReflectionVoice + "Notice recurring themes and how their mood moved through the week. " + ReflectionCare +
            " Reply with only the note.";

        var week = new StringBuilder();
        foreach (var e in entries.OrderBy(e => e.CreatedAt))
        {
            var mood = e.Mood == Mood.None ? "" : $" (mood: {e.Mood})";
            week.AppendLine($"[{e.CreatedAt:dddd}]{mood} {e.Title} {Truncate(e.Body, 500)}".Trim());
        }

        var reflection = CleanReflection(await GenerateAsync(system, week.ToString(), 0.5), 5);
        return reflection.Length > 0 ? reflection : throw new InvalidOperationException("The AI didn't come up with a reflection. Try again.");
    }

    public static async Task<string?> SuggestTitleAsync(string body)
    {
        const string system =
            "Suggest a short, warm title (2 to 6 words) for this journal entry. Reply with only the title, no quotes or punctuation at the end.";
        // Small models often wrap the title in quotes despite being asked not to.
        var title = StripWrapping(await GenerateAsync(system, Truncate(body, 2000), 0.4)).Split('\n')[0]
            .Trim().Trim('"', '\'', '“', '”', '‘', '’').Trim().TrimEnd('.');
        return title.Length is > 0 and <= 60 ? title : null;
    }

    public static async Task<string> FollowUpQuestionAsync(string body)
    {
        const string system =
            "You are a gentle, curious journaling companion. Read the writer's entry so far and ask one short follow-up " +
            "question (under 20 words) that helps them go deeper. Reply with only the question.";
        var question = StripWrapping(await GenerateAsync(system, Truncate(body, 3000), 0.7)).Split('\n')[0].Trim();
        return question.Length > 0 ? question : throw new InvalidOperationException("The AI didn't come up with a question. Try again.");
    }

    /// <summary>Prompts that build on themes from the writer's recent entries.</summary>
    public static async Task<IReadOnlyList<string>> PersonalizedPromptsAsync(IEnumerable<JournalEntry> entries, int count)
    {
        const string system =
            "You write gentle, specific journaling prompts. Each prompt is a single question or invitation under 15 words. " +
            "Write one prompt per line with no numbering, bullets or quotes, and nothing else.";

        var recent = entries.OrderByDescending(e => e.CreatedAt).Take(5).ToList();
        var now = DateTime.Now;
        var when = $"It's {now:dddd} {(now.Hour < 12 ? "morning" : now.Hour < 17 ? "afternoon" : "evening")}, {now:MMMM d}.";

        string user;
        if (recent.Count == 0)
        {
            user = $"{when} I'm just starting a journal. Write {count} inviting prompts to help me begin.";
        }
        else
        {
            var excerpts = new StringBuilder();
            foreach (var e in recent)
            {
                excerpts.AppendLine($"[{e.CreatedAt:MMM d}] {e.Title} {Truncate(e.Body, 300)}".Trim());
            }

            user = $"{when} Here are excerpts from my recent journal entries:\n---\n{excerpts}---\n" +
                   $"Write exactly {count} new prompts, each on its own line, that build on different themes from these entries without repeating them.";
        }

        var focus = SettingsService.Current.FocusAreas
            .Select(f => CategoryOption.All.FirstOrDefault(c => c.Value == f)?.Label)
            .OfType<string>()
            .ToList();
        if (focus.Count > 0) user += $" I especially want to focus on: {string.Join(", ", focus)}. Lean toward these.";

        var prompts = (await GenerateAsync(system, user, 0.9))
            .Split('\n')
            .Select(l => BulletRegex().Replace(l, "").Trim().Trim('"', '“', '”'))
            .Where(l => l.Length is > 8 and < 160)
            .Distinct()
            .Take(count)
            .ToList();

        if (prompts.Count == 0) throw new InvalidOperationException("The AI didn't return usable prompts.");

        // Small models sometimes return fewer than asked; top up with built-in prompts.
        prompts.AddRange(SuggestionService.RandomPrompts(count).Except(prompts).Take(count - prompts.Count));
        return prompts;
    }

    /// <summary>A user-facing message for any failure during generation. Also logs the technical details.</summary>
    public static string Describe(Exception ex)
    {
        LogError(ex);
        return ex is InvalidOperationException
            ? ex.Message
            : "Something went wrong with on-device AI. Try again.";
    }

    /// <summary>Appends error details (never journal text) to ai.log for troubleshooting.</summary>
    public static void LogError(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(JournalStore.DataFolder, "ai.log"),
                $"[{DateTimeOffset.Now:o}] {Provider} {_model?.Id} {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
        }
        catch (IOException)
        {
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string StripWrapping(string text) => text.Trim().Trim('`').Trim();

    [GeneratedRegex(@"^\s*(\d+[\.\)]|[-*•])\s*")]
    private static partial Regex BulletRegex();

    #endregion
}
