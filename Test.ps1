param(
    [switch]$Etw,
    [string]$ColdLoader,
    [switch]$Permissions,
    [switch]$LaunchModes,
    [switch]$SteamAchievements,
    [switch]$SteamPlaytime,
    [string]$SteamApi
)
$ErrorActionPreference = 'Stop'
if ($SteamAchievements -and (!$ColdLoader -or !$SteamApi)) { throw 'Steam 成就测试需要 -ColdLoader 和原版 -SteamApi 路径，仅测试 Spacewar (480)。' }
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
if ($LaunchModes) {
    & $runner (Join-Path $output 'launch-modes') $mock --launch-modes
    if ($LASTEXITCODE -ne 0) { throw '应用列表的三种启动 / 同步方式测试失败，请检查测试报告。' }
}
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
if ($SteamAchievements) {
    & $runner (Join-Path $output 'steam-spacewar') $mock --steam-live --loader $ColdLoader --steam-api $SteamApi
    if ($LASTEXITCODE -ne 0) { throw 'Spacewar 成就同步测试失败，请检查测试报告。' }
    & $runner (Join-Path $output 'steam-spacewar-failure') $mock --steam-live --loader $ColdLoader --steam-api $SteamApi --fail-after-upload
    if ($LASTEXITCODE -ne 0) { throw 'Spacewar 成就测试异常后的清理失败，请检查测试报告。' }
}
if ($SteamPlaytime) {
    & $runner (Join-Path $output 'steam-status') $mock --steam-status
    if ($LASTEXITCODE -ne 0) { throw 'Steam 会话占用或断线处理测试失败，请检查测试报告。' }
    & $runner (Join-Path $output 'steam-playtime') $mock --steam-playtime
    if ($LASTEXITCODE -ne 0) { throw 'Spacewar 状态或时长测试失败，请检查测试报告。' }
    $launchArgs = @()
    if ($ColdLoader) { $launchArgs = @('--loader', $ColdLoader) }
    & $runner (Join-Path $output 'steam-launch') $mock --steam-launch @launchArgs
    if ($LASTEXITCODE -ne 0) { throw 'Steam 状态与启动流程集成测试失败，请检查测试报告。' }
}
Write-Output "测试结果：$output"
