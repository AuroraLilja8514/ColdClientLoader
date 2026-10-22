using ColdClientManager;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static partial class Tests
{
    static void TestRecordingOptions(string root)
    {
        var profile = new GameProfile { Name = "Order: 游戏/测试", RecordingDirectory = Path.Combine(root, "auto-recordings") };
        var started = new DateTimeOffset(2026, 10, 22, 10, 23, 45, 123, TimeSpan.FromHours(8));
        string automatic = ObsRecorder.CreateOutputPath(profile, started);
        Assert(Path.GetFileName(automatic).StartsWith("Order_ 游戏_测试_2026-10-22_10-23-45-123_") && Path.GetExtension(automatic) == ".mkv",
            "automatic recordings include the saved game name, Gregorian start date and millisecond timestamp with invalid filename characters replaced");
        Assert(automatic != ObsRecorder.CreateOutputPath(profile, started), "recordings started in the same millisecond receive distinct filenames");
        profile.RecordingFormat = "mp4";
        Assert(Path.GetExtension(ObsRecorder.CreateOutputPath(profile, started)) == ".mp4", "automatic recording uses the game's saved container format");
        profile.RecordingFormat = "../other";
        Throws<ArgumentException>(() => ObsRecorder.CreateOutputPath(profile, started), "invalid persisted recording formats cannot change the output path structure");
        string output = Path.Combine(root, "recording options", "游戏 100% 完整文件名.mkv");
        var options = RecordingCommand.ParseStart(["--pid", "123", "--output", output], out int? duration);
        Assert(options.Output == output && options.Width == 1280 && options.Height == 720 && options.Fps == 30
            && options.Encoder == "qsv" && options.Audio && !options.Cursor && duration == null,
            "recording accepts an exact Unicode output path and defaults to game-only 720p30 hardware capture");
        options = RecordingCommand.ParseStart(["--pid", "123", "--output", output, "--width", "640", "--height", "360", "--fps", "24",
            "--bitrate", "2500", "--encoder", "x264", "--audio", "none", "--cursor", "true", "--duration", "5"], out duration);
        Assert(options.Width == 640 && options.Height == 360 && options.Fps == 24 && options.Bitrate == 2500 && options.Encoder == "x264"
            && !options.Audio && options.Cursor && duration == 5, "recording CLI applies all explicit output, encoding and capture options");
        options = RecordingCommand.ParseStart(["--pid", "123", "--output", output, "--obs-exe", @"C:\Program Files\obs-studio\bin\64bit\obs64.exe"], out _);
        Assert(options.ObsExecutable == @"C:\Program Files\obs-studio\bin\64bit\obs64.exe" && options.ObsDirectory == "", "recording accepts an installed OBS executable without a portable installation");
        Throws<ArgumentException>(() => RecordingCommand.ParseStart(["--pid", "123", "--output", output, "--obs-exe", "obs64.exe", "--obs-dir", "portable"], out _),
            "recording rejects conflicting installed and portable OBS selections");
        Throws<ArgumentException>(() => RecordingCommand.ParseStart(["--pid", "123"], out _), "recording requires the caller to choose an output path");
        Throws<ArgumentException>(() => RecordingCommand.ParseStart(["--pid", "123", "--output", output, "--audio", "desktop"], out _), "recording never enables desktop audio through CLI options");
        Throws<ArgumentException>(() => RecordingCommand.ParseStart(["--pid", "123", "--output", output, "--width", "1279"], out _), "recording rejects dimensions incompatible with NV12 subsampling");
        Throws<ArgumentException>(() => RecordingCommand.ParseStart(["--pid", "123", "--output", output, "--fps", "30", "--fps", "60"], out _), "recording rejects ambiguous duplicate options");
        Write(output, "existing video");
        Throws<IOException>(() => RecordingCommand.ParseStart(["--pid", "123", "--output", output], out _), "recording refuses to overwrite an existing video");
        Assert(File.ReadAllText(output) == "existing video", "recording validation preserves existing video contents");
        string stateRoot = Path.Combine(root, "recording-session-ownership");
        var state = new RecordingState(123, 0, 1234, [], "unused", "current.mkv", "current-working.mkv", new());
        string statePath = Path.Combine(stateRoot, "recording", "session.json"); Storage.Write(statePath, state);
        byte[] before = File.ReadAllBytes(statePath);
        var stopped = ObsRecorder.StopAsync(stateRoot, "previous-working.mkv").GetAwaiter().GetResult();
        Assert(!stopped.Active && File.ReadAllBytes(statePath).SequenceEqual(before),
            "a completed timed or game session cannot stop a later recording session");
        TestObsStartupReady().GetAwaiter().GetResult();
    }

    static async Task TestObsStartupReady()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new List<string>();
        var server = Task.Run(async () => {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = connection.GetStream();
            var header = new StringBuilder(); var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n"))
            {
                if (await stream.ReadAsync(one, timeout.Token) == 0) throw new IOException("WebSocket fixture connection closed.");
                header.Append((char)one[0]);
            }
            string key = header.ToString().Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), timeout.Token);
            using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
            foreach (int code in new[] { 207, 100, 207, 600 })
            {
                var buffer = new byte[4096];
                var packet = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                using var document = JsonDocument.Parse(buffer.AsMemory(0, packet.Count));
                var request = document.RootElement.GetProperty("d");
                received.Add(request.GetProperty("requestType").GetString()!);
                var response = new { op = 7, d = new { requestId = request.GetProperty("requestId").GetString(),
                    requestStatus = new { result = code == 100, code }, responseData = new { desktop1 = (string?)null } } };
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(response), WebSocketMessageType.Text, true, timeout.Token);
            }
        });
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), timeout.Token);
        Type type = typeof(ObsRecorder).GetNestedType("ObsSocket", BindingFlags.NonPublic)!;
        using var owner = (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, [client], null)!;
        Task<JsonElement> Call(string name, bool wait) => (Task<JsonElement>)type.GetMethod("CallAsync")!.Invoke(owner, [name, null, wait])!;
        var result = await Call("GetSpecialInputs", true);
        Assert(result.GetProperty("desktop1").ValueKind == JsonValueKind.Null, "OBS startup waits for a demonstrated NotReady response before checking capture isolation");
        Throws<IOException>(() => Call("StartRecord", false).GetAwaiter().GetResult(), "OBS recording commands do not acquire startup retries");
        Throws<IOException>(() => Call("GetSpecialInputs", true).GetAwaiter().GetResult(), "OBS startup does not retry unrelated server errors");
        await server;
        Assert(received.SequenceEqual(new[] { "GetSpecialInputs", "GetSpecialInputs", "StartRecord", "GetSpecialInputs" }), "only the startup readiness check repeats after status 207");
    }
}
