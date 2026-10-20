#requires -Version 7.0
<#
.SYNOPSIS
在临时 Sandboxie 沙盒中运行游戏，导出候选存档路径，然后删除临时沙盒。
.DESCRIPTION
只记录沙盒内生成的文件清单，不恢复文件、不修改现有沙盒。
请在游戏中实际保存并退出。报告列出候选文件，不能自动确认哪些是存档。
测试产生的存档会随临时沙盒删除；报告保存在 save-probes 目录。
可用 -TimeoutSeconds 300 限制试运行时长；默认 0 表示等待正常退出。
.EXAMPLE
.\Find-GameSavePath.ps1 -ExecutablePath 'E:\Tools\MyGame\steamclient_loader.exe'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ExecutablePath,
    [string[]] $ArgumentList = @(),
    [string] $WorkingDirectory,
    [ValidateRange(0, 2147483647)]
    [int] $TimeoutSeconds = 0,
    [string] $SandboxieDirectory = 'C:\Program Files\Sandboxie-Plus',
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'save-probes')
)

$ErrorActionPreference = 'Stop'
$executable = Get-Item -LiteralPath $ExecutablePath
if ($executable.PSIsContainer -or $executable.Extension -ine '.exe') {
    throw 'ExecutablePath 必须是实际的 .exe 文件。'
}
if (!$WorkingDirectory) { $WorkingDirectory = $executable.DirectoryName }
$workingFolder = Get-Item -LiteralPath $WorkingDirectory
if (!$workingFolder.PSIsContainer) { throw 'WorkingDirectory 必须是目录。' }
$startExe = (Get-Item -LiteralPath (Join-Path $SandboxieDirectory 'Start.exe')).FullName
$iniExe = (Get-Item -LiteralPath (Join-Path $SandboxieDirectory 'SbieIni.exe')).FullName
$dllPath = (Get-Item -LiteralPath (Join-Path $SandboxieDirectory 'SbieDll.dll')).FullName
if ((Get-Service -Name SbieSvc).Status -ne 'Running') {
    throw 'Sandboxie 服务尚未运行。'
}

# SbieIni query excludes template values. Query the effective configuration too,
# so inherited direct-write or breakout rules cannot bypass the test sandbox.
$nativeSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ColdClientSaveProbeNative {
    [DllImport(@"DLL_PATH", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint SbieApi_QueryConf(string section, string setting,
        uint index, StringBuilder value, uint length);
    public static string[] Values(string section, string setting) {
        var values = new List<string>();
        for (uint index = 0; ; index++) {
            var buffer = new StringBuilder(8192);
            uint status = SbieApi_QueryConf(section, setting,
                index | 0x20000000u, buffer, 16384);
            if (status == 0xC000008Bu) return values.ToArray();
            if (status != 0) throw new InvalidOperationException(
                "Sandboxie query failed: " + setting + " (0x" + status.ToString("X8") + ")");
            values.Add(buffer.ToString());
        }
    }
}
'@
Add-Type -TypeDefinition $nativeSource.Replace('DLL_PATH', $dllPath.Replace('"', '""'))

$boxName = 'CCMProbe_' + [guid]::NewGuid().ToString('N').Substring(0, 16)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$runRoot = Join-Path $outputRoot $boxName
$boxRoot = Join-Path $runRoot 'sandbox'
$reportPath = Join-Path $runRoot 'report.json'
[void][IO.Directory]::CreateDirectory($runRoot)
$report = [ordered]@{
    BoxName = $boxName
    ExecutablePath = $executable.FullName
    StartedAtUtc = [datetime]::UtcNow.ToString('o')
    FinishedAtUtc = $null
    ExitCode = $null
    Files = @()
    CandidateDirectories = @()
    Error = $null
    CleanupError = $null
    CleanupCompleted = $false
}
$configurationStarted = $false
$runFailure = $null
$cleanupFailure = $null

function Get-ProbeProcesses {
    $processText = & $startExe /silent "/box:$boxName" /listpids | Out-String
    $lines = @($processText -split '\r?\n' | Where-Object { $_.Length -gt 0 })
    if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) {
        throw '无法查询临时沙盒的进程，停止清理。'
    }
    $processIds = @($lines | Select-Object -Skip 1 | ForEach-Object { [uint32]$_ })
    if ([uint32]$lines[0] -ne $processIds.Count) {
        throw 'Sandboxie 返回了不完整的进程清单，停止清理。'
    }
    return $processIds
}

function Set-ProbeSetting([string] $Name, [string] $Value) {
    & $iniExe set $boxName $Name $Value | Out-Null
    if ($LASTEXITCODE -ne 0 -or
        [ColdClientSaveProbeNative]::Values($boxName, $Name)[0] -cne $Value) {
        throw "无法设置临时沙盒选项：$Name。"
    }
}

function Save-ProbeInventory {
    if (!(Test-Path -LiteralPath $boxRoot)) { return }
    $report.Files = @(Get-ChildItem -LiteralPath $boxRoot -File -Force -Recurse |
        Where-Object { !($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($boxRoot, $_.FullName)
            $original = $null
            if ($relative -match '^drive\\([A-Za-z])\\(.+)$') {
                $original = $Matches[1] + ':\' + $Matches[2]
            }
            [pscustomobject]@{
                OriginalPath = $original
                SandboxRelativePath = $relative
                Length = $_.Length
                LastWriteTimeUtc = $_.LastWriteTimeUtc.ToString('o')
            }
        })
    $report.CandidateDirectories = @($report.Files |
        Where-Object OriginalPath |
        Group-Object { [IO.Path]::GetDirectoryName($_.OriginalPath) } |
        Sort-Object Name |
        ForEach-Object { [pscustomobject]@{ Path = $_.Name; FileCount = $_.Count } })
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
}

try {
    $configurationStarted = $true
    Set-ProbeSetting 'ConfigLevel' '10'
    Set-ProbeSetting 'FileRootPath' $boxRoot
    Set-ProbeSetting 'SeparateUserFolders' 'n'
    Set-ProbeSetting 'UseVolumeSerialNumbers' 'n'
    Set-ProbeSetting 'AutoRecover' 'n'
    Set-ProbeSetting 'AutoDelete' 'n'
    Set-ProbeSetting 'CopyLimitKb' '-1'
    Set-ProbeSetting 'Template' 'LingerPrograms'
    Set-ProbeSetting 'Enabled' 'y'

    foreach ($setting in @('OpenFilePath', 'OpenKeyPath', 'BreakoutProcess',
                           'BreakoutFolder', 'BreakoutDocument')) {
        foreach ($value in [ColdClientSaveProbeNative]::Values($boxName, $setting)) {
            # Named pipes carry IPC; they do not grant writes to save files.
            if ($setting -eq 'OpenFilePath' -and
                $value -match '^(?:[^,]+,)?\\Device\\NamedPipe\\') { continue }
            throw "全局配置或模板包含 $setting=$value；无法保证文件隔离，未启动游戏。"
        }
    }
    if ([ColdClientSaveProbeNative]::Values($boxName, 'NoSecurityIsolation') -contains 'y') {
        throw '全局配置关闭了安全隔离，未启动游戏。'
    }

    Write-Host "已创建临时沙盒 $boxName。请在游戏中保存一次，然后正常退出。"
    $launchInfo = [Diagnostics.ProcessStartInfo]::new($startExe)
    $launchInfo.UseShellExecute = $false
    $launchInfo.CreateNoWindow = $true
    $launchInfo.WorkingDirectory = $workingFolder.FullName
    foreach ($argument in @('/silent', "/box:$boxName", '/wait', $executable.FullName) + $ArgumentList) {
        $launchInfo.ArgumentList.Add($argument)
    }
    $runWatch = [Diagnostics.Stopwatch]::StartNew()
    $launcher = [Diagnostics.Process]::Start($launchInfo)
    while (!$launcher.WaitForExit(500)) {
        if ($TimeoutSeconds -gt 0 -and $runWatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            throw "试运行超过 $TimeoutSeconds 秒，结束测试并清理临时沙盒。"
        }
    }
    $report.ExitCode = $launcher.ExitCode
    if ($launcher.ExitCode -ne 0) {
        throw "启动程序返回退出码 $($launcher.ExitCode)。"
    }
    # A loader can exit before the game it spawned; wait for the box as well.
    while (@(Get-ProbeProcesses).Count -gt 0) {
        if ($TimeoutSeconds -gt 0 -and $runWatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            throw "试运行超过 $TimeoutSeconds 秒，结束测试并清理临时沙盒。"
        }
        Start-Sleep -Milliseconds 500
    }
}
catch {
    $runFailure = $_
    $report.Error = $_.Exception.Message
}
finally {
    if ($configurationStarted) {
        try {
            if ([ColdClientSaveProbeNative]::Values($boxName, 'Enabled') -contains 'y') {
                & $startExe /silent "/box:$boxName" /terminate | Out-Null
                $stopDeadline = [datetime]::UtcNow.AddSeconds(15)
                while (@(Get-ProbeProcesses).Count -gt 0) {
                    if ([datetime]::UtcNow -ge $stopDeadline) {
                        throw '临时沙盒进程尚未结束，已保留沙盒，避免在写入中删除文件。'
                    }
                    Start-Sleep -Milliseconds 250
                }
            }
            try {
                Save-ProbeInventory
            }
            finally {
                if (Test-Path -LiteralPath $boxRoot) {
                    # Phase 2 can clean pending directories belonging to other
                    # boxes. Use only phase 1, then remove this run's directory.
                    & $startExe /silent "/box:$boxName" delete_sandbox_silent_phase1 | Out-Null
                    if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $boxRoot)) {
                        throw 'Sandboxie 未能释放临时目录，已保留配置以便检查。'
                    }
                    $pending = @(Get-ChildItem -LiteralPath $runRoot -Directory -Force |
                        Where-Object Name -Like "__Delete_${boxName}_*")
                    foreach ($folder in $pending) {
                        $deletePath = (Resolve-Path -LiteralPath $folder.FullName).ProviderPath
                        if ([IO.Path]::GetDirectoryName($deletePath) -ine $runRoot -or
                            $folder.Name -notmatch ('^__Delete_' + [regex]::Escape($boxName) + '_[0-9A-Fa-f]+$') -or
                            ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                            throw '临时目录的删除范围验证失败。'
                        }
                        Remove-Item -LiteralPath $deletePath -Recurse -Force
                    }
                }
                & $iniExe set $boxName '*' | Out-Null
                if ($LASTEXITCODE -ne 0 -or (@(& $iniExe query '*') -contains $boxName)) {
                    throw '临时沙盒配置未能删除。'
                }
                $report.CleanupCompleted = $true
            }
        }
        catch {
            $cleanupFailure = $_
            $report.CleanupError = $_.Exception.Message
        }
    }
    $report.FinishedAtUtc = [datetime]::UtcNow.ToString('o')
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
}

Write-Host "候选路径报告：$reportPath"
if ($runFailure) { throw $runFailure }
if ($cleanupFailure) { throw $cleanupFailure }
$report.CandidateDirectories | Format-Table -AutoSize
