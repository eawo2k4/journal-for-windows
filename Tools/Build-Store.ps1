# Builds bin\Store\JournalForWindows-Store-x64.msix to upload to Partner Center. It's left unsigned, because the Store
# signs it. The Store assigns its own identity, so that's applied to the manifest only for this build; the GitHub
# build (Build-Msix.ps1) keeps its own, and the two install as separate apps.
# Usage: powershell -ExecutionPolicy Bypass -File Tools\Build-Store.ps1
$ErrorActionPreference = 'Stop'

# From Partner Center > Product Identity.
$storeName = 'eawo2k4.JournalForWindows'
$storePublisher = 'CN=5817E141-61B2-4D41-99CB-3025FECF384C'
$reservedName = 'Journal For Windows'

$project = Split-Path $PSScriptRoot -Parent
$manifestPath = "$project\Package.appxmanifest"
$original = [IO.File]::ReadAllText($manifestPath)
$utf8 = New-Object Text.UTF8Encoding($false)

$out = "$project\bin\Store"
$build = "$project\bin\StoreBuild\"
Remove-Item $out, $build -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $out | Out-Null

try {
    $manifest = $original `
        -replace 'Identity Name="[^"]*"', "Identity Name=`"$storeName`"" `
        -replace 'Publisher="CN=[^"]*"', "Publisher=`"$storePublisher`"" `
        -replace '(<Properties>\s*)<DisplayName>[^<]*</DisplayName>', "`$1<DisplayName>$reservedName</DisplayName>"
    [IO.File]::WriteAllText($manifestPath, $manifest, $utf8)

    # Self-contained, so people don't need to install .NET first.
    dotnet publish "$project\WindowsJournal.csproj" -c Release -r win-x64 --self-contained true -p:Packaged=true `
        "-p:OutputPath=bin\StoreBuild\output\" -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never `
        -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxPackageSigningEnabled=false "-p:AppxPackageDir=$build"
    if ($LASTEXITCODE -ne 0) { throw 'The build failed. See the messages above.' }
}
finally {
    [IO.File]::WriteAllText($manifestPath, $original, $utf8)
}

$msix = Get-ChildItem $build -Recurse -Filter *.msix | Select-Object -First 1
Copy-Item $msix.FullName "$out\JournalForWindows-Store-x64.msix"
Remove-Item $build -Recurse -Force
Get-ChildItem $out | ForEach-Object { "$($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB)" }
