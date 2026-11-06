# One-time setup so Atoll can read Windows notifications. Run it yourself; it asks for admin (UAC).
#   1. Trusts packaging\out\AtollDev.cer in Local Machine > Trusted People. That lets Windows install
#      packages signed with this one certificate; it is not a root CA and trusts nothing else.
#   2. Registers packaging\out\Atoll.Identity.msix with Atoll.exe's folder as its external location.
# Undo: packaging\remove-identity.ps1
param(
    [string]$AppDir = (Join-Path $PSScriptRoot "..\src\Atoll.App\bin\Debug\net10.0-windows10.0.19041.0")
)
$ErrorActionPreference = "Stop"
$AppDir = (Resolve-Path $AppDir).Path
$cer = Join-Path $PSScriptRoot "out\AtollDev.cer"
$msix = Join-Path $PSScriptRoot "out\Atoll.Identity.msix"
foreach ($f in @($cer, $msix, (Join-Path $AppDir "Atoll.exe"))) {
    if (-not (Test-Path $f)) { throw "Missing $f. Build Atoll and run packaging\build-identity.ps1 first." }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -Wait -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-AppDir", "`"$AppDir`"")
    return
}

$thumb = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $thumb)) {
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    "Trusted certificate $thumb (Local Machine > Trusted People)"
}

Get-AppxPackage -Name Atoll | Remove-AppxPackage   # replace any earlier registration
Add-AppxPackage -Path $msix -ExternalLocation $AppDir
"Registered ATOLL identity for $AppDir. Restart ATOLL, then allow notification access in its settings."
Read-Host "Press Enter to close"
