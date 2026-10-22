# The portable release includes a console entry point with its own .NET runtime.
$console = Join-Path $PSScriptRoot 'ColdClientManager.Cli.exe'
if (Test-Path -LiteralPath $console) {
    & $console @args
} else {
    & dotnet (Join-Path $PSScriptRoot 'ColdClientManager.dll') --record @args
}
exit $LASTEXITCODE
