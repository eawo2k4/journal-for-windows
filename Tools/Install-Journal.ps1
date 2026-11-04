# Builds a signed package and installs it on this PC, keeping your journal. The PC must already trust the certificate,
# and a copy installed by the .appinstaller file gets updated too.
# Usage: powershell -ExecutionPolicy Bypass -File Tools\Install-Journal.ps1
$ErrorActionPreference = 'Stop'

& "$PSScriptRoot\Build-Msix.ps1"

# Closing normally lets an open entry save. A window that stays open is left alone, because it may be asking something.
Get-Process WindowsJournal -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | ForEach-Object { [void]$_.CloseMainWindow() }
Start-Sleep 5
if (Get-Process WindowsJournal -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0) {
    throw 'Journal is still open. Close it, then run this again.'
}

# Only the windowless widget helper can be left by now, and it's safe to stop.
Add-AppxPackage -Path (Join-Path $PSScriptRoot '..\bin\Msix\JournalForWindows-x64.msix') -ForceUpdateFromAnyVersion -ForceApplicationShutdown
Write-Host "Installed Journal $((Get-AppxPackage WindowsJournal).Version)."
