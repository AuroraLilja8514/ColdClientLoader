using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ColdClientManager;

// A suspended root is assigned before it can create children. Job accounting remains valid
// when the launcher exits and its descendants keep running. Closing the job never kills them.
public sealed class GameProcess : IDisposable
{
    readonly SafeFileHandle job;
    readonly SafeFileHandle process;
    readonly SafeFileHandle thread;
    bool resumed;
    public int Id { get; }
    public GameProcess(string executable, string arguments, string workingDirectory)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到启动程序。", executable);
        if (executable.Contains('"') || arguments.Contains('\0')) throw new ArgumentException("无效的启动参数。");
        job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception();
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(executable, new StringBuilder('"' + executable + "\" " + arguments), IntPtr.Zero, IntPtr.Zero,
            false, 0x00000004, IntPtr.Zero, workingDirectory, ref startup, out var info))
        {
            job.Dispose();
            throw new Win32Exception();
        }
        process = new SafeFileHandle(info.hProcess, true);
        thread = new SafeFileHandle(info.hThread, true);
        Id = info.dwProcessId;
        if (!AssignProcessToJobObject(job, process))
        {
            int error = Marshal.GetLastWin32Error();
            TerminateProcess(process, 1); // This root is still suspended and has never executed.
            Dispose();
            throw new Win32Exception(error, "无法跟踪游戏进程树，已取消启动。");
        }
    }
    public void Resume()
    {
        if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
        resumed = true;
    }
    public bool ContainsProcess(Process candidate)
    {
        if (candidate.HasExited) return false;
        if (!IsProcessInJob(candidate.SafeHandle, job, out bool contains)) throw new Win32Exception();
        return contains;
    }
    public async Task<int> WaitAsync()
    {
        while (true)
        {
            if (!QueryInformationJobObject(job, 1, out var accounting, Marshal.SizeOf<Accounting>(), IntPtr.Zero)) throw new Win32Exception();
            if (accounting.ActiveProcesses == 0) break;
            await Task.Delay(150);
        }
        if (!GetExitCodeProcess(process, out int exitCode)) throw new Win32Exception();
        return exitCode;
    }
    public void Dispose()
    {
        if (!resumed && process is { IsInvalid: false, IsClosed: false }) TerminateProcess(process, 1);
        thread?.Dispose();
        process?.Dispose();
        job.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfo
    {
        public int cb; public IntPtr reserved, desktop, title;
        public int x, y, xSize, ySize, xChars, yChars, fill, flags;
        public short show, reservedSize; public IntPtr reserved2, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct Accounting
    {
        public long userTime, kernelTime, periodUser, periodKernel;
        public uint faults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateJobObjectW(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out bool result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessW(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out Accounting info, int length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(SafeFileHandle process, out int code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(SafeFileHandle process, int code);
}
