param(
    [string]$PublishDir = "publish",
    [string]$DistDir = "dist",
    [string]$Version = "1.9.1"
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
    "CredentialProvider", "Updater", "runtimes", "win-x64",
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
$hostCertSha1   = "F1A00AC831420B2AADEFD0BCCDFD99FDAF246D7B" # Portal Host
$cpCertSha1     = "110025BF33D9D8EEE632AD6C4F43618A24069B22" # Portal Credential Provider
$upCertSha1     = "6B757026547640EBCDB08ACFD568F2A873D601F0" # Portal Updater
$tsaUrl         = "http://timestamp.acs.microsoft.com"

# Ensure solution-specific binaries are signed with dedicated certs
$specificSignings = @(
    @{ Path = Join-Path $DistDir "Portal.Host.exe"; Cert = $hostCertSha1 },
    @{ Path = Join-Path $DistDir "Portal.Host.dll"; Cert = $hostCertSha1 },
    @{ Path = Join-Path $DistDir "Portal.Common.dll"; Cert = $commonCertSha1 },
    @{ Path = Join-Path "$DistDir\CredentialProvider" "Portal.CredentialProvider.comhost.dll"; Cert = $cpCertSha1 },
    @{ Path = Join-Path "$DistDir\CredentialProvider" "Portal.CredentialProvider.dll"; Cert = $cpCertSha1 },
    @{ Path = Join-Path "$DistDir\CredentialProvider" "Portal.Common.dll"; Cert = $commonCertSha1 },
    @{ Path = Join-Path "$DistDir\Updater" "Portal.Updater.exe"; Cert = $upCertSha1 },
    @{ Path = Join-Path "$DistDir\Updater" "Portal.Updater.dll"; Cert = $upCertSha1 },
    @{ Path = Join-Path "$DistDir\Updater" "Portal.Common.dll"; Cert = $commonCertSha1 }
)

foreach ($target in $specificSignings) {
    if (Test-Path $target.Path) {
        & $signTool sign /sha1 $target.Cert /tr $tsaUrl /td sha256 /fd sha256 $target.Path | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to sign $($target.Path)"
        }
        Write-Host "  [Dedicated] Signed: $(Split-Path $target.Path -Leaf)" -ForegroundColor Green
    }
}

# Check every binary (.exe, .dll) in $DistDir. If unsigned, sign with Common Cert.
$allBinaries = Get-ChildItem -Path $DistDir -Recurse -Include *.exe, *.dll

foreach ($bin in $allBinaries) {
    $sig = Get-AuthenticodeSignature $bin.FullName
    if ($sig.Status -eq 'NotSigned') {
        Write-Host "  [Common] Signing unsigned binary: $($bin.FullName.Replace((Resolve-Path $DistDir).Path, ''))" -ForegroundColor Yellow
        & $signTool sign /sha1 $commonCertSha1 /tr $tsaUrl /td sha256 /fd sha256 $bin.FullName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to sign $($bin.FullName)"
        }
    } else {
        $subject = $sig.SignerCertificate.Subject
        Write-Host "  [Verified] Signed: $($bin.Name) ($subject)" -ForegroundColor Green
    }
}

Write-Host "=== Step 4: Creating ZIP archive with maximum compression ===" -ForegroundColor Cyan
$zipName = "PortalWin-$Version-win-x64.zip"
$zipPath = Join-Path (Get-Location) $zipName
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

# Compress contents of $DistDir into root of zip
$distItems = (Get-ChildItem -Path $DistDir).FullName
Compress-Archive -Path $distItems -DestinationPath $zipPath -CompressionLevel Optimal -Force

$zipItem = Get-Item $zipPath
Write-Host "  Created ZIP: $($zipItem.FullName) ($([math]::Round($zipItem.Length / 1MB, 2)) MB, $($zipItem.Length) bytes)" -ForegroundColor Green

Write-Host "=== Step 5: Computing SHA-256 Hash ===" -ForegroundColor Cyan
$hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLower()
$sha256Content = "$hash  $zipName"
$sha256Path = "$zipPath.sha256"
[System.IO.File]::WriteAllText($sha256Path, $sha256Content)
Write-Host "  Created SHA256 file: $sha256Path" -ForegroundColor Green
Write-Host "  Hash: $hash" -ForegroundColor Yellow

Write-Host "=== Step 6: Creating Detached PKCS#7 / CMS Signature ===" -ForegroundColor Cyan
Add-Type -AssemblyName System.Security
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
