param(
    [string]$PublishDir = 'publish',
    [string]$ZipName = ''
)
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ZipName)) {
    $propsPath = Join-Path $PSScriptRoot "..\Directory.Build.props"
    $ver = "1.6.3"
    if (Test-Path $propsPath) {
        $xml = [xml](Get-Content $propsPath)
        if ($xml.Project.PropertyGroup.PortalVersion) {
            $ver = $xml.Project.PropertyGroup.PortalVersion.Trim()
        }
    }
    $ZipName = "PortalWin-$ver-win-x64.zip"
}

$stage = Join-Path $PSScriptRoot "..\package_staging"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Path $stage | Out-Null

try {
    $items = Get-ChildItem -Path $PublishDir | Where-Object { $_.Name -ne 'Debug' -and $_.Name -notlike '*.zip' }
    foreach ($item in $items) {
        if ($item.PSIsContainer) {
            Copy-Item -Path $item.FullName -Destination (Join-Path $stage $item.Name) -Recurse -Force
        } else {
            $inStream = [System.IO.File]::Open($item.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            $outStream = [System.IO.File]::Create((Join-Path $stage $item.Name))
            $inStream.CopyTo($outStream)
            $outStream.Close()
            $inStream.Close()
        }
    }

    if (Test-Path $ZipName) { Remove-Item -Force $ZipName }
    $stagingItems = (Get-ChildItem -Path $stage).FullName
    Compress-Archive -Path $stagingItems -DestinationPath $ZipName -CompressionLevel Optimal -Force
    (Get-Item $ZipName) | Select-Object FullName, Length, LastWriteTime
}
finally {
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
}
