param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$releaseRoot = Join-Path $PSScriptRoot "dist\releases\v$Version"
if (Test-Path -LiteralPath $releaseRoot) { throw "发布目录已存在，请先移走旧产物：$releaseRoot" }
$destination = Join-Path $releaseRoot 'ColdClientManager'
$runtimeVersion = '10.0.13'
foreach ($name in @('ColdClientManager', 'ColdClientManager.Cli')) {
    $project = Join-Path $PSScriptRoot "src\$name\$name.csproj"
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $destination --nologo "-p:Version=$Version" "-p:RuntimeFrameworkVersion=$runtimeVersion" -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "$name 发布构建失败。" }
}
foreach ($file in @('README.txt', 'THIRD-PARTY-NOTICES.txt', 'Record-Game.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $destination
}
$packageRoot = dotnet msbuild $project -nologo -getProperty:NuGetPackageRoot
if ($LASTEXITCODE -ne 0) { throw '无法读取运行时许可证目录。' }
$runtimePackage = Join-Path $packageRoot "microsoft.netcore.app.runtime.win-x64\$runtimeVersion"
$desktopPackage = Join-Path $packageRoot "microsoft.windowsdesktop.app.runtime.win-x64\$runtimeVersion"
$licenses = Join-Path $destination 'licenses'
Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination (Join-Path $licenses 'dotnet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenses 'dotnet-THIRD-PARTY-NOTICES.txt')
Copy-Item -LiteralPath (Join-Path $desktopPackage 'LICENSE') -Destination (Join-Path $licenses 'windowsdesktop-LICENSE.txt')
$archive = Join-Path $releaseRoot "ColdClientManager-$Version-win-x64-portable.zip"
Compress-Archive -LiteralPath $destination -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding utf8NoBOM
Write-Output $archive
Write-Output (Join-Path $releaseRoot 'SHA256SUMS.txt')
