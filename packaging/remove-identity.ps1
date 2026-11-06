# Undoes setup-identity.ps1: unregisters the Atoll identity package and removes the certificate
# from Local Machine > Trusted People. Asks for admin (UAC).
$ErrorActionPreference = "Stop"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -Wait -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    return
}
Get-AppxPackage -Name Atoll | Remove-AppxPackage
Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq "CN=Atoll Dev" | Remove-Item
"Removed the ATOLL identity package and its certificate trust."
Read-Host "Press Enter to close"
