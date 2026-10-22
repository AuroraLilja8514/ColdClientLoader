# Use the installed .NET runtime so command output and exit codes work in a terminal.
& dotnet (Join-Path $PSScriptRoot 'ColdClientManager.dll') --record @args
exit $LASTEXITCODE
