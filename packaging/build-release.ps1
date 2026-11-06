# Builds a Velopack release for ATOLL: Setup.exe, the update packages and the releases feed.
#
# Usage:
#   .\build-release.ps1 -Version 1.0.0
#   .\build-release.ps1 -Version 1.0.1 -Publish        # also create the GitHub release
#
# Uploading needs a GitHub personal access token (repo scope) in the VPK_TOKEN environment variable.
# The repo must already exist on GitHub; -Publish only attaches the packages to a release.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$RepoUrl = "https://github.com/Mazon3442/Atoll",
    [switch]$Publish
)
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\Atoll.App\Atoll.App.csproj"
$hookProject = Join-Path $root "src\Atoll.ClaudeHook\Atoll.ClaudeHook.csproj"
$publishDir = Join-Path $root "publish"
$hookDir = Join-Path $root "publish-hook"
$releasesDir = Join-Path $root "releases"
$icon = Join-Path $PSScriptRoot "identity\Assets\Atoll.ico"

"== 1/3 Publish the app (self-contained, win-x64) =="
dotnet publish $project -c Release -r win-x64 --self-contained -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

"== 2/3 Publish the Claude hook (self-contained, win-x64) and drop it next to Atoll.exe =="
# The hook runs on every Claude Code event; it must start without a separately installed runtime.
dotnet publish $hookProject -c Release -r win-x64 --self-contained -o $hookDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (hook) failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $hookDir "Atoll.ClaudeHook.exe") (Join-Path $publishDir "Atoll.ClaudeHook.exe") -Force

"== 3/3 Package with Velopack =="
$vpk = (Get-Command vpk -ErrorAction SilentlyContinue).Source
if (-not $vpk) {
    "Installing the vpk CLI (pin matches the Velopack package in Atoll.App.csproj)..."
    dotnet tool install --global vpk --version 1.2.161
    if ($LASTEXITCODE -ne 0) { throw "vpk install failed ($LASTEXITCODE)" }
    $vpk = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
}

$iconArg = @()
if (Test-Path $icon) { $iconArg = @("--icon", $icon) } else { "No icon at $icon; run packaging\make-logos.ps1 to generate one." }

& $vpk pack --packId Atoll --packVersion $Version --packDir $publishDir --mainExe Atoll.exe `
    --packTitle ATOLL --packAuthors "Mazon3442" @iconArg --outputDir $releasesDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

""
"Release $Version written to $releasesDir (Setup.exe, nupkg, portable zip, releases.win.json)."

if ($Publish) {
    if (-not $env:VPK_TOKEN) { throw "Set VPK_TOKEN to a GitHub token with repo scope to publish." }
    "Uploading to $RepoUrl ..."
    & $vpk upload github --repoUrl $RepoUrl --token $env:VPK_TOKEN --outputDir $releasesDir --publish --tag $Version
    if ($LASTEXITCODE -ne 0) { throw "vpk upload failed ($LASTEXITCODE)" }
    "Published release $Version."
}
else {
    "Run with -Publish to attach these files to a GitHub release (needs VPK_TOKEN)."
}
