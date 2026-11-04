# Builds Journal as an MSIX package and installs it for the current user. Run by "Install Journal.cmd".
# On the first install it also copies the journal from the unpackaged version into the installed app,
# because the installed app can't see that folder itself (Windows virtualizes AppData for packaged apps).
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '..\WindowsJournal.csproj'
$manifest = Join-Path $PSScriptRoot '..\bin\Packaged\Release\net10.0-windows10.0.22621.0\win-x64\AppxManifest.xml'

Write-Host 'Building Journal...'
Get-Process WindowsJournal -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build $project -c Release -p:Packaged=true -v quiet -nologo
if ($LASTEXITCODE -ne 0) { throw 'The build failed. See the messages above.' }

Write-Host 'Installing...'
Add-AppxPackage -Register $manifest -ForceUpdateFromAnyVersion
$package = Get-AppxPackage -Name WindowsJournal

# Bring over an existing journal once, only if the installed app doesn't have one yet. The original stays put.
$state = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState"
$old = Join-Path $env:LOCALAPPDATA 'WindowsJournal'
$hasJournal = (Test-Path "$state\entries.dat") -or (Test-Path "$state\entries.json")
$oldJournal = @('entries.dat', 'entries.json') | ForEach-Object { Join-Path $old $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $hasJournal -and $oldJournal) {
    New-Item -ItemType Directory "$state\Photos" -Force | Out-Null
    Copy-Item $oldJournal $state
    if (Test-Path "$old\settings.json") { Copy-Item "$old\settings.json" $state -Force }
    if (Test-Path "$old\Photos") { Copy-Item "$old\Photos\*" "$state\Photos" -ErrorAction SilentlyContinue }
    Write-Host "Copied your journal into the installed app (the original is still in $old)."
}

Write-Host 'Done. Opening Journal; you''ll also find it in the Start menu.'
Start-Process "shell:AppsFolder\$($package.PackageFamilyName)!App"
