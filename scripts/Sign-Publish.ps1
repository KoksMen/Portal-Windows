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

if ([string]::IsNullOrWhiteSpace($CertPassword)) {
    $sec = Read-Host "Enter certificate password" -AsSecureString
    $BSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    $CertPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($BSTR)
}

$certMap = @{
    "Portal.CommonFiles.pfx"        = @("Portal.Common.dll")
    "Portal.CredentialProvider.pfx" = @("Portal.CredentialProvider.dll", "Portal.CredentialProvider.comhost.dll")
    "Portal.Host.pfx"               = @("Portal.Host.exe", "Portal.Host.dll")
    "Portal.Updater.pfx"            = @("Portal.Updater.exe", "Portal.Updater.dll")
}

Write-Host "==> Signing publish output in: $PublishDir" -ForegroundColor Cyan

foreach ($pfxName in $certMap.Keys) {
    $pfxPath = Join-Path $CertDir $pfxName
    if (-not (Test-Path $pfxPath)) {
        Write-Warning "Certificate file missing: $pfxPath"
        continue
    }

    $targetNames = $certMap[$pfxName]

    # Find matching files in root and subdirectories of PublishDir
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
