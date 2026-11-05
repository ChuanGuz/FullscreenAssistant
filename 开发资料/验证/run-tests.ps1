$ErrorActionPreference = 'Stop'
$testDirectory = $PSScriptRoot
$sourceDirectory = Join-Path (Split-Path $testDirectory -Parent) ([string][char]0x6E90 + [char]0x7801)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testProgram = Join-Path $testDirectory 'WindowIntegrationTests.exe'
& $compiler /nologo /codepage:65001 /target:exe /platform:x64 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$testProgram" (Join-Path $testDirectory 'WindowIntegrationTests.cs') (Join-Path $sourceDirectory 'WindowController.cs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $testProgram
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $testProgram --regressions
exit $LASTEXITCODE
