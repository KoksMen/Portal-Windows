param(
    [string]$DistDir = "dist",
    [string]$TargetDir = "E:\Software\Portal"
)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Requesting administrative privileges..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

Write-Host "=== Deploying local build to $TargetDir ===" -ForegroundColor Cyan

Get-Process -Name 'Portal.Host', 'Portal.Client' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping $($_.ProcessName)..."
    $_ | Stop-Process -Force
}

Get-Process -Name 'LogonUI' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping lingering LogonUI (PID $($_.Id))..."
    $_ | Stop-Process -Force -ErrorAction SilentlyContinue
}

Start-Sleep -Milliseconds 500

$distRoot = Resolve-Path (Join-Path $PSScriptRoot "..\$DistDir")
$targetRoot = $TargetDir

Get-ChildItem -Path $distRoot -Recurse | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
    $rel = $_.FullName.Substring($distRoot.Path.Length + 1)
    $dest = Join-Path $targetRoot $rel
    $destDir = Split-Path $dest -Parent
    if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }

    try {
        if (Test-Path $dest) { [System.IO.File]::SetAttributes($dest, [System.IO.FileAttributes]::Normal) }
        Copy-Item -Path $_.FullName -Destination $dest -Force
    } catch {
        Write-Warning "File locked ($rel), renaming aside..."
        $tmp = "$dest.pending-delete-$([Guid]::NewGuid().ToString('N')).tmp"
        [System.IO.File]::Move($dest, $tmp)
        Copy-Item -Path $_.FullName -Destination $dest -Force
    }
}

$cpDll = Join-Path $TargetDir "CredentialProvider\Portal.CredentialProvider.comhost.dll"
if (Test-Path $cpDll) {
    Write-Host "Registering Credential Provider COM DLL..." -ForegroundColor Cyan
    & regsvr32 /s "$cpDll"
}

Write-Host "Deployment completed successfully!" -ForegroundColor Green
Write-Host "Starting Portal.Host..." -ForegroundColor Cyan
Start-Process (Join-Path $TargetDir "Portal.Host.exe")
