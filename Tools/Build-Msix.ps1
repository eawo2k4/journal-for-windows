# Builds a signed release in bin\Msix, ready to attach to a GitHub release:
#   JournalForWindows-x64.msix          the app
#   JournalForWindows.appinstaller      install this one: Windows then checks it for updates
#   JournalForWindows.cer               what people trust once, because the certificate is self-signed
# File names stay the same in every release, so "releases/latest/download" always points at the newest one.
# Uses the signing certificate in your Windows certificate store; add -CreateCertificate the very first time.
# The certificate's private key must stay private and must not change between releases, or updates won't install
# over existing copies. Back it up with Export-PfxCertificate.
# Usage: powershell -ExecutionPolicy Bypass -File Tools\Build-Msix.ps1
param(
    [switch]$CreateCertificate,
    # Where the release files will be downloaded from.
    [string]$DownloadUrl = 'https://github.com/eawo2k4/journal-for-windows/releases/latest/download',
    [int]$HoursBetweenUpdateChecks = 24
)
$ErrorActionPreference = 'Stop'

$project = Split-Path $PSScriptRoot -Parent
$manifest = [xml](Get-Content "$project\Package.appxmanifest")
$publisher = $manifest.Package.Identity.Publisher
$version = $manifest.Package.Identity.Version

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
if (-not $cert) {
    if (-not $CreateCertificate) { throw "No signing certificate for $publisher. Restore yours, or run with -CreateCertificate if this is the first release." }
    # 2.5.29.37 = code signing; 2.5.29.19 = not a certificate authority, so trusting it can't vouch for anything else it signs as a CA.
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -FriendlyName 'Journal for Windows signing' `
        -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(10) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Write-Host "Created signing certificate $($cert.Thumbprint)"
}

$out = "$project\bin\Msix"
$build = "$project\bin\MsixBuild\"
Remove-Item $out, $build -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $out | Out-Null

# Self-contained, so people don't need to install .NET first. Its own output folder, because a Journal installed
# by Install Journal.cmd runs from bin\Packaged, and its widget helper can be holding files there.
dotnet publish "$project\WindowsJournal.csproj" -c Release -r win-x64 --self-contained true -p:Packaged=true `
    "-p:OutputPath=bin\MsixBuild\output\" -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly `
    -p:AppxPackageSigningEnabled=true "-p:PackageCertificateThumbprint=$($cert.Thumbprint)" "-p:AppxPackageDir=$build"
if ($LASTEXITCODE -ne 0) { throw 'The build failed. See the messages above.' }

$msix = Get-ChildItem $build -Recurse -Filter *.msix | Select-Object -First 1
Copy-Item $msix.FullName "$out\JournalForWindows-x64.msix"
Export-Certificate -Cert $cert -FilePath "$out\JournalForWindows.cer" | Out-Null

$name = $manifest.Package.Identity.Name
[IO.File]::WriteAllText("$out\JournalForWindows.appinstaller", @"
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018"
              Version="$version"
              Uri="$DownloadUrl/JournalForWindows.appinstaller">
  <MainPackage Name="$name"
               Publisher="$publisher"
               Version="$version"
               ProcessorArchitecture="x64"
               Uri="$DownloadUrl/JournalForWindows-x64.msix" />
  <UpdateSettings>
    <OnLaunch HoursBetweenUpdateChecks="$HoursBetweenUpdateChecks" ShowPrompt="true" UpdateBlocksActivation="false" />
    <AutomaticBackgroundTask />
  </UpdateSettings>
</AppInstaller>
"@, (New-Object Text.UTF8Encoding($false)))

Remove-Item $build -Recurse -Force
Get-ChildItem $out | ForEach-Object { "$($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB)" }
