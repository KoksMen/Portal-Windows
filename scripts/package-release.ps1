param(
    [string]$PublishDir = "publish",
    [string]$DistDir = "dist",
    [string]$Version = "1.8.7"
)

$ErrorActionPreference = "Stop"

Write-Host "=== Step 1: Preparing clean distribution folder '$DistDir' ===" -ForegroundColor Cyan
if (Test-Path $DistDir) {
    Remove-Item -Recurse -Force $DistDir
}
New-Item -ItemType Directory -Path $DistDir | Out-Null
New-Item -ItemType Directory -Path (Join-Path $DistDir "CredentialProvider") | Out-Null
New-Item -ItemType Directory -Path (Join-Path $DistDir "Updater") | Out-Null
New-Item -ItemType Directory -Path (Join-Path $DistDir "runtimes\win\lib\net9.0") | Out-Null

Write-Host "=== Step 2: Copying required files without duplicates or excess ===" -ForegroundColor Cyan

# 2.1 Copy Root Files (excluding .pdb, CredentialProvider, Updater, and runtimes)
$rootExcluded = @(
    "CredentialProvider", "Updater", "runtimes",
    "Portal.CredentialProvider.comhost.dll", "Portal.CredentialProvider.dll",
    "Portal.CredentialProvider.deps.json", "Portal.CredentialProvider.runtimeconfig.json",
    "Portal.Updater.exe", "Portal.Updater.dll",
    "Portal.Updater.deps.json", "Portal.Updater.runtimeconfig.json"
)

$rootFiles = Get-ChildItem -Path $PublishDir -File | Where-Object {
    $_.Extension -ne ".pdb" -and ($rootExcluded -notcontains $_.Name)
}

foreach ($f in $rootFiles) {
    Copy-Item -Path $f.FullName -Destination (Join-Path $DistDir $f.Name) -Force
    Write-Host "  [Root] Copied: $($f.Name)" -ForegroundColor DarkGray
}

# 2.2 Copy CredentialProvider Files (excluding .pdb and linux runtimes)
$cpFiles = Get-ChildItem -Path (Join-Path $PublishDir "CredentialProvider") -File | Where-Object {
    $_.Extension -ne ".pdb"
}

foreach ($f in $cpFiles) {
    Copy-Item -Path $f.FullName -Destination (Join-Path "$DistDir\CredentialProvider" $f.Name) -Force
    Write-Host "  [CredentialProvider] Copied: $($f.Name)" -ForegroundColor DarkGray
}

# 2.3 Copy Updater Files (excluding .pdb and linux runtimes)
$upFiles = Get-ChildItem -Path (Join-Path $PublishDir "Updater") -File | Where-Object {
    $_.Extension -ne ".pdb"
}

foreach ($f in $upFiles) {
    Copy-Item -Path $f.FullName -Destination (Join-Path "$DistDir\Updater" $f.Name) -Force
    Write-Host "  [Updater] Copied: $($f.Name)" -ForegroundColor DarkGray
}

# 2.4 Copy Windows runtime System.Management.dll
$winRuntimeSrc = Join-Path $PublishDir "runtimes\win\lib\net9.0\System.Management.dll"
if (Test-Path $winRuntimeSrc) {
    Copy-Item -Path $winRuntimeSrc -Destination (Join-Path "$DistDir\runtimes\win\lib\net9.0" "System.Management.dll") -Force
    Write-Host "  [Runtime] Copied: runtimes\win\lib\net9.0\System.Management.dll" -ForegroundColor DarkGray
}

Write-Host "=== Step 3: Authenticode Signing of all binaries ===" -ForegroundColor Cyan
$signTool = "E:\Windows Kits\10\bin\10.0.26100.0\x86\signtool.exe"
$commonCertSha1 = "7492EFCAEAEFD328F405F05E0A6BCBF12402C7A8" # Portal Common Files
$tsaUrl = "http://timestamp.acs.microsoft.com"

# Check every binary (.exe, .dll) in $DistDir. If unsigned, sign with Common Cert.
$allBinaries = Get-ChildItem -Path $DistDir -Recurse -Include *.exe, *.dll

foreach ($bin in $allBinaries) {
    $sig = Get-AuthenticodeSignature $bin.FullName
    if ($sig.Status -eq 'NotSigned') {
        Write-Host "  Signing unsigned binary: $($bin.FullName.Replace((Resolve-Path $DistDir).Path, ''))" -ForegroundColor Yellow
        & $signTool sign /sha1 $commonCertSha1 /tr $tsaUrl /td sha256 /fd sha256 $bin.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to sign $($bin.FullName)"
        }
    } else {
        $subject = $sig.SignerCertificate.Subject
        Write-Host "  Already signed: $($bin.Name) ($subject)" -ForegroundColor Green
    }
}

Write-Host "=== Step 4: Creating ZIP archive ===" -ForegroundColor Cyan
$zipName = "PortalWin-$Version-win-x64.zip"
$zipPath = Join-Path (Get-Location) $zipName
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

# Compress contents of $DistDir into root of zip
$distItems = (Get-ChildItem -Path $DistDir).FullName
Compress-Archive -Path $distItems -DestinationPath $zipPath -CompressionLevel Optimal -Force

$zipItem = Get-Item $zipPath
Write-Host "  Created ZIP: $($zipItem.FullName) ($([math]::Round($zipItem.Length / 1MB, 2)) MB)" -ForegroundColor Green

Write-Host "=== Step 5: Computing SHA-256 Hash ===" -ForegroundColor Cyan
$hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLower()
$sha256Content = "$hash  $zipName"
$sha256Path = "$zipPath.sha256"
[System.IO.File]::WriteAllText($sha256Path, $sha256Content)
Write-Host "  Created SHA256 file: $sha256Path" -ForegroundColor Green
Write-Host "  Hash: $hash" -ForegroundColor Yellow

Write-Host "=== Step 6: Creating Detached PKCS#7 / CMS Signature ===" -ForegroundColor Cyan
Add-Type -AssemblyName System.Security
$hostCertSha1 = "F1A00AC831420B2AADEFD0BCCDFD99FDAF246D7B" # Portal Host cert
$cert = Get-Item "Cert:\CurrentUser\My\$hostCertSha1"

$zipBytes = [System.IO.File]::ReadAllBytes($zipPath)
$contentInfo = [System.Security.Cryptography.Pkcs.ContentInfo]::new($zipBytes)
$signedCms = [System.Security.Cryptography.Pkcs.SignedCms]::new($contentInfo, $true) # detached signature
$cmsSigner = [System.Security.Cryptography.Pkcs.CmsSigner]::new($cert)
$cmsSigner.DigestAlgorithm = [System.Security.Cryptography.Oid]::new("2.16.840.1.101.3.4.2.1") # SHA-256
$signedCms.ComputeSignature($cmsSigner, $false)
$sigBytes = $signedCms.Encode()

$sigPath = "$zipPath.sig"
[System.IO.File]::WriteAllBytes($sigPath, $sigBytes)
Write-Host "  Created detached signature file: $sigPath ($($sigBytes.Length) bytes)" -ForegroundColor Green

# Verify detached signature
$verifyCms = [System.Security.Cryptography.Pkcs.SignedCms]::new($contentInfo, $true)
$verifyCms.Decode($sigBytes)
$verifyCms.CheckSignature($true)
Write-Host "  Detached CMS signature successfully verified!" -ForegroundColor Green

Write-Host "`n=== Package Summary ===" -ForegroundColor Magenta
Write-Host "Host Executable : $(Resolve-Path "$DistDir\Portal.Host.exe")"
Write-Host "ZIP Archive     : $zipPath"
Write-Host "SHA256 File     : $sha256Path"
Write-Host "Signature File  : $sigPath"
