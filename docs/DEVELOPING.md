# Developer guide

Journal is a WinUI 3 app on the Windows App SDK and .NET 10, built the way an inbox Windows app would be.

## Run it

You'll need the .NET 10 SDK on Windows 11.

```powershell
dotnet run
```

The default build is **unpackaged**, so no install, Developer Mode or signing is needed. Reminders and Windows AI need package identity, so they're only available in the packaged build. Keep the project in a short path, because the XAML compiler fails with `WMC9999` when paths exceed 260 characters.

To test with a throwaway journal, point these at other folders. Your real journal isn't touched:

| Variable | What it does |
|---|---|
| `WINDOWSJOURNAL_DATA` | Folder for the journal and settings |
| `WINDOWSJOURNAL_AI` | Folder for AI runtime files, for testing the download from scratch |
| `WINDOWSJOURNAL_TEST_AUDIO` | A 16 kHz mono WAV to use in place of a microphone (unpackaged only) |

## Build a signed package

```powershell
powershell -ExecutionPolicy Bypass -File Tools\Build-Msix.ps1 -CreateCertificate
```

This writes `JournalForWindows-x64.msix`, `JournalForWindows.appinstaller` and `JournalForWindows.cer` to `bin\Msix`. Leave out `-CreateCertificate` once you have a certificate.

- **To build your own copy:** the `Publisher` in `Package.appxmanifest` must match your certificate's subject, and `-DownloadUrl` should point to where you'll host the files.
- **To release:** bump `Version` in `Package.appxmanifest` and `<Version>` in the `.csproj`, build, and attach the three files to a new GitHub release with their names unchanged. The update file points to `releases/latest/download`, so old installs find new versions by themselves.
- **The signing key:** it must stay private and the same between releases, or updates won't install over existing copies. Back it up with `Export-PfxCertificate`, and never commit it. `.gitignore` blocks `.pfx` files.

`Tools\Install-Journal.ps1` builds and installs a signed package on your own PC, once your PC trusts the certificate.

## How it fits together

```
App.xaml(.cs)          Theme resources, startup, activation (reminders, links, Share)
Program.cs             Custom Main: single instance, and the widget helper mode
MainWindow.xaml(.cs)   Title bar, navigation, search, Windows Hello lock, automatic lock
Pages/                 JournalPage, EntryPage, InsightsPage, SettingsPage
Controls/              WelcomeDialog (tour), FocusPicker, SketchDialog (Draw)
Models/                JournalEntry, moods, topics
Services/              JournalStore, DataProtection (DPAPI), BackupService, BackupCrypto, AIService,
                       EntryInsights, SuggestionFeed, SuggestionService, VoiceService, ReminderService,
                       SharedContent, SettingsService, TextFormatter
Widgets/               Widget content, and the COM provider the Widgets board talks to
Helpers/               RichText (Markdown conversion), x:Bind helpers
Assets/Source/         Scripts that draw the icon, tour art and widget previews
Tools/                 Build-Msix.ps1, Install-Journal.ps1
```

## Design notes

**Storage.** Entries live in one DPAPI-encrypted file (`entries.dat`), and photos are encrypted beside it. Saves go to a temp file and swap, so a crash mid-save can't truncate the journal. A file that can't be read is moved aside rather than overwritten.

**Rich text.** `Body` stays plain text for search, previews and AI. Formatting is saved as Markdown in `RichBody` only when an entry has any, because RTF stores colors that break between light and dark mode. AI edits formatted entries one line at a time, so headings and lists can't be lost.

**Backups.** `.journalbackup` files use AES-256-GCM, with a key derived by HKDF from a random 160-bit secret. That secret is shown once as the recovery key and remembered on the PC with DPAPI. Restoring goes through Import, which only adds entries, so this is backup and restore rather than two-way sync. Backups are built in memory, so journals over about 1.5 GB need Export instead.

**AI.** `AIService` uses Windows AI (Phi Silica) on Copilot+ PCs and otherwise hosts Phi-4 mini in-process with the Foundry Local SDK, which is pinned because `ChatSession` in 2.2.1 was far slower than `OpenAIChatClient`. Small models don't always follow instructions, so replies are checked: reflections are filtered for first-person talk and advice, and rewrites that change the length too much are rejected, with one retry. `SuggestionFeed` makes AI suggestions ahead in batches, so New ideas never waits on the model. Background work and requests share one model, so a request can wait its turn.

**Widgets.** There are two definitions, and they share a helper process that Windows starts with `-RegisterProcessAsComServer`. Content is built in `WidgetContent` from dates and built-in prompts only, because widgets can be on the lock screen. They aren't customizable: on Windows build 26340 the board opens its Customize window but never calls `OnCustomizationRequested`, even though `IWidgetProvider2` is exposed. It's worth retrying on later builds. `GetWidgetInfos` returns null, not an empty list, when nothing is pinned.

**Share.** `SharedContent` copies everything out of the share before reporting it complete, because the sending app can release its data after that.

**Draw.** The Windows App SDK has no `InkCanvas`, so strokes are drawn as shapes and rendered to a PNG, with no handwriting recognition.

## Testing

Tests so far have been run by hand against a throwaway journal, using UI Automation. There's no automated test suite in the repo yet, and that would be a welcome contribution.

## Ideas

- Tags, Print or PDF, voice notes that keep the recording, and entry templates
- More dictation languages, since today's voice model is English only
- An ARM64 package
- A Microsoft Store listing, which would remove the certificate step
- Opt-in, on-device suggestions from Photos or your calendar
