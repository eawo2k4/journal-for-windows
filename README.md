# Windows Journal

A private, on-device journaling app for Windows 11, in the spirit of Apple Journal / Google's Journal app — built the way an inbox Windows app would be: WinUI 3, Windows App SDK, Fluent Design.

## Try it as a new user

Build once with `dotnet build -c Release`, then double-click one of the launchers in the folder above this one:

- **Try Journal as a new user.cmd**: empty journal, welcome slideshow, default settings. Uses its own test folder (`%LOCALAPPDATA%\WindowsJournal-FreshTest`), which is wiped every run, so your real journal is never touched. AI uses the model already on the PC.
- **Try Journal as a new user (no AI model).cmd**: same, but AI starts as "not downloaded" so you see Settings > AI > Download. Downloading really fetches about 4 GB into `%LOCALAPPDATA%\WindowsJournal-FreshTest-AI`, which is kept between runs; delete it to free the space.

## Install (packaged, the way it ships)

Turn on **Developer Mode** (Settings > System > Advanced, or "For developers"), then double-click **Install Journal.cmd** in the folder above this one. It builds the MSIX and installs it for your account: Journal appears in the Start menu, uninstalls normally, and can send daily reminders. **Uninstall Journal.cmd** removes it.

- On the first install, the script copies the journal from the unpackaged version (`%LOCALAPPDATA%\WindowsJournal`) into the installed app's data folder; the app encrypts it on first launch. The original stays where it was. (The installed app can't copy it itself: Windows hides other AppData folders from packaged apps.)
- Like any installed app, its data is deleted when it's uninstalled. Export first (Settings > Your data > Export) if you want a copy.
- Only one Journal window runs at a time: launching it again, or clicking a reminder, brings the open window forward (`Program.cs`).

When `Package.appxmanifest` changes (capabilities, extensions), bump `Version` there (and `<Version>` in the .csproj), or Windows refuses to re-register the package over the installed one.

By hand: `dotnet build -c Release -p:Packaged=true`, then `Add-AppxPackage -Register bin\Packaged\Release\net10.0-windows10.0.22621.0\win-x64\AppxManifest.xml`. For the Store, set `Identity` in `Package.appxmanifest` to the values Partner Center gives you and package with `dotnet publish` / Visual Studio's "Package and Publish".

## Run (unpackaged, for development)

Requires the .NET 10 SDK on Windows 10 1904x or later (Windows 11 recommended for Mica).

```
dotnet run
```

The default build is **unpackaged and self-contained** (`WindowsAppSDKSelfContained=true`), so no install, Developer Mode, or signing is needed while developing, and the "Try Journal as a new user" launchers use it. Daily reminders and Windows AI need the packaged build. Packaged output goes to `bin\Packaged`, so the two never overwrite each other.

> Keep the project in a short path (e.g. `C:\src\WindowsJournal`). The XAML compiler fails with `WMC9999 Could not find a part of the path` when paths exceed 260 characters.

## Features

| Area | What it does | Windows 11 pattern used |
|---|---|---|
| Shell | Mica backdrop, custom tall title bar, left nav pane, search box in the pane | `MicaBackdrop`, `ExtendsContentIntoTitleBar`, `NavigationView` |
| Journal | Timeline grouped by month, cards with mood, favorite star and photo thumbnail | `ListView` + `CollectionViewSource` grouping, card brushes |
| Suggestions | Random writing prompts (personalized by on-device AI when available), "New ideas", and an "On this day" memory | Horizontal `ItemsRepeater` of buttons |
| Editor | Title, body, date, mood, photos, favorite; Format, Inspire me, Reflect; autosaves on leave; Ctrl+N / Ctrl+S | `CommandBar`, `MenuFlyout`, `CalendarDatePicker`, `InfoBar` with Undo, lightweight styling |
| AI | Improve > **Fix errors** (spelling and punctuation only) or **Polish wording** (keeps voice and facts); Inspire me asks a follow-up question; Reflect writes a short reflection saved with the entry; Insights reflects on your week; suggestions build on recent entries | Windows AI `LanguageModel` (Phi Silica) on Copilot+ PCs, Microsoft Foundry Local elsewhere |
| Welcome | First-run slideshow (5 slides): illustrations drawn in XAML from theme resources over the Windows 11 Bloom backdrop (light/dark), with an optional Windows Hello toggle; replay from Settings | `ContentDialog`, `FlipView`, `PipsPager`, `ImageBrush` in theme dictionaries |
| Insights | Current/longest streak, entries, words, days journaled, calendar heatmap, mood breakdown | `CalendarView` density colors, `UniformGridLayout` |
| Privacy | Entries and photos encrypted at rest with the Windows account; optional Windows Hello lock (offered in the welcome slideshow, like Apple's and Google's journal apps); data never leaves the PC | DPAPI (`ProtectedData`), `UserConsentVerifierInterop` |
| Settings | Theme, lock, data folder, JSON export, about | `SettingsCard` / `SettingsExpander` (Community Toolkit), same as Windows Settings |

Data lives in `%LOCALAPPDATA%\WindowsJournal`: `entries.dat` and `Photos\` are encrypted with DPAPI (current user), `settings.json` holds preferences only. Journals from older versions (plain `entries.json`) are encrypted on first launch and the plain copy is deleted. If a saved file can't be read (for example, copied from another Windows account), it's moved aside and the Journal page says where, rather than being overwritten. Set `WINDOWSJOURNAL_DATA` to another folder to test with a throwaway journal.

## On-device AI

AI is optional and never sends journal text to the cloud. Nothing extra needs installing. `Services/AIService.cs` picks a provider at startup:

1. **Windows AI (Phi Silica)** on Copilot+ PCs: built into Windows, no download. Note: the Windows AI APIs need package identity, so this path lights up once the app is packaged as MSIX.
2. **Built-in model** on any other PC: Journal hosts Phi-4 mini in-process with the [Foundry Local SDK](https://www.nuget.org/packages/Microsoft.AI.Foundry.Local) (`Microsoft.AI.Foundry.Local` 2.2.1). The user downloads it once from **Settings > AI > Download** (about 4 GB, with progress and Cancel, and it keeps going if they leave Settings). If the Foundry Local app is already installed, its model cache (`~\.foundry\cache\models`) is reused instead of downloading a second copy. AI runtime files live in `%LOCALAPPDATA%\WindowsJournal\AI` (`WINDOWSJOURNAL_AI` overrides this for testing the download from scratch).

   Measured on a Ryzen 9 5900X + RTX 3060: the model is prepared in the background after launch (~15-25s), then Format/Reflect/Inspire take 1–3 seconds. Left alone, the SDK re-downloads every GPU runtime on every launch, including ones that can never load (CUDA without NVIDIA's CUDA toolkit), and that download can stall. So Journal registers runtimes one at a time with a 2-minute limit each, tries CUDA last, and records ones that genuinely fail to load in `AI\unusable-eps.txt` so they're skipped from then on. Delete that file to retry them (for example, after installing the CUDA toolkit).

   SDK notes: `OpenAIChatClient` is marked obsolete in favor of `ChatSession`, but in 2.2.1 `ChatSession` took ~47s for a reply `OpenAIChatClient` returns in ~2s, so Journal keeps the older API (warning suppressed with a comment). The package is pinned; switch when a later SDK fixes `ChatSession`'s speed.

AI errors (never journal text) are written to `ai.log`, and unexpected crashes to `crash.log`, in the data folder.

Without either, Fix errors falls back to rule-based cleanup (`TextFormatter`) and suggestions use the built-in prompt list.

## Before you share or publish

The welcome slideshow's backdrop is Microsoft's Windows 11 "Bloom" wallpaper (`Assets/Source/bloom-light.jpg` and `bloom-dark.jpg`), used here for a personal build. Microsoft doesn't license its wallpapers for redistribution inside other apps, so replace them with original artwork before sharing the app or submitting it to the Microsoft Store:

1. Put the new light and dark images in `Assets/Source/` with the same names (any size; keep the subject roughly centered, since the slides show the middle band).
2. Run `powershell -ExecutionPolicy Bypass -File Assets\Source\make-welcome-backgrounds.ps1`. It writes the softly blurred 1280-px versions the app ships in `Assets/Welcome/` (pass `-Radius` to change the softness; default 14). Files in `Assets/Source/` aren't shipped.

## Project layout

```
App.xaml(.cs)          Theme resources, startup
MainWindow.xaml(.cs)   Title bar, navigation, search, Windows Hello lock, welcome slideshow launch
Pages/                 JournalPage, EntryPage, InsightsPage, SettingsPage
Controls/              WelcomeDialog (first-run slideshow)
Models/                JournalEntry, Mood, grouping types
Services/              JournalStore (encrypted storage), DataProtection (DPAPI), SettingsService, SuggestionService,
                       AIService (Windows AI / Foundry Local), TextFormatter (non-AI fallback)
Helpers/               x:Bind helper functions
Assets/                App icon; Assets/Source/make-icon.ps1 regenerates it
```

## Ideas for next steps

- Richer suggestions from Photos / calendar / recent activity (opt-in, on-device)
- Dictation in more languages (today's voice model is English only; `nemotron-3.5-asr-streaming` and Whisper are multilingual)
- MVVM with CommunityToolkit.Mvvm once the pages grow

Done: Share to Journal (`Services\SharedContent.cs`: text, links and pictures from another app's Share become a new draft entry), drawing (`Controls\SketchDialog.cs`, editor > Draw: pen, touch or mouse, saved as an encrypted picture with the entry; the Windows App SDK has no InkCanvas, so there's no handwriting recognition), automatic updates (`Tools\Build-Msix.ps1` writes an `.appinstaller`; people who install from it get updates from the latest GitHub release), two widgets for the Widgets board and lock screen (`Widgets\`: Journal prompt shows a built-in prompt, and Journal progress shows the days written this week and any streak of 2 days or more; both have a Write button and never show entry text; they aren't customizable because on Windows build 26340 the board opens its Customize window but never calls `OnCustomizationRequested`; a windowless helper serves it, staying up while a widget is pinned and exiting within about a minute once none are), encrypted automatic backups (`Services\BackupService.cs`, `BackupCrypto.cs`, Settings > Your data > Automatic backups: `.journalbackup` files in a chosen folder, AES-256-GCM with a key derived by HKDF from a random 160-bit secret, shown once as a recovery key and remembered on the PC with DPAPI, so they're safe in OneDrive and open on any PC with the key; keeps the newest 5, deleting only its own files; restoring goes through Import, which only adds entries, so this is backup and restore rather than two-way sync; built in memory, so journals over about 1.5 GB need Export instead), automatic lock (Settings > Lock automatically: with Windows Hello on, the window locks after 1 to 30 minutes in the background, default 5, saving the open entry first; Hello opens by itself when Journal starts locked or you return to it, and canceling leaves the Unlock button), a clickable calendar (Insights: a marked day opens its entry, or lists them if there are several), topic and mood filters on the entry list, focus areas (`Controls\FocusPicker.cs`, in the welcome slideshow and Settings > Focus areas: picked topics are saved in `FocusAreas`, up to 3 of 4 built-in suggestions come from them, AI suggestions are asked to lean toward them, and Insights counts this month's entries for each), drafts and autosave (the editor saves after a 2-second pause in typing; new entries stay `IsDraft` until Done, listed under Drafts and left out of streaks, Insights, reminders, the widget and AI analysis), automatic insights (`Services\EntryInsights.cs`: after saving, on-device AI detects mood and one of nine topics and writes a reflection, shown on timeline cards and in the editor; hand-picked values and removed reflections are respected, unchanged entries aren't re-analyzed, older entries are filled in up to 20 per launch, and Settings > Automatic insights turns it off; Insights shows the topic breakdown), rich text (`RichEditBox` with bold, italic, underline, headings and lists; `Body` stays plain text for search, previews and AI, and formatting is saved as Markdown in `RichBody` only when an entry has any, because RTF stores colors that break between light and dark mode, so older entries need no conversion. Pasted text comes in plain. AI Format works line by line on formatted entries, so headings and lists can't be lost, and a line whose styling the model drops keeps its original text), encryption at rest (always on, not just with the lock), AI weekly reflections (Insights > Your week), MSIX packaging, full backups (Settings > Export: a .zip with `entries.json`, decrypted `photos/`, and a README; "Entries only (JSON)" is still offered), restoring them (Settings > Import: only adds entries, matched by ID, so nothing is deleted or overwritten and importing twice is harmless; shows what it will add before doing it; photos are re-encrypted), dictation (editor > Dictate: live, on-device speech-to-text with `nemotron-speech-streaming-en-0.6b` through the Foundry Local SDK's live transcription, ~700 MB one-time download, text only, no audio kept; microphone via NAudio; set `WINDOWSJOURNAL_TEST_AUDIO` to a 16 kHz mono WAV to test the unpackaged build without a mic), and daily reminders (Settings > Reminders: scheduled notifications delivered by Windows even when Journal is closed, skipped on days you've already written; "Write now" opens a new entry).
