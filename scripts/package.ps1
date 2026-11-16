$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$distDirectory = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $distDirectory -Force | Out-Null
$packageDirectory = Join-Path $distDirectory 'LlmUsageTray-0.2.1'
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'build\LlmUsageTray.exe'),(Join-Path $projectRoot 'build\LlmUsageTray.exe.config'),(Join-Path $projectRoot 'README.md') -Destination $packageDirectory -Force
$archive = Join-Path $distDirectory 'LlmUsageTray-0.2.1.zip'
Compress-Archive -LiteralPath $packageDirectory -DestinationPath $archive -Force
Write-Output $archive
