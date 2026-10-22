using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ColdClientManager;

public sealed class RecordingOptions
{
    public int ProcessId { get; set; }
    public string Output { get; set; } = "";
    public string ObsDirectory { get; set; } = "";
    public string ObsExecutable { get; set; } = "";
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;
    public int Bitrate { get; set; } = 5000;
    public string Encoder { get; set; } = "qsv";
    public bool Audio { get; set; } = true;
    public bool Cursor { get; set; }

    public void Validate()
    {
        if (ProcessId <= 0) throw new ArgumentException("请用 --pid 指定正在运行的游戏进程 ID。");
        if (Width <= 0 || Height <= 0 || Width % 2 != 0 || Height % 2 != 0 || Fps <= 0 || Bitrate <= 0)
            throw new ArgumentException("宽、高必须是正偶数；帧率和码率必须是正整数。");
        if (Encoder is not ("qsv" or "x264")) throw new ArgumentException("编码器请选择 qsv 或 x264，不会自动切换编码器。");
        if (!string.IsNullOrWhiteSpace(ObsExecutable) && !string.IsNullOrWhiteSpace(ObsDirectory))
            throw new ArgumentException("--obs-exe 与 --obs-dir 不能同时指定。");
        if (string.IsNullOrWhiteSpace(Output)) throw new ArgumentException("请用 --output 指定完整录像文件路径（.mkv 或 .mp4）。");
        Output = Path.GetFullPath(Output);
        if (Path.GetExtension(Output).ToLowerInvariant() is not (".mkv" or ".mp4"))
            throw new ArgumentException("录像文件扩展名必须是 .mkv 或 .mp4。");
        if (File.Exists(Output) || Directory.Exists(Output)) throw new IOException("输出路径已经存在，不会覆盖：" + Output);
        if (Output.Contains('\r') || Output.Contains('\n')) throw new ArgumentException("录像路径不能包含换行。");
    }
}

public sealed record RecordingState(int ObsProcessId, long ObsStartTime, int Port, byte[] Password, string ObsDirectory,
    string Output, string WorkingFile, RecordingOptions Options);
public sealed record RecordingStatus(bool Active, string? Output, string? WorkingFile, double DurationMilliseconds = 0,
    string? Message = null);

public static class ObsRecorder
{
    const string Profile = "ColdClientManager";
    const string ArchiveHash = "4d6e40e3ab155f56b30de517380566a206d74b63cdf5ad49aa596924768f97e1";
    public const string ArchiveUrl = "https://github.com/obsproject/obs-studio/releases/download/32.2.2/OBS-Studio-32.2.2-Windows-x64.zip";
    public static string DefaultObsDirectory(string dataRoot) => Path.Combine(dataRoot, "obs");
    public static string DefaultVideoDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "ColdClientManager");
    public static string CreateOutputPath(GameProfile profile, DateTimeOffset started)
    {
        if (profile.RecordingFormat is not ("mkv" or "mp4")) throw new ArgumentException("录像格式请选择 mkv 或 mp4。");
        string folder = string.IsNullOrWhiteSpace(profile.RecordingDirectory) ? DefaultVideoDirectory : profile.RecordingDirectory;
        var invalid = Path.GetInvalidFileNameChars();
        string name = new string(profile.Name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        string time = started.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
        return Path.Combine(folder, $"{name}_{time}_{Guid.NewGuid():N}.{profile.RecordingFormat}");
    }
    static string StatePath(string dataRoot) => Path.Combine(dataRoot, "recording", "session.json");
    static string SettingsBackupPath(string dataRoot) => Path.Combine(dataRoot, "recording", "installed-obs-backup.json");
    static string InstalledConfigDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");
    static readonly string[] InstalledSettingsFiles = ["global.ini", "user.ini", Path.Combine("plugin_config", "obs-websocket", "config.json")];
    static FileStream Lock(string dataRoot)
    {
        string folder = Path.Combine(dataRoot, "recording"); Directory.CreateDirectory(folder);
        return new FileStream(Path.Combine(folder, "control.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static async Task InstallAsync(string dataRoot, string? archive = null)
    {
        using var control = Lock(dataRoot);
        string destination = DefaultObsDirectory(dataRoot);
        if (Directory.Exists(destination)) throw new IOException("OBS 目录已经存在，不会覆盖：" + destination);
        if (archive == null)
        {
            archive = Path.Combine(dataRoot, "recording", "OBS-Studio-32.2.2-Windows-x64.zip");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await http.GetAsync(ArchiveUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(output).ConfigureAwait(false);
        }
        using (var input = File.OpenRead(archive))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(input).ConfigureAwait(false)).Equals(ArchiveHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OBS 压缩包 SHA-256 不匹配；只接受官方 32.2.2 Windows x64 压缩包。");
        ZipFile.ExtractToDirectory(archive, destination);
        File.WriteAllText(Path.Combine(destination, "coldclientmanager-obs.txt"), "OBS 32.2.2; dedicated ColdClientManager instance.");
    }

    public static async Task<RecordingStatus> StartAsync(string dataRoot, RecordingOptions options, GameProfile? automaticProfile = null)
    {
        options.Validate();
        using var control = Lock(dataRoot);
        if (File.Exists(StatePath(dataRoot))) throw new IOException("已有录制会话；请先查询 status 或执行 stop。");
        if (File.Exists(SettingsBackupPath(dataRoot))) throw new IOException("现有 OBS 的设置尚未恢复；退出 OBS 后执行录制命令 recover。");
        var target = await WaitForWindowAsync(options.ProcessId).ConfigureAwait(false);
        bool installed = !string.IsNullOrWhiteSpace(options.ObsExecutable);
        string root = installed ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.ObsExecutable))!, "..", ".."))
            : string.IsNullOrWhiteSpace(options.ObsDirectory) ? DefaultObsDirectory(dataRoot) : Path.GetFullPath(options.ObsDirectory);
        string executable = installed ? Path.GetFullPath(options.ObsExecutable) : Path.Combine(root, "bin", "64bit", "obs64.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到 OBS；请选择已安装的 obs64.exe，或运行录制命令 install。", executable);
        if (installed)
        {
            var version = FileVersionInfo.GetVersionInfo(executable);
            if (!Path.GetFileName(executable).Equals("obs64.exe", StringComparison.OrdinalIgnoreCase) || version.ProductName != "OBS Studio"
                || new Version(version.FileMajorPart, version.FileMinorPart) < new Version(30, 1))
                throw new IOException("请选择 OBS Studio 30.1 或更高版本的 obs64.exe（需要游戏进程音频捕获）。");
            if (new[] { "portable_mode", "obs_portable_mode", "portable_mode.txt", "obs_portable_mode.txt" }.Any(f => File.Exists(Path.Combine(root, f))))
                throw new IOException("该 OBS 已启用便携模式；本应用管理的便携版请用 --obs-dir 指定。");
            EnsureObsClosed();
        }
        else if (!File.Exists(Path.Combine(root, "coldclientmanager-obs.txt")))
            throw new IOException("请选择 ColdClientManager 安装的专用便携 OBS 目录，或用 --obs-exe 指定已安装的 OBS。");
        string config = installed ? InstalledConfigDirectory : Path.Combine(root, "config", "obs-studio");
        if (Directory.Exists(Path.Combine(config, ".sentinel")) && Directory.EnumerateFiles(Path.Combine(config, ".sentinel"), "run_*").Any())
            throw new IOException("OBS 上次未正常退出。请手动打开该 OBS 处理恢复提示并正常退出，再开始录制。");
        if (installed && (Directory.Exists(Path.Combine(config, "basic", "profiles", Profile)) || File.Exists(Path.Combine(config, "basic", "scenes", Profile + ".json")))
            && !File.Exists(Path.Combine(config, "basic", "profiles", Profile, "coldclientmanager-obs.txt")))
            throw new IOException("OBS 已有非本应用创建的 ColdClientManager 配置或场景集合，请先在 OBS 中改名；不会覆盖。");
        Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
        string working = Path.Combine(Path.GetDirectoryName(options.Output)!, "CCM-" + Guid.NewGuid().ToString("N") + Path.GetExtension(options.Output).ToLowerInvariant());
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        if (installed) BackupInstalledSettings(dataRoot, config);
        RecordingState? state = null;
        try
        {
            Prepare(root, config, options, working, port, password, installed);
            var info = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = true,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string argument in installed ? new[] { "--only-bundled-plugins" } : new[] { "--portable", "--multi" }) info.ArgumentList.Add(argument);
            foreach (string argument in new[] { "--profile", Profile, "--collection", Profile, "--minimize-to-tray", "--disable-updater" }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            options.ObsExecutable = installed ? executable : "";
            state = new RecordingState(process.Id, process.StartTime.ToUniversalTime().Ticks, port,
                ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser), root, options.Output, working, options);
            Storage.Write(StatePath(dataRoot), state);
            using var client = await ObsSocket.ConnectAsync(state, true).ConfigureAwait(false);
            // The WebSocket server can listen before OBS finishes loading its scene collection.
            var special = await client.CallAsync("GetSpecialInputs", waitForReady: true).ConfigureAwait(false);
            if (special.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.Null))
                throw new IOException("OBS 存在桌面或麦克风音源，已阻止录制。");
            var input = await client.CallAsync("CreateInput", new { sceneName = Profile, inputName = "GameOnly", inputKind = "game_capture",
                inputSettings = new { capture_mode = "window", window = target, priority = 0, capture_audio = options.Audio,
                    capture_cursor = options.Cursor, limit_framerate = true }, sceneItemEnabled = true }).ConfigureAwait(false);
            int item = input.GetProperty("sceneItemId").GetInt32();
            await client.CallAsync("SetSceneItemTransform", new { sceneName = Profile, sceneItemId = item,
                sceneItemTransform = new { boundsType = "OBS_BOUNDS_SCALE_INNER", boundsWidth = options.Width, boundsHeight = options.Height, positionX = 0, positionY = 0 } }).ConfigureAwait(false);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                var transform = await client.CallAsync("GetSceneItemTransform", new { sceneName = Profile, sceneItemId = item }).ConfigureAwait(false);
                if (transform.GetProperty("sceneItemTransform").GetProperty("sourceWidth").GetDouble() > 0) break;
                if (timer.Elapsed.TotalSeconds >= 20) throw new IOException("OBS 未能捕获目标游戏；不会改为桌面录制。请检查游戏捕获兼容性及权限。");
                await Task.Delay(200).ConfigureAwait(false);
            }
            if (automaticProfile != null)
            {
                options.Output = Path.GetFullPath(CreateOutputPath(automaticProfile, DateTimeOffset.Now));
                state = state with { Output = options.Output };
                Storage.Write(StatePath(dataRoot), state);
            }
            await client.CallAsync("StartRecord").ConfigureAwait(false);
            timer.Restart();
            while (true)
            {
                var started = await GetStatusAsync(client, state).ConfigureAwait(false);
                if (started.Active) return started;
                if (timer.Elapsed.TotalSeconds >= 20) throw new IOException("OBS 未能开始录制，请检查编码器及输出路径。");
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            try
            {
                if (state != null) await ShutdownAsync(state).ConfigureAwait(false);
                if (installed) RestoreInstalledSettings(dataRoot);
                File.Delete(StatePath(dataRoot));
            }
            catch (Exception cleanup) { throw new AggregateException(error.Message + "\nOBS 清理未完成：" + cleanup.Message, error, cleanup); }
            throw;
        }
    }

    public static async Task<RecordingStatus> StatusAsync(string dataRoot)
    {
        if (!File.Exists(StatePath(dataRoot))) return new(false, null, null, Message: "没有录制会话。");
        var state = Storage.Read<RecordingState>(StatePath(dataRoot));
        using var process = FindObsProcess(state);
        if (process == null) return new(false, state.Output, state.WorkingFile, Message: "OBS 已退出；请检查工作录像并执行 stop 清理会话记录。");
        using var client = await ObsSocket.ConnectAsync(state, false).ConfigureAwait(false);
        return await GetStatusAsync(client, state).ConfigureAwait(false);
    }

    public static async Task<RecordingStatus> StopAsync(string dataRoot, string? expectedWorkingFile = null)
    {
        using var control = Lock(dataRoot);
        if (!File.Exists(StatePath(dataRoot)))
        {
            if (expectedWorkingFile != null) return new(false, null, null, Message: "原录制会话已结束。");
            throw new IOException("没有由本应用启动的录制会话。");
        }
        var state = Storage.Read<RecordingState>(StatePath(dataRoot));
        if (expectedWorkingFile != null && state.WorkingFile != expectedWorkingFile)
            return new(false, null, null, Message: "原录制会话已结束；当前录制未受影响。");
        using var process = FindObsProcess(state);
        if (process == null)
        {
            if (!string.IsNullOrWhiteSpace(state.Options.ObsExecutable)) RestoreInstalledSettings(dataRoot);
            File.Delete(StatePath(dataRoot));
            return new(false, state.Output, state.WorkingFile, Message: "OBS 已异常退出，已清理会话记录；工作录像保留，请自行检查完整性。");
        }
        using (var client = await ObsSocket.ConnectAsync(state, false).ConfigureAwait(false))
        {
            var status = await GetStatusAsync(client, state).ConfigureAwait(false);
            if (status.Active)
            {
                var stopped = await client.CallAsync("StopRecord").ConfigureAwait(false);
                if (!Path.GetFullPath(stopped.GetProperty("outputPath").GetString()!).Equals(state.WorkingFile, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("OBS 返回了不同的录像路径；已停止本次操作，请检查专用 OBS。");
                var timer = Stopwatch.StartNew();
                while ((await GetStatusAsync(client, state).ConfigureAwait(false)).Active)
                {
                    if (timer.Elapsed.TotalSeconds >= 30) throw new IOException("OBS 停止录制超时，录像与会话记录保留；请检查 OBS 状态。");
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }
        }
        await ShutdownAsync(state).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(state.Options.ObsExecutable)) RestoreInstalledSettings(dataRoot);
        if (!File.Exists(state.WorkingFile)) throw new IOException("OBS 已结束，但未找到工作录像：" + state.WorkingFile);
        File.Move(state.WorkingFile, state.Output, false);
        File.Delete(StatePath(dataRoot));
        return new(false, state.Output, null, Message: "录像已保存。");
    }

    static async Task<RecordingStatus> GetStatusAsync(ObsSocket client, RecordingState state)
    {
        var result = await client.CallAsync("GetRecordStatus").ConfigureAwait(false);
        return new(result.GetProperty("outputActive").GetBoolean(), state.Output, state.WorkingFile,
            result.GetProperty("outputDuration").GetDouble());
    }

    static void EnsureObsClosed()
    {
        bool running = false;
        foreach (var process in Process.GetProcessesByName("obs64").Concat(Process.GetProcessesByName("obs32")))
            using (process) running |= !process.HasExited;
        if (running) throw new IOException("请先退出正在运行的 OBS，再由本应用启动录制，避免同时修改 OBS 的共享设置。");
    }

    static void BackupInstalledSettings(string dataRoot, string config)
    {
        var files = new Dictionary<string, byte[]?>();
        foreach (string name in InstalledSettingsFiles)
            foreach (string suffix in new[] { "", ".bak" })
            {
                string relative = name + suffix, path = Path.Combine(config, relative);
                files.Add(relative, File.Exists(path) ? File.ReadAllBytes(path) : null);
            }
        Storage.Write(SettingsBackupPath(dataRoot), ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(files), null, DataProtectionScope.CurrentUser));
    }

    static void RestoreInstalledSettings(string dataRoot)
    {
        if (!File.Exists(SettingsBackupPath(dataRoot))) return;
        EnsureObsClosed();
        var files = JsonSerializer.Deserialize<Dictionary<string, byte[]?>>(ProtectedData.Unprotect(
            Storage.Read<byte[]>(SettingsBackupPath(dataRoot)), null, DataProtectionScope.CurrentUser))!;
        foreach (string name in InstalledSettingsFiles)
            foreach (string suffix in new[] { "", ".bak" })
            {
                byte[]? contents = files[name + suffix];
                string path = Path.Combine(InstalledConfigDirectory, name + suffix);
                if (contents == null) File.Delete(path);
                else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, contents); }
            }
        File.Delete(SettingsBackupPath(dataRoot));
    }

    public static void Recover(string dataRoot)
    {
        using var control = Lock(dataRoot);
        if (!File.Exists(SettingsBackupPath(dataRoot))) throw new IOException("没有待恢复的现有 OBS 设置。");
        RestoreInstalledSettings(dataRoot);
    }

    static void Prepare(string root, string config, RecordingOptions options, string working, int port, string password, bool installed)
    {
        string profile = Path.Combine(config, "basic", "profiles", Profile);
        Directory.CreateDirectory(profile);
        if (installed) File.WriteAllText(Path.Combine(profile, "coldclientmanager-obs.txt"), "Dedicated ColdClientManager profile and scene collection.");
        else File.WriteAllText(Path.Combine(root, "portable_mode.txt"), "");
        string settings = $"[General]\nFirstRun=true\nConfirmOnExit=false\n[Basic]\nProfile={Profile}\nProfileDir={Profile}\nSceneCollection={Profile}\nSceneCollectionFile={Profile}\n[BasicWindow]\nPreviewEnabled=false\nSysTrayEnabled=true\n";
        File.WriteAllText(Path.Combine(config, "user.ini"), settings);
        File.WriteAllText(Path.Combine(config, "global.ini"), installed ? settings.Replace("[General]\n", "[General]\nEnableAutoUpdates=false\n") : "[General]\nEnableAutoUpdates=false\n");
        string format = Path.GetExtension(working) == ".mkv" ? "mkv" : "mp4";
        string encoder = options.Encoder == "qsv" ? "obs_qsv11_v2" : "obs_x264";
        File.WriteAllText(Path.Combine(profile, "basic.ini"), $"""
            [General]
            Name={Profile}
            [Video]
            BaseCX={options.Width}
            BaseCY={options.Height}
            OutputCX={options.Width}
            OutputCY={options.Height}
            FPSType=1
            FPSInt={options.Fps}
            ColorFormat=NV12
            ColorSpace=709
            ColorRange=Partial
            [Output]
            Mode=Advanced
            FilenameFormatting={Path.GetFileNameWithoutExtension(working)}
            [AdvOut]
            RecType=Standard
            RecFilePath={Path.GetDirectoryName(working)!.Replace(Path.DirectorySeparatorChar, '/')}
            RecFormat2={format}
            RecEncoder={encoder}
            RecAudioEncoder=ffmpeg_aac
            RecTracks=1
            Track1Bitrate=128
            [Audio]
            SampleRate=48000
            ChannelSetup=Stereo
            """);
        Storage.Write(Path.Combine(profile, "recordEncoder.json"), new { rate_control = "CBR", bitrate = options.Bitrate,
            target_usage = "TU4", preset = "veryfast", profile = "high", keyint_sec = 2, bframes = 2 });
        Storage.Write(Path.Combine(config, "plugin_config", "obs-websocket", "config.json"), new { server_enabled = true,
            server_port = port, auth_required = true, server_password = password, first_load = false, alerts_enabled = false });
        Storage.Write(Path.Combine(config, "basic", "scenes", Profile + ".json"), new { name = Profile, current_scene = Profile,
            current_program_scene = Profile, scene_order = new[] { new { name = Profile } },
            sources = new[] { new { name = Profile, id = "scene", versioned_id = "scene", settings = new { items = Array.Empty<object>() } } } });
    }

    static Process? FindObsProcess(RecordingState state)
    {
        Process process;
        try { process = Process.GetProcessById(state.ObsProcessId); }
        catch (ArgumentException) { return null; }
        if (process.StartTime.ToUniversalTime().Ticks == state.ObsStartTime && string.Equals(process.MainModule!.FileName,
            string.IsNullOrWhiteSpace(state.Options.ObsExecutable) ? Path.Combine(state.ObsDirectory, "bin", "64bit", "obs64.exe") : state.Options.ObsExecutable,
            StringComparison.OrdinalIgnoreCase)) return process;
        process.Dispose(); return null;
    }

    static async Task ShutdownAsync(RecordingState state)
    {
        using var process = FindObsProcess(state);
        if (process == null) return;
        IntPtr window = IntPtr.Zero;
        EnumWindows((handle, _) => {
            GetWindowThreadProcessId(handle, out int pid);
            if (pid == state.ObsProcessId && WindowText(handle).StartsWith("OBS ", StringComparison.Ordinal)) { window = handle; return false; }
            return true;
        }, IntPtr.Zero);
        if (window == IntPtr.Zero || !PostMessageW(window, 0x0010, IntPtr.Zero, IntPtr.Zero))
            throw new IOException("无法正常关闭专用 OBS，请手动退出它；不会强制终止录制进程。");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    public static async Task<int> FindGameProcessAsync(GameProcess game, string executable)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
                using (process)
                    if (game.ContainsProcess(process) && process.MainWindowHandle != IntPtr.Zero &&
                        string.Equals(process.MainModule!.FileName, executable, StringComparison.OrdinalIgnoreCase)) return process.Id;
            if (timer.Elapsed.TotalSeconds >= 30) throw new IOException("30 秒内未找到本次启动的游戏窗口，录制没有开始。");
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    static async Task<string> WaitForWindowAsync(int pid)
    {
        using var process = Process.GetProcessById(pid);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            process.Refresh();
            if (process.HasExited) throw new IOException("目标游戏进程已经退出。");
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                string title = WindowText(process.MainWindowHandle), name = Path.GetFileName(process.MainModule!.FileName);
                var cls = new StringBuilder(256);
                if (GetClassNameW(process.MainWindowHandle, cls, cls.Capacity) == 0) throw new Win32Exception();
                foreach (var other in Process.GetProcessesByName(process.ProcessName))
                    using (other)
                        if (other.Id != pid && other.MainWindowHandle != IntPtr.Zero && WindowText(other.MainWindowHandle) == title)
                            throw new IOException("存在同名的多个游戏窗口，OBS 无法可靠区分；请先关闭额外实例。");
                static string Escape(string value) => value.Replace("#", "#22").Replace(":", "#3A");
                return Escape(title) + ":" + Escape(cls.ToString()) + ":" + Escape(name);
            }
            if (timer.Elapsed.TotalSeconds >= 30) throw new IOException("目标进程尚无可捕获窗口。");
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    static string WindowText(IntPtr window)
    {
        var text = new StringBuilder(GetWindowTextLengthW(window) + 1);
        GetWindowTextW(window, text, text.Capacity); return text.ToString();
    }
    delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern int GetClassNameW(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLengthW(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    sealed class ObsSocket : IDisposable
    {
        readonly ClientWebSocket socket;
        ObsSocket(ClientWebSocket socket) => this.socket = socket;
        public static async Task<ObsSocket> ConnectAsync(RecordingState state, bool starting)
        {
            var timer = Stopwatch.StartNew();
            ClientWebSocket socket;
            while (true)
            {
                socket = new ClientWebSocket();
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{state.Port}"), timeout.Token).ConfigureAwait(false);
                    break;
                }
                catch (WebSocketException) when (starting && timer.Elapsed.TotalSeconds < 30)
                {
                    socket.Dispose();
                    using var process = FindObsProcess(state);
                    if (process == null) throw new IOException("专用 OBS 启动失败，请检查其日志。");
                    await Task.Delay(200).ConfigureAwait(false);
                }
                catch { socket.Dispose(); throw; }
            }
            var client = new ObsSocket(socket);
            try
            {
                var hello = await client.ReceiveAsync().ConfigureAwait(false);
                if (hello.GetProperty("op").GetInt32() != 0) throw new InvalidDataException("OBS 未返回 WebSocket Hello。");
                var auth = hello.GetProperty("d").GetProperty("authentication");
                string password = Encoding.UTF8.GetString(ProtectedData.Unprotect(state.Password, null, DataProtectionScope.CurrentUser));
                static string Hash(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
                string secret = Hash(password + auth.GetProperty("salt").GetString());
                await client.SendAsync(new { op = 1, d = new { rpcVersion = 1, eventSubscriptions = 0,
                    authentication = Hash(secret + auth.GetProperty("challenge").GetString()) } }).ConfigureAwait(false);
                if ((await client.ReceiveAsync().ConfigureAwait(false)).GetProperty("op").GetInt32() != 2)
                    throw new IOException("OBS WebSocket 认证失败。");
                return client;
            }
            catch { client.Dispose(); throw; }
        }
        public async Task<JsonElement> CallAsync(string type, object? data = null, bool waitForReady = false)
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                string id = Guid.NewGuid().ToString("N");
                await SendAsync(new { op = 6, d = new { requestType = type, requestId = id, requestData = data ?? new { } } }).ConfigureAwait(false);
                var response = await ReceiveAsync().ConfigureAwait(false);
                if (response.GetProperty("op").GetInt32() != 7) throw new InvalidDataException("OBS 返回了错误的响应类型。");
                var result = response.GetProperty("d");
                if (result.GetProperty("requestId").GetString() != id) throw new InvalidDataException("OBS 响应不属于本次请求。");
                var status = result.GetProperty("requestStatus");
                if (!status.GetProperty("result").GetBoolean())
                {
                    if (waitForReady && status.GetProperty("code").GetInt32() == 207 && timer.Elapsed.TotalSeconds < 30)
                    {
                        await Task.Delay(200).ConfigureAwait(false); continue;
                    }
                    throw new IOException($"OBS {type} 失败：{status}");
                }
                return result.TryGetProperty("responseData", out var value) ? value : default;
            }
        }
        async Task SendAsync(object message)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        }
        async Task<JsonElement> ReceiveAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var content = new MemoryStream();
            var buffer = new byte[4096];
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                if (result.MessageType != WebSocketMessageType.Text) throw new IOException("OBS WebSocket 连接关闭或返回了非文本数据。");
                content.Write(buffer, 0, result.Count);
                if (content.Length > 1024 * 1024) throw new InvalidDataException("OBS 响应超过预期大小。");
            } while (!result.EndOfMessage);
            using var document = JsonDocument.Parse(content.ToArray());
            return document.RootElement.Clone();
        }
        public void Dispose() => socket.Dispose();
    }
}
