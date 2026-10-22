using System.Diagnostics;
using System.Reflection;
using ColdClientManager;
using SteamKit2;
using SteamKit2.Internal;

internal static partial class Tests
{
    static async Task TestSteamPlayingInterruption(string root, string mock)
    {
        await using var session = await SteamSession.ConnectAsync(Storage.DataRoot);
        var profile = new GameProfile { AppId = "480", SteamId = session.SteamId.ToString() };
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
            session.Client.Disconnect();
            var error = await session.PlayingInterrupted.WaitAsync(TimeSpan.FromSeconds(30));
            Assert(error is IOException, "disconnect during playing reports an interruption instead of continued playtime");
            Assert(await game.WaitAsync() == 0, "Steam disconnect does not stop the game process");
        }
        finally
        {
            try { await session.StopPlayingAsync(); }
            catch (IOException) when (!session.Client.IsConnected)
            {
                Assert(true, "disconnected session cannot falsely confirm a successful stop report");
            }
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
                        await (Task)method.Invoke(form, [false, true])!;
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
            int exit = await game.WaitAsync();
            Assert(exit == 0 && File.Exists(Path.Combine(root, "playtime-finished.txt")) && elapsed.Elapsed.TotalSeconds >= 125,
                "playing session stays connected until the long-lived child exits after its launcher");
        }
        finally
        {
            await session.StopPlayingAsync();
            Assert(!session.PlayingInterrupted.IsCompleted, "Steam server confirms playing presence cleared on exit");
        }
        int after = await ReadSpacewarPlaytime(session);
        Storage.Write(Path.Combine(root, "playtime-verification.json"), new { AppId = 480, BeforeMinutes = before, AfterMinutes = after,
            ActualSeconds = elapsed.Elapsed.TotalSeconds, PlayingPresenceConfirmed = true, ExitPresenceConfirmed = true });
        Assert(after > before, $"Steam playtime increased on the server: {before} -> {after} minutes");
    }
}
