param([string]$AppDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/portable/win-x64'))
$ErrorActionPreference = 'Stop'
$AppDirectory = [IO.Path]::GetFullPath($AppDirectory)
if (!(Test-Path -LiteralPath (Join-Path $AppDirectory 'DupeFinder.pri'))) { throw 'Missing WinUI resource index' }
$report = Join-Path $AppDirectory 'self-test-result.txt'
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
$testProcess = Start-Process -FilePath (Join-Path $AppDirectory 'DupeFinder.exe') -ArgumentList '--self-test' -PassThru -WindowStyle Hidden
if (!$testProcess.WaitForExit(45000)) { $testProcess.Kill(); throw 'UI test timed out' }
if (!(Test-Path -LiteralPath $report)) { throw 'UI test exited without a report' }
$result = Get-Content -LiteralPath $report -Raw
Write-Output $result
if (!$result.StartsWith('PASS:')) { throw 'UI integration test failed' }
