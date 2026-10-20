#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('ETW', 'Procmon')][string] $Backend,
    [Parameter(Mandatory)][string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
$runRoot = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($runRoot)
$statePath = Join-Path $runRoot 'state.json'
$state = [ordered]@{
    Backend = $Backend; Phase = 'Preflight'; Status = 'Running'
    StartedAtUtc = [datetime]::UtcNow.ToString('o'); FinishedAtUtc = $null
    ControllerId = $PID; RootProcessId = $null; RootStartUtc = $null
    SecuritySignals = @(); Error = $null; CaptureStopped = $false
}
$startedAt = [datetime]::UtcNow
$wpr = 'C:\Windows\System32\wpr.exe'
$xperf = 'C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\xperf.exe'
$procmon = Join-Path $PSScriptRoot 'tools\Procmon\Procmon64.exe'
$fixture = Join-Path $PSScriptRoot 'bin\CCMTraceFixture.exe'
$etwExporter = Join-Path $PSScriptRoot 'etw-bin\EtwExport.exe'
$sessionName = 'CCMTrace_' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$captureStarted = $false
$procmonProcess = $null

function Save-State([string] $Phase) {
    $state.Phase = $Phase
    $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $statePath -Encoding utf8
}

function Invoke-TraceTool([string] $Path, [string[]] $Arguments, [int] $TimeoutSeconds = 45) {
    $info = [Diagnostics.ProcessStartInfo]::new($Path)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "$([IO.Path]::GetFileName($Path)) 超时；未自动重试。"
    }
    $text = $outTask.GetAwaiter().GetResult() + $errTask.GetAwaiter().GetResult()
    Add-Content -LiteralPath (Join-Path $runRoot 'tools.log') -Value ($Path + ' ' + ($Arguments -join ' ') + "`n" + $text) -Encoding utf8
    if ($process.ExitCode -ne 0) { throw "$([IO.Path]::GetFileName($Path)) exit=$($process.ExitCode): $text" }
    return $text
}

function Get-SecuritySignals {
    $log = 'Microsoft-Windows-Windows Defender/Operational'
    try {
        @(Get-WinEvent -FilterHashtable @{LogName=$log; StartTime=$startedAt; Id=1116,1117,1121,1123} -ErrorAction Stop |
            Where-Object { $_.Message -match 'trace-tests|CCMTraceFixture|Procmon64|wpr\.exe|xperf\.exe' } |
            Select-Object TimeCreated, Id, Message)
    }
    catch {
        if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') {
            Add-Content -LiteralPath (Join-Path $runRoot 'tools.log') -Value "Security log unavailable: $($_.Exception.Message)"
        }
    }
}

function Assert-NoSecurityBlock {
    $state.SecuritySignals = @(Get-SecuritySignals)
    if ($state.SecuritySignals.Count -gt 0) { throw '检测到相关安全软件拦截，暂停测试。' }
}

function Start-Fixture([string] $Role) {
    $info = [Diagnostics.ProcessStartInfo]::new($fixture)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    foreach ($argument in @($Role, $runRoot, [string]$PID)) { $info.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($info)
}

function Invoke-FixtureWorkload {
    $broker = Start-Fixture 'broker'
    $noise = Start-Fixture 'noise'
    $root = Start-Fixture 'root'
    $state.RootProcessId = $root.Id
    $state.RootStartUtc = $root.StartTime.ToUniversalTime().ToString('o')
    Save-State 'Workload'
    $deadline = [datetime]::UtcNow.AddSeconds(30)
    while (@(Get-ChildItem -LiteralPath (Join-Path $runRoot 'truth') -File).Count -lt 18) {
        Assert-NoSecurityBlock
        if ([datetime]::UtcNow -ge $deadline) { throw '测试进程未在 30 秒内完成；不重试，等待诊断。' }
        Start-Sleep -Milliseconds 200
    }
    foreach ($process in @($root, $noise, $broker)) {
        if (!$process.WaitForExit(5000) -or $process.ExitCode -ne 0) { throw '测试进程异常退出。' }
    }
    $truth = @(Get-ChildItem -LiteralPath (Join-Path $runRoot 'truth') -File | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    })
    foreach ($entry in $truth) {
        $live = Get-Process -Id $entry.ProcessId -ErrorAction SilentlyContinue
        if ($live -and $live.Path -eq $fixture -and
            [math]::Abs(($live.StartTime.ToUniversalTime() - [datetime]$entry.StartUtc).TotalMilliseconds) -lt 1) {
            if (!$live.WaitForExit(5000)) { throw '已记录的子进程尚未退出。' }
        }
    }
    $truth | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'ground-truth.json') -Encoding utf8
}

try {
    Save-State 'Preflight'
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '采集需要管理员权限；未开始监控。'
    }
    foreach ($directory in @('data\tree', 'data\noise', 'data\broker', 'control', 'truth')) {
        [void][IO.Directory]::CreateDirectory((Join-Path $runRoot $directory))
    }
    [IO.File]::WriteAllBytes((Join-Path $runRoot 'data\tree\existing.sav'), [byte[]](1,2,3))
    [IO.File]::WriteAllBytes((Join-Path $runRoot 'data\tree\mapped-existing.sav'), [byte[]]::new(128))
    Assert-NoSecurityBlock
    Save-State 'StartingCapture'
    if ($Backend -eq 'ETW') {
        Invoke-TraceTool $wpr @('-start', ((Join-Path $PSScriptRoot 'FileTrace.wprp') + '!CCMFileTrace'), '-filemode', '-recordtempto', $runRoot, '-instancename', $sessionName) | Out-Null
        $captureStarted = $true
        Invoke-TraceTool $wpr @('-status', 'collectors', '-details', '-instancename', $sessionName) | Set-Content -LiteralPath (Join-Path $runRoot 'status-start.txt')
    }
    else {
        if (@(Get-Process -Name 'Procmon', 'Procmon64', 'Procmon64a' -ErrorAction SilentlyContinue).Count) {
            throw '已有 Procmon 正在运行，未更改或停止现有采集。'
        }
        $info = [Diagnostics.ProcessStartInfo]::new($procmon)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        foreach ($argument in @('/AcceptEula','/Quiet','/Minimized','/NoFilter','/BackingFile',(Join-Path $runRoot 'capture.pml'),'/Runtime','45')) {
            $info.ArgumentList.Add($argument)
        }
        $procmonProcess = [Diagnostics.Process]::Start($info)
        $captureStarted = $true
        Invoke-TraceTool $procmon @('/AcceptEula','/Quiet','/WaitForIdle') 20 | Out-Null
        if ($procmonProcess.HasExited) { throw 'Procmon 在采集前已退出；未重试。' }
    }
    Assert-NoSecurityBlock
    Invoke-FixtureWorkload
    Assert-NoSecurityBlock
    Save-State 'StoppingCapture'
    if ($Backend -eq 'ETW') {
        Invoke-TraceTool $wpr @('-status','-instancename',$sessionName) | Set-Content -LiteralPath (Join-Path $runRoot 'status-end.txt')
        Invoke-TraceTool $wpr @('-stop',(Join-Path $runRoot 'capture.etl'),'-skipPdbGen','-instancename',$sessionName) | Out-Null
    }
    else {
        Invoke-TraceTool $procmon @('/AcceptEula','/Quiet','/Terminate') | Out-Null
        if (!$procmonProcess.WaitForExit(10000)) { throw '本次 Procmon 尚未结束。' }
    }
    $captureStarted = $false
    $state.CaptureStopped = $true
    Save-State 'Exporting'
    if ($Backend -eq 'ETW') {
        Invoke-TraceTool $etwExporter @((Join-Path $runRoot 'capture.etl'),(Join-Path $runRoot 'events.jsonl')) | Out-Null
        Invoke-TraceTool $xperf @('-i',(Join-Path $runRoot 'capture.etl'),'-o',(Join-Path $runRoot 'trace-statistics.txt'),'-a','tracestats') | Out-Null
    }
    else {
        Invoke-TraceTool $procmon @('/AcceptEula','/Quiet','/OpenLog',(Join-Path $runRoot 'capture.pml'),'/SaveAs',(Join-Path $runRoot 'events.csv')) | Out-Null
    }
    Assert-NoSecurityBlock
    $state.Status = 'Captured'
}
catch {
    $state.Error = $_.Exception.ToString()
    $state.SecuritySignals = @(Get-SecuritySignals)
    $state.Status = if ($state.SecuritySignals.Count -gt 0 -or $state.Error -match '安全软件拦截|病毒|恶意软件|contains a virus|malware') { 'PausedSecurity' } else { 'Failed' }
}
finally {
    # Stop only the session or Procmon instance started by this invocation.
    if ($captureStarted) {
        try {
            if ($Backend -eq 'ETW') { Invoke-TraceTool $wpr @('-cancel','-instancename',$sessionName) | Out-Null }
            elseif ($procmonProcess -and !$procmonProcess.HasExited) {
                Invoke-TraceTool $procmon @('/AcceptEula','/Quiet','/Terminate') | Out-Null
                if (!$procmonProcess.WaitForExit(10000)) { throw '测试 Procmon 仍在运行。' }
            }
            $state.CaptureStopped = $true
        }
        catch { $state.Error += "`nCleanup: $($_.Exception.Message)" }
    }
    foreach ($process in @(Get-Process -Name CCMTraceFixture -ErrorAction SilentlyContinue)) {
        if ($process.Path -eq $fixture -and $process.StartTime.ToUniversalTime() -ge $startedAt) {
            $process.Kill()
        }
    }
    $state.FinishedAtUtc = [datetime]::UtcNow.ToString('o')
    Save-State 'Finished'
}
