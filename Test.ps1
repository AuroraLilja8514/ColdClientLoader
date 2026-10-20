param(
    [switch]$Etw,
    [string]$ColdLoader,
    [switch]$Permissions
)
$ErrorActionPreference = 'Stop'
$testProject = Join-Path $PSScriptRoot 'tests\ColdClientManager.Tests\ColdClientManager.Tests.csproj'
$mockProject = Join-Path $PSScriptRoot 'tests\MockGame\MockGame.csproj'
foreach ($project in @($testProject, $mockProject)) {
    dotnet build $project --nologo
    if ($LASTEXITCODE -ne 0) { throw '测试程序构建失败。' }
}
$runner = Join-Path $PSScriptRoot 'tests\ColdClientManager.Tests\bin\Debug\net10.0-windows\ColdClientManager.Tests.exe'
$mock = Join-Path $PSScriptRoot 'tests\MockGame\bin\Debug\net10.0-windows\MockGame.exe'
$app = Join-Path $PSScriptRoot 'src\ColdClientManager\bin\Debug\net10.0-windows\ColdClientManager.exe'
$output = Join-Path $PSScriptRoot ('test-results\run-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
& $runner (Join-Path $output 'core') $mock
if ($LASTEXITCODE -ne 0) { throw '同步或进程树测试失败。' }
& $runner (Join-Path $output 'ui') $mock --ui
if ($LASTEXITCODE -ne 0) { throw '界面测试失败。' }
if ($Etw) {
    & $runner (Join-Path $output 'etw') $mock --etw
    if ($LASTEXITCODE -ne 0) { throw 'ETW 测试失败，请检查错误报告或安全软件提示，勿自动重试。' }
}
if ($ColdLoader) {
    & $runner (Join-Path $output 'cold') $mock --cold $ColdLoader
    if ($LASTEXITCODE -ne 0) { throw 'ColdClientLoader 模拟测试失败，请检查错误报告或安全软件提示，勿自动重试。' }
}
if ($Permissions) {
    & $runner (Join-Path $output 'permissions') $mock --permissions $app
    if ($LASTEXITCODE -ne 0) { throw '临时目录权限测试失败，请检查测试报告。' }
}
Write-Output "测试结果：$output"
