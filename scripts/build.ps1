param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '需要 Windows x64 和 .NET Framework 4.8。' }
$buildDirectory = Join-Path $projectRoot 'build'
New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName
$sources += Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' | Select-Object -ExpandProperty FullName
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output /codepage:65001 /warn:4 "/out:$buildDirectory\LlmUsageTray.exe" "/win32manifest:$projectRoot\src\app.manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll $sources
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination (Join-Path $buildDirectory 'LlmUsageTray.exe.config') -Force
if ($Test) {
  $testProcess = Start-Process -FilePath (Join-Path $buildDirectory 'LlmUsageTray.exe') -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
  if ($testProcess.ExitCode -ne 0) { throw '自检失败。检查 tests\last-result.txt。' }
  Get-Content -LiteralPath (Join-Path $projectRoot 'tests\last-result.txt')
}
Write-Output (Join-Path $buildDirectory 'LlmUsageTray.exe')
