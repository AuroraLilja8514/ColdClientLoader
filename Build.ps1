$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\ColdClientManager\ColdClientManager.csproj'
$destination = Join-Path $PSScriptRoot 'dist\ColdClientManager'
dotnet publish $project -c Release -o $destination --nologo
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.txt') -Destination $destination
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $destination
Write-Output (Join-Path $destination 'ColdClientManager.exe')
