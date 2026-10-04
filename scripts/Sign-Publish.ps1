param(
    [Parameter(Mandatory = $false)]
    [string]$CertPassword = "",

    [Parameter(Mandatory = $false)]
    [string]$PublishDir = "publish",

    [Parameter(Mandatory = $false)]
    [string]$CertDir = "E:\Sertificates\Portal-Sertificates",

    [Parameter(Mandatory = $false)]
    [string]$SigntoolPath = "E:\Windows Kits\10\bin\10.0.26100.0\x86\signtool.exe",

    [Parameter(Mandatory = $false)]
    [string]$TimestampUrl = "http://timestamp.sectigo.com"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SigntoolPath)) {
    throw "SignTool not found at: $SigntoolPath"
}

if (-not (Test-Path $CertDir)) {
    throw "Certificate directory not found at: $CertDir"
}

if (-not (Test-Path $PublishDir)) {
    throw "Publish directory not found at: $PublishDir. Please run 'dotnet publish src\Portal.Host\Portal.Host.csproj -c Release -p:SkipSigning=true -o publish\' first."
}

$thumbprintMap = @{
    "F1A00AC831420B2AADEFD0BCCDFD99FDAF246D7B" = @("Portal.Host.exe", "Portal.Host.dll")
    "110025BF33D9D8EEE632AD6C4F43618A24069B22" = @("Portal.CredentialProvider.dll", "Portal.CredentialProvider.comhost.dll")
    "7492EFCAEAEFD328F405F05E0A6BCBF12402C7A8" = @("Portal.Common.dll")
    "6B757026547640EBCDB08ACFD568F2A873D601F0" = @("Portal.Updater.exe", "Portal.Updater.dll")
}

$certMap = @{
    "Portal.CommonFiles.pfx"        = @("Portal.Common.dll")
    "Portal.CredentialProvider.pfx" = @("Portal.CredentialProvider.dll", "Portal.CredentialProvider.comhost.dll")
    "Portal.Host.pfx"               = @("Portal.Host.exe", "Portal.Host.dll")
    "Portal.Updater.pfx"            = @("Portal.Updater.exe", "Portal.Updater.dll")
}

Write-Host "==> Signing publish output in: $PublishDir" -ForegroundColor Cyan

# Check if certificates are available in CurrentUser store
$storeThumbprints = (Get-ChildItem Cert:\CurrentUser\My).Thumbprint
$useStore = $true
foreach ($tp in $thumbprintMap.Keys) {
    if ($storeThumbprints -notcontains $tp) {
        $useStore = $false
        break
    }
}

if ($useStore) {
    Write-Host "--> Using certificates from Cert:\CurrentUser\My" -ForegroundColor Green
    foreach ($sha1 in $thumbprintMap.Keys) {
        $targetNames = $thumbprintMap[$sha1]
        $matchedFiles = Get-ChildItem -Path $PublishDir -Recurse | Where-Object { $targetNames -contains $_.Name }

        foreach ($file in $matchedFiles) {
            Write-Host "--> Signing $($file.FullName) with SHA1 $sha1..." -ForegroundColor Yellow
            $signArgs = @(
                "sign",
                "/sha1", $sha1,
                "/tr", $TimestampUrl,
                "/td", "sha256",
                "/fd", "sha256",
                $file.FullName
            )

            $signed = $false
            $lastErr = 0
            for ($attempt = 1; $attempt -le 3; $attempt++) {
                & $SigntoolPath @signArgs
                if ($LASTEXITCODE -eq 0) {
                    $signed = $true
                    break
                }
                $lastErr = $LASTEXITCODE
                Write-Host "--> Timestamp attempt $attempt failed, retrying in 2 seconds..." -ForegroundColor DarkYellow
                Start-Sleep -Seconds 2
            }
            if (-not $signed) {
                $sig = Get-AuthenticodeSignature $file.FullName
                if ($sig.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
                    Write-Host "--> File $($file.FullName) could not be re-signed (file in use), but already has an existing signature. Continuing." -ForegroundColor DarkYellow
                    continue
                }
                throw "Failed to sign $($file.FullName) with SHA1 $sha1 (exit code $lastErr)"
            }
        }
    }
} else {
    if ([string]::IsNullOrWhiteSpace($CertPassword)) {
        $sec = Read-Host "Enter certificate password" -AsSecureString
        $BSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
        $CertPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($BSTR)
    }

    foreach ($pfxName in $certMap.Keys) {
        $pfxPath = Join-Path $CertDir $pfxName
        if (-not (Test-Path $pfxPath)) {
            Write-Warning "Certificate file missing: $pfxPath"
            continue
        }

        $targetNames = $certMap[$pfxName]
        $matchedFiles = Get-ChildItem -Path $PublishDir -Recurse | Where-Object { $targetNames -contains $_.Name }

        foreach ($file in $matchedFiles) {
            Write-Host "--> Signing $($file.FullName) with $pfxName..." -ForegroundColor Yellow
            $signArgs = @(
                "sign",
                "/f", $pfxPath,
                "/p", $CertPassword,
                "/tr", $TimestampUrl,
                "/td", "sha256",
                "/fd", "sha256",
                $file.FullName
            )

            & $SigntoolPath @signArgs
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to sign $($file.FullName) with $pfxName (exit code $LASTEXITCODE)"
            }
        }
    }
}

Write-Host "`n==> Verifying signatures..." -ForegroundColor Cyan
$allSigned = Get-ChildItem -Path $PublishDir -Recurse -Include *.exe, *.dll | Where-Object {
    $name = $_.Name
    $certMap.Values | ForEach-Object { $_ } | Where-Object { $_ -eq $name }
}

foreach ($f in $allSigned) {
    & $SigntoolPath verify /pa $f.FullName
    if ($LASTEXITCODE -eq 0) {
        Write-Host " [OK] $($f.FullName)" -ForegroundColor Green
    } else {
        Write-Host " [FAIL] $($f.FullName)" -ForegroundColor Red
    }
}

Write-Host "`n==> Signing completed successfully!" -ForegroundColor Green
