using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using ColdClientManager;
using SteamKit2;
using SteamKit2.Internal;

internal static partial class Tests
{
    // Only this test process uses the temporary proxy. Finish every CONNECT with an error, including late attempts.
    sealed class SteamOutage : IAsyncDisposable
    {
        readonly IWebProxy original = HttpClient.DefaultProxy;
        readonly TcpListener proxy = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly TaskCompletionSource rejectedTwice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Task server;
        public SteamOutage(SteamSession session)
        {
            proxy.Start();
            HttpClient.DefaultProxy = new WebProxy(new Uri("http://" + proxy.LocalEndpoint));
            server = RejectAsync();
            session.Client.Disconnect();
        }
        async Task RejectAsync()
        {
            try
            {
                for (int count = 1; ; count++)
                {
                    using var peer = await proxy.AcceptTcpClientAsync(stop.Token);
                    var stream = peer.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                    await stream.WriteAsync("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), stop.Token);
                    if (count == 2) rejectedTwice.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        public Task WaitForRetriesAsync() => rejectedTwice.Task.WaitAsync(TimeSpan.FromSeconds(45));
        public async ValueTask DisposeAsync()
        {
            HttpClient.DefaultProxy = original;
            stop.Cancel();
            try { await server; }
            finally { proxy.Stop(); stop.Dispose(); }
        }
    }

    static async Task TestSteamPlayingInterruption(string root, string mock)
    {
        await using var session = await SteamSession.ConnectAsync(Storage.DataRoot);
        var profile = new GameProfile { AppId = "480", SteamId = session.SteamId.ToString() };
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ConnectionProgress = new Progress<string>(message => { if (message.Contains("已确认恢复正在游玩")) restored.TrySetResult(); });
        using var game = new GameProcess(mock, "presence \"" + root + "\"", Path.GetDirectoryName(mock)!);
        try
        {
            await session.StartPlayingAsync(profile, game.Id);
            game.Resume();
            await using (var other = await SteamSession.ConnectAsync(Storage.DataRoot))
            {
                try
                {
                    await other.StartPlayingAsync(profile, game.Id);
                    throw new Exception("FAIL: a blocked session took over the playing client");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("其他客户端"))
                {
                    Assert(!session.PlayingInterrupted.IsCompleted, "another login refuses to take over an existing Steam playing session");
                }
            }
            var first = session.Client;
            Task<CMsgClientGetUserStatsResponse> stats;
            await using (var offline = new SteamOutage(session))
            {
                stats = AchievementSync.ReadRemoteAsync(session, 480);
                await offline.WaitForRetriesAsync();
                Assert(!stats.IsCompleted && !session.PlayingInterrupted.IsCompleted, "achievements and playtime survive repeated failed connections");
            }
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(90));
            Assert((await stats.WaitAsync(TimeSpan.FromSeconds(45))).schema.Length > 0 && first != session.Client,
                "Steam confirms the same game again and a pending achievement read completes on the new connection");
            await using (var offline = new SteamOutage(session))
            {
                await offline.WaitForRetriesAsync();
                Write(Path.Combine(root, "presence-exit.txt"), "exit");
                Assert(await game.WaitAsync() == 0, "the game exits normally during another outage");
                Assert(!await session.StopPlayingAsync().WaitAsync(TimeSpan.FromSeconds(5)), "ending play offline cancels the pending presence report");
            }
            await AchievementSync.ReadRemoteAsync(session, 480).WaitAsync(TimeSpan.FromSeconds(90));
            await using var check = await SteamSession.ConnectAsync(Storage.DataRoot);
            await check.StartPlayingAsync(profile, Environment.ProcessId);
            Assert(!check.PlayingInterrupted.IsCompleted, "the recovered session leaves the Steam playing slot free after game exit");
            Assert(await check.StopPlayingAsync(), "ending a confirmed report keeps the healthy connection until Steam confirms the stop");
        }
        finally
        {
            Write(Path.Combine(root, "presence-exit.txt"), "exit");
            await session.StopPlayingAsync();
        }
    }

    static void TestSteamLaunch(string root, string mock, string? loader)
    {
        Application.EnableVisualStyles();
        foreach (bool cold in loader == null ? new[] { false } : new[] { false, true })
        {
            string state = Path.Combine(root, cold ? "cold" : "direct"), saves = Path.Combine(state, "game", "profile", "saves"), shared = Path.Combine(state, "shared");
            ulong account;
            var login = SteamSession.ConnectAsync(Storage.DataRoot).GetAwaiter().GetResult();
            try { account = login.SteamId; }
            finally { login.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            var profile = new GameProfile { Name = "Spacewar launch fixture", Executable = mock, WorkingDirectory = Path.GetDirectoryName(mock)!,
                Arguments = "root \"" + Path.Combine(state, "game") + "\"", AppId = "480", SteamId = account.ToString(),
                UseColdClient = cold, LoaderSource = loader ?? "", SyncAchievements = cold, ReportSteamPlaytime = true,
                Rules = [new SyncRule { LocalPath = saves, SharedPath = shared, Patterns = "*.sav;*.dat" }] };
            Storage.Write(Path.Combine(state, "profiles.json"), new[] { profile });
            string loginPath = SteamSession.LoginPath(state);
            Directory.CreateDirectory(Path.GetDirectoryName(loginPath)!);
            File.Copy(SteamSession.LoginPath(Storage.DataRoot), loginPath);
            try
            {
                using var form = new MainForm(state) { ShowInTaskbar = false, Opacity = 0 };
                Exception? failure = null;
                string messages = "";
                form.Shown += async (_, _) => {
                    try
                    {
                        var method = typeof(MainForm).GetMethod("LaunchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                        await (Task)method.Invoke(form, [false, true, null, null])!;
                        messages = ((TextBox)typeof(MainForm).GetField("log", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!).Text;
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { form.Close(); }
                };
                Application.Run(form);
                if (failure != null) throw failure;
                Assert(messages.Contains("Steam 已确认正在游玩") && messages.Contains("Steam 已确认结束游玩状态"),
                    $"{(cold ? "ColdClientLoader" : "direct")} UI launch confirms Steam presence and clears it on exit");
                Assert(File.ReadAllText(Path.Combine(shared, "slot1.sav")) == "grandchild-final",
                    "Steam reporting preserves save synchronization after all game descendants exit");
                if (cold) Assert(messages.Split("Steam 成就同步完成").Length == 3, "one persistent Steam connection supports achievements before and after playing");
                Storage.Write(Path.Combine(state, "launch-verification.json"), new { ColdClient = cold, PresenceConfirmed = true, ExitConfirmed = true,
                    SaveSynced = true, AchievementSyncBeforeAndAfter = cold });
            }
            finally { File.Delete(loginPath); }
        }
    }

    static async Task<int> ReadSpacewarPlaytime(SteamSession session)
    {
        var player = session.Client.GetHandler<SteamUnifiedMessages>()!.CreateService<Player>();
        var response = await player.ClientGetLastPlayedTimes(new CPlayer_GetLastPlayedTimes_Request()).ToTask().WaitAsync(TimeSpan.FromSeconds(30));
        if (response.Result != EResult.OK) throw new IOException($"读取 Steam 时长失败：{response.Result}。");
        return response.Body.games.FirstOrDefault(g => g.appid == 480)?.playtime_forever ?? 0;
    }

    static async Task TestSteamPlaytime(string root, string mock)
    {
        await using var session = await SteamSession.ConnectAsync(Storage.DataRoot);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ConnectionProgress = new Progress<string>(message => { if (message.Contains("已确认恢复正在游玩")) restored.TrySetResult(); });
        int before = await ReadSpacewarPlaytime(session);
        Storage.Write(Path.Combine(root, "playtime-before.json"), new { AppId = 480, Minutes = before });
        var profile = new GameProfile { AppId = "480", SteamId = session.SteamId.ToString() };
        using var game = new GameProcess(mock, "playtime-root \"" + root + "\"", Path.GetDirectoryName(mock)!);
        var elapsed = Stopwatch.StartNew();
        try
        {
            await session.StartPlayingAsync(profile, game.Id);
            Assert(!session.PlayingInterrupted.IsCompleted, "Steam server confirms Spacewar playing presence");
            game.Resume();
            session.Client.Disconnect();
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(60));
            int exit = await game.WaitAsync();
            Assert(exit == 0 && File.Exists(Path.Combine(root, "playtime-finished.txt")) && elapsed.Elapsed.TotalSeconds >= 125,
                "playing session stays connected until the long-lived child exits after its launcher");
        }
        finally
        {
            bool cleared = await session.StopPlayingAsync();
            Assert(cleared && !session.PlayingInterrupted.IsCompleted, "Steam server confirms playing presence cleared on exit");
        }
        int after = await ReadSpacewarPlaytime(session);
        Storage.Write(Path.Combine(root, "playtime-verification.json"), new { AppId = 480, BeforeMinutes = before, AfterMinutes = after,
            ActualSeconds = elapsed.Elapsed.TotalSeconds, PlayingPresenceConfirmed = true, ReconnectConfirmed = true, ExitPresenceConfirmed = true });
        Assert(after > before, $"Steam playtime increased on the server after reconnect: {before} -> {after} minutes");
    }
}
