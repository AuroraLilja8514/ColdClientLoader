#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
$runRoot = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($runRoot)
$statePath = Join-Path $runRoot 'state.json'
$wpr = 'C:\Windows\System32\wpr.exe'
$xperf = 'C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\xperf.exe'
$procmon = Join-Path $PSScriptRoot 'tools\Procmon\Procmon64.exe'
$exporter = Join-Path $PSScriptRoot 'etw-bin\EtwExport.exe'
$session = 'CCMGame_' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$startedAt = [datetime]::UtcNow
$etwStarted = $false
$procmonStarted = $false
$procmonProcess = $null
$state = [ordered]@{
    Status='Running'; Phase='Preflight'; StartedAtUtc=$startedAt.ToString('o')
    SessionName=$session; RootProcessId=$null; RootStartUtc=$null
    CaptureStopped=$false; StopReason=$null; SecuritySignals=@(); Error=$null
}
function Save-State([string] $Phase) {
    $state.Phase = $Phase
    $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $statePath -Encoding utf8
}
function Invoke-TraceTool([string] $Path, [string[]] $Arguments, [int] $TimeoutSeconds=120) {
    $info = [Diagnostics.ProcessStartInfo]::new($Path)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "$([IO.Path]::GetFileName($Path)) 超时，未重试。"
    }
    $message = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    Add-Content -LiteralPath (Join-Path $runRoot 'tools.log') -Value ($Path + ' ' + ($Arguments -join ' ') + "`n" + $message) -Encoding utf8
    if ($process.ExitCode -ne 0) { throw "$([IO.Path]::GetFileName($Path)) exit=$($process.ExitCode): $message" }
    return $message
}
function Assert-NoSecurityBlock {
    try {
        $signals = @(Get-WinEvent -FilterHashtable @{
            LogName='Microsoft-Windows-Windows Defender/Operational'
            StartTime=$startedAt; Id=1116,1117,1121,1123
        } -ErrorAction Stop | Where-Object {
            $_.Message -match 'trace-tests|canofwormholes|Procmon64|wpr\.exe|EtwExport'
        } | Select-Object TimeCreated,Id,Message)
    }
    catch {
        if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { throw }
        $signals = @()
    }
    $state.SecuritySignals = $signals
    if ($signals.Count) { throw '检测到安全软件拦截，暂停测试。' }
}
try {
    Save-State 'Preflight'
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '采集器需要管理员权限。' }
    if (@(Get-Process -Name Procmon,Procmon64,Procmon64a -ErrorAction SilentlyContinue).Count) { throw '已有 Procmon 正在运行，未更改现有采集。' }
    # Read HKCU in the actual user's collector, rather than the executor's sandbox account.
    $registryLayout = @(
        @{Path='Software\Valve\Steam';Names=@('SourceModInstallPath','SteamPath','SteamExe','RunningAppID')},
        @{Path='Software\Valve\Steam\ActiveProcess';Names=@('pid','SteamClientDll','SteamClientDll64','ActiveUser','Universe')}
    )
    $registrySnapshot = foreach ($entry in $registryLayout) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($entry.Path)
        $values = foreach ($name in $entry.Names) {
            $exists = $key -and $key.GetValueNames() -contains $name
            [ordered]@{Name=$name;Exists=[bool]$exists;Kind=$(if($exists){$key.GetValueKind($name).ToString()}else{$null});Value=$(if($exists){$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}else{$null})}
        }
        [ordered]@{Path=$entry.Path;Existed=[bool]$key;Values=@($values)}
        if ($key) { $key.Dispose() }
    }
    [ordered]@{UserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;Keys=@($registrySnapshot)} |
        ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $runRoot 'registry-before.actual-user.json') -Encoding utf8
    foreach ($backend in @('ETW','Procmon')) { [void][IO.Directory]::CreateDirectory((Join-Path $runRoot $backend)) }
    Assert-NoSecurityBlock
    Save-State 'StartingCapture'
    Invoke-TraceTool $wpr @('-start',((Join-Path $PSScriptRoot 'FileTrace.wprp')+'!CCMFileTrace'),'-filemode','-recordtempto',(Join-Path $runRoot 'ETW'),'-instancename',$session) | Out-Null
    $etwStarted = $true
    $info = [Diagnostics.ProcessStartInfo]::new($procmon)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    foreach ($argument in @('/AcceptEula','/Quiet','/Minimized','/NoFilter','/BackingFile',(Join-Path $runRoot 'Procmon\capture.pml'),'/Runtime','960')) { $info.ArgumentList.Add($argument) }
    $procmonProcess = [Diagnostics.Process]::Start($info)
    $procmonStarted = $true
    Invoke-TraceTool $procmon @('/AcceptEula','/Quiet','/WaitForIdle') 20 | Out-Null
    if ($procmonProcess.HasExited) { throw 'Procmon 在启动游戏前退出。' }
    Assert-NoSecurityBlock
    Save-State 'ReadyToLaunch'
    $deadline = [datetime]::UtcNow.AddMinutes(15)
    while (!(Test-Path -LiteralPath (Join-Path $runRoot 'stop-request.json'))) {
        Assert-NoSecurityBlock
        if ($procmonProcess.HasExited) { throw 'Procmon 意外退出，未重试。' }
        if ([datetime]::UtcNow -ge $deadline) { $state.StopReason='15-minute capture limit'; break }
        if (!$state.RootProcessId -and (Test-Path -LiteralPath (Join-Path $runRoot 'launch.json'))) {
            $launch = Get-Content -LiteralPath (Join-Path $runRoot 'launch.json') -Raw | ConvertFrom-Json
            $state.RootProcessId = $launch.RootProcessId
            $state.RootStartUtc = $launch.RootStartUtc
            Save-State 'WaitingForGameplayAndExit'
        }
        Start-Sleep -Seconds 1
    }
    if (!$state.StopReason) { $state.StopReason='Stop requested after user interaction' }
    Save-State 'StoppingCapture'
    Invoke-TraceTool $wpr @('-stop',(Join-Path $runRoot 'ETW\capture.etl'),'-skipPdbGen','-instancename',$session) | Out-Null
    $etwStarted = $false
    Invoke-TraceTool $procmon @('/AcceptEula','/Quiet','/Terminate') | Out-Null
    if (!$procmonProcess.WaitForExit(10000)) { throw '本次 Procmon 未正常停止。' }
    $procmonStarted = $false
    $state.CaptureStopped = $true
    if (!$state.RootProcessId) { throw '未记录游戏启动信息。' }
    Save-State 'Exporting'
    Invoke-TraceTool $exporter @((Join-Path $runRoot 'ETW\capture.etl'),(Join-Path $runRoot 'ETW\events.jsonl')) | Out-Null
    Invoke-TraceTool $xperf @('-i',(Join-Path $runRoot 'ETW\capture.etl'),'-o',(Join-Path $runRoot 'ETW\trace-statistics.txt'),'-a','tracestats') | Out-Null
    Invoke-TraceTool $procmon @('/AcceptEula','/Quiet','/OpenLog',(Join-Path $runRoot 'Procmon\capture.pml'),'/SaveAs',(Join-Path $runRoot 'Procmon\events.csv')) 300 | Out-Null
    foreach ($backend in @('ETW','Procmon')) {
        [ordered]@{Backend=$backend;Status='Captured';CaptureStopped=$true;RootProcessId=$state.RootProcessId;RootStartUtc=$state.RootStartUtc;StartedAtUtc=$state.StartedAtUtc} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot "$backend\state.json") -Encoding utf8
    }
    Assert-NoSecurityBlock
    $state.Status='Captured'
}
catch {
    $state.Error=$_.Exception.ToString()
    $state.Status=if ($state.SecuritySignals.Count -or $state.Error -match '安全软件|病毒|恶意软件|contains a virus|malware') {'PausedSecurity'} else {'Failed'}
}
finally {
    if ($etwStarted) {
        try { Invoke-TraceTool $wpr @('-cancel','-instancename',$session) | Out-Null; $etwStarted=$false }
        catch { $state.Error += "`nETW cleanup: $($_.Exception.Message)" }
    }
    if ($procmonStarted -and !$procmonProcess.HasExited) {
        try {
            Invoke-TraceTool $procmon @('/AcceptEula','/Quiet','/Terminate') | Out-Null
            if (!$procmonProcess.WaitForExit(10000)) { throw 'Procmon 仍在运行。' }
            $procmonStarted=$false
        }
        catch { $state.Error += "`nProcmon cleanup: $($_.Exception.Message)" }
    }
    $state.CaptureStopped = !$etwStarted -and (!$procmonStarted -or $procmonProcess.HasExited)
    $state.FinishedAtUtc=[datetime]::UtcNow.ToString('o')
    Save-State 'Finished'
}
