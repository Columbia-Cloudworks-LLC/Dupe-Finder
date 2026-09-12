param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishPath = Join-Path $repoRoot 'artifacts/portable/win-x64'
New-Item -ItemType Directory -Force $publishPath | Out-Null
dotnet publish (Join-Path $repoRoot 'src/DupeFinder.App/DupeFinder.App.csproj') -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
& (Join-Path $PSScriptRoot 'test-ui.ps1') -AppDirectory $publishPath
Remove-Item -LiteralPath (Join-Path $publishPath 'self-test-result.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $publishPath
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $publishPath
$zip = Join-Path $repoRoot 'artifacts/DupeFinder-win-x64.zip'
Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $zip -Force
Get-FileHash -LiteralPath $zip -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  DupeFinder-win-x64.zip" } | Set-Content -LiteralPath (Join-Path $repoRoot 'artifacts/SHA256SUMS.txt')
Write-Output $zip
