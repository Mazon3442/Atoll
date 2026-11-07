# Run on the target OS. Output is isolated from the running developer app.
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')]
    [string]$RuntimeIdentifier = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier,
    [string]$OutputRoot,
    [string]$RepoUrl = 'https://github.com/Mazon3442/Atoll',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputRoot) { $OutputRoot = Join-Path $root "artifacts/release/$RuntimeIdentifier/$Version" }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$hostOs = if ($env:OS -eq 'Windows_NT') { 'win' } elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) { 'osx' } else { 'linux' }
if ($RuntimeIdentifier.Split('-')[0] -ne $hostOs) { throw "Build $RuntimeIdentifier on its native OS. Cross-publishing alone does not produce a tested installer." }
if (Test-Path $OutputRoot) { throw "Output already exists: $OutputRoot. Choose a fresh -OutputRoot to avoid packaging stale files." }
if ($Publish -and -not $env:GH_TOKEN -and -not $env:VPK_TOKEN) { throw 'Set GH_TOKEN (or VPK_TOKEN) before publishing.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$publishDir = Join-Path $OutputRoot 'publish'
$hookDir = Join-Path $OutputRoot 'hook'
$packages = Join-Path $OutputRoot 'packages'
$build = Join-Path $OutputRoot 'build'
New-Item -ItemType Directory -Path $packages | Out-Null
if ($hostOs -eq 'win') { & (Join-Path $PSScriptRoot 'build-identity.ps1') -Version $Version }
# Windows ships the WPF island (the app `dotnet run --project src/Atoll.App` starts); Linux and macOS ship the Avalonia port.
$appProject = if ($hostOs -eq 'win') { 'src/Atoll.App/Atoll.App.csproj' } else { 'src/Atoll.Desktop/Atoll.Desktop.csproj' }
dotnet publish (Join-Path $root $appProject) -c Release -r $RuntimeIdentifier --self-contained true --artifacts-path $build -o $publishDir "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
dotnet publish (Join-Path $root 'src/Atoll.ClaudeHook/Atoll.ClaudeHook.csproj') -c Release -r $RuntimeIdentifier --self-contained true --artifacts-path $build -o $hookDir "-p:Version=$Version" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw 'Hook publish failed.' }
$suffix = if ($hostOs -eq 'win') { '.exe' } else { '' }
Copy-Item -LiteralPath (Join-Path $hookDir "Atoll.ClaudeHook$suffix") -Destination $publishDir
switch ($hostOs) {
    'win' {
        $tools = Join-Path $OutputRoot 'tools'
        dotnet tool install vpk --version 1.2.161 --tool-path $tools
        if ($LASTEXITCODE -ne 0) { throw 'Velopack tool install failed.' }
        & (Join-Path $tools 'vpk.exe') pack --packId Atoll --packVersion $Version --runtime win-x64 --packDir $publishDir --mainExe Atoll.exe --packTitle ATOLL --packAuthors Mazon3442 --icon (Join-Path $PSScriptRoot 'identity/Assets/Atoll.ico') --outputDir $packages
        if ($LASTEXITCODE -ne 0) { throw 'Windows packaging failed.' }
    }
    'linux' {
        & bash (Join-Path $PSScriptRoot 'package-linux.sh') $Version $publishDir $packages
        if ($LASTEXITCODE -ne 0) { throw 'Linux packaging failed.' }
    }
    'osx' {
        & bash (Join-Path $PSScriptRoot 'package-macos.sh') $Version $RuntimeIdentifier $publishDir $packages
        if ($LASTEXITCODE -ne 0) { throw 'macOS packaging failed.' }
    }
}
# A freshly written package can be held open for a moment by the OS (macOS scans new disk images), so retry briefly.
function Get-PackageHash([string]$Path) {
    for ($attempt = 1; ; $attempt++) {
        try {
            $stream = [IO.File]::Open($Path, 'Open', 'Read', 'Read')
            try {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { return (($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) -join '') } finally { $sha.Dispose() }
            }
            finally { $stream.Dispose() }
        }
        catch [System.IO.IOException] {
            if ($attempt -ge 30) { throw }
            Start-Sleep -Seconds 1
        }
    }
}
$hashes = Get-ChildItem -LiteralPath $packages -File | Sort-Object Name | ForEach-Object {
    "$(Get-PackageHash $_.FullName)  $($_.Name)"
}
[IO.File]::WriteAllLines((Join-Path $packages "SHA256SUMS-$RuntimeIdentifier.txt"), [string[]]$hashes, [Text.UTF8Encoding]::new($false))
Write-Output "Release packages: $packages"
if ($Publish) {
    if (-not $env:GH_TOKEN) { $env:GH_TOKEN = $env:VPK_TOKEN }
    $commit = (git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the release commit.' }
    $files = @(Get-ChildItem -LiteralPath $packages -File | ForEach-Object FullName)
    gh release create "v$Version" @files --repo $RepoUrl --target $commit --title "ATOLL v$Version" --generate-notes --draft
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the draft release. No existing assets were overwritten.' }
}
