param(
    [string]$PublishDir = 'publish',
    [string]$ZipName = 'PortalWin-1.5.4-win-x64.zip'
)
$ErrorActionPreference = 'Stop'
$items = (Get-ChildItem -Path $PublishDir | Where-Object { $_.Name -ne 'Debug' -and $_.Name -notlike '*.zip' }).FullName
Compress-Archive -Path $items -DestinationPath $ZipName -Force
(Get-Item $ZipName) | Select-Object FullName, Length, LastWriteTime
