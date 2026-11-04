# Privacy

Journal for Windows is built so that your writing stays yours. This page explains what happens to your data.

## What stays on your PC

- **Your entries and photos** are stored in the app's private folder on your PC, encrypted with your Windows account. Other accounts on the PC can't read them.
- **AI features** process your entries on your own device. Journal doesn't send your entries to any AI service.
- **Dictation** turns your voice into text on your PC, and no audio is kept.
- **Logs:** if something goes wrong, Journal writes error details to a log file in its private folder. These stay on your PC, and aren't meant to contain your entries.

Journal has no account, no ads, and no analytics of its own.

## When Journal uses the internet

| What | When | What it involves |
|---|---|---|
| AI model | You select Download in Settings, and when the AI starts up | Downloads Microsoft's Phi-4 mini model and its runtime files using Microsoft's Foundry Local SDK |
| Voice model | You first use Dictate | Downloads the speech model the same way |
| Updates | If you installed from the `.appinstaller` file, Windows checks for you | Windows downloads the update file and any new version from GitHub |

These requests go to Microsoft and GitHub, and those services see normal connection details such as your IP address. They're governed by those companies' own privacy policies. Journal doesn't send your entries in them. Journal's developer doesn't receive any of this.

## When your entries can leave your PC

Only when you choose to:

- **Automatic backups** are encrypted and saved to the folder you pick. If that folder syncs, such as OneDrive, the encrypted backup goes with it. Anyone who has both a backup file and your recovery key can read it, so keep the key private.
- **Export** creates a copy that **isn't encrypted**. Keep it somewhere private.
- **Sharing or copying** text out of Journal is up to you.

## What's visible to other people

- **Widgets** can sit on your lock screen, where anyone near your PC can see them. They show a built-in prompt, whether you wrote today, the days you've written this week and your streak. They never show your entries.
- **Reminders** show a built-in prompt, not your entries.

## What the protection can't do

- Encryption protects your journal from other Windows accounts and from anyone who copies the files off your PC. It can't stop software that's already running under your own account.
- An open, unlocked Journal window can be read by anyone at your PC. The Windows Hello lock and automatic lock in Settings help with this.
- If you lose access to your Windows account and have no backup and recovery key, your entries can't be recovered. Nobody can reset them for you.

## Questions

This is a hobby project without a legal team. If something here looks wrong, please [open an issue](https://github.com/eawo2k4/journal-for-windows/issues).
