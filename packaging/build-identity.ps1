# Builds and signs packaging\out\Atoll.Identity.msix (developer machine; needs the Windows SDK).
# Creates the signing certificate "CN=Atoll Dev" in your personal store (Cert:\CurrentUser\My) on
# first run and exports its public part to packaging\out\AtollDev.cer. Changes no trust settings.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "out"
$stage = Join-Path $out "stage"
$subject = "CN=Atoll Dev"

$sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "makeappx.exe") } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdk) { throw "Windows SDK not found (makeappx.exe). Install the Windows 10/11 SDK." }
$makeappx = Join-Path $sdk.FullName "makeappx.exe"
$signtool = Join-Path $sdk.FullName "signtool.exe"

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature `
        -FriendlyName "Atoll package signing (local)" -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(3) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    "Created signing certificate $($cert.Thumbprint)"
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $root "identity\*") $stage -Recurse
Export-Certificate -Cert $cert -FilePath (Join-Path $out "AtollDev.cer") | Out-Null

$msix = Join-Path $out "Atoll.Identity.msix"
& $makeappx pack /o /nv /d $stage /p $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)" }
& $signtool sign /q /fd SHA256 /sha1 $cert.Thumbprint /s My $msix
if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
"Built $msix (signed by $subject, $($cert.Thumbprint))"
