using System.Globalization;
using System.Text.Json;

namespace ColdClientManager;

public static class RecordingCommand
{
    public const string Help = """
        OBS 录制（只录游戏窗口和该进程音频；默认 1280x720 / 30 帧 / QSV H.264）：
          install [--archive 官方32.2.2-x64压缩包路径]
          start --pid 游戏进程ID --output 完整录像路径.mkv [选项]
          status
          stop
          recover                  退出 OBS 后恢复上次中断留下的现有 OBS 设置
        start 选项：
          --width 1280 --height 720 --fps 30 --bitrate 5000
          --encoder qsv|x264        显式选择编码器；不会自动切换
          --audio game|none         默认仅游戏音频，始终不录麦克风和桌面音频
          --cursor true|false       默认不录鼠标指针
          --duration 秒             可选；保持命令运行，到时自动停止
          --obs-dir 专用OBS目录      默认 %LOCALAPPDATA%\ColdClientManager\obs
          --obs-exe obs64.exe路径    使用现有 OBS 30.1+，无需 install；先退出其他 OBS
        --output 必填，支持 .mkv / .mp4；相对路径按当前目录解析，不覆盖已有文件。
        录制中工作文件位于所选目录，正常 stop 后使用指定的完整文件名。
        帧率是录像输出设置；游戏帧率低时会出现重复画面，不能生成额外的游戏帧。
        """;

    public static RecordingOptions ParseStart(string[] args, out int? duration)
    {
        var options = new RecordingOptions(); duration = null;
        var seen = new HashSet<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (!seen.Add(key) || ++i == args.Length) throw new ArgumentException("重复的选项或缺少值：" + key);
            string value = args[i];
            int Number() => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                ? number : throw new ArgumentException("选项需要正整数：" + key);
            switch (key)
            {
                case "--pid": options.ProcessId = Number(); break;
                case "--output": options.Output = value; break;
                case "--obs-dir": options.ObsDirectory = value; break;
                case "--obs-exe": options.ObsExecutable = value; break;
                case "--width": options.Width = Number(); break;
                case "--height": options.Height = Number(); break;
                case "--fps": options.Fps = Number(); break;
                case "--bitrate": options.Bitrate = Number(); break;
                case "--encoder": options.Encoder = value; break;
                case "--audio": options.Audio = value switch { "game" => true, "none" => false, _ => throw new ArgumentException("--audio 只接受 game 或 none。") }; break;
                case "--cursor": options.Cursor = bool.TryParse(value, out bool cursor) ? cursor : throw new ArgumentException("--cursor 只接受 true 或 false。"); break;
                case "--duration": duration = Number(); if (duration <= 0) throw new ArgumentException("录制时长必须大于 0 秒。"); break;
                default: throw new ArgumentException("未知的录制选项：" + key);
            }
        }
        options.Validate(); return options;
    }

    public static async Task<int> RunAsync(string[] args, string dataRoot)
    {
        try
        {
            if (args.Length == 0 || args is ["help"] or ["--help"]) { Console.WriteLine(Help); return 0; }
            switch (args[0])
            {
                case "install":
                    string? archive = args.Length == 3 && args[1] == "--archive" ? args[2] : null;
                    if (args.Length != 1 && archive == null) throw new ArgumentException("install 只接受可选的 --archive 压缩包路径。");
                    await ObsRecorder.InstallAsync(dataRoot, archive).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(new { Installed = true, Directory = ObsRecorder.DefaultObsDirectory(dataRoot) }, Storage.Json));
                    break;
                case "start":
                    var options = ParseStart(args[1..], out int? duration);
                    var started = await ObsRecorder.StartAsync(dataRoot, options).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(started, Storage.Json));
                    if (duration != null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(duration.Value)).ConfigureAwait(false);
                        Console.WriteLine(JsonSerializer.Serialize(await ObsRecorder.StopAsync(dataRoot, started.WorkingFile).ConfigureAwait(false), Storage.Json));
                    }
                    break;
                case "stop" when args.Length == 1:
                    Console.WriteLine(JsonSerializer.Serialize(await ObsRecorder.StopAsync(dataRoot).ConfigureAwait(false), Storage.Json)); break;
                case "status" when args.Length == 1:
                    Console.WriteLine(JsonSerializer.Serialize(await ObsRecorder.StatusAsync(dataRoot).ConfigureAwait(false), Storage.Json)); break;
                case "recover" when args.Length == 1:
                    ObsRecorder.Recover(dataRoot);
                    Console.WriteLine(JsonSerializer.Serialize(new { Restored = true }, Storage.Json)); break;
                default: throw new ArgumentException("未知的录制命令；请使用 --record help 查看用法。");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("录制操作未完成：" + ex.Message); return 1; }
    }
}
