using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ColdClientManager;

internal static partial class Tests
{
    static void TestRealtimeAchievementLaunch(string root, string mock, string loader, string steamApi, bool corruptDuringPlay)
    {
        Application.EnableVisualStyles();
        string state = Path.Combine(root, "state"), fixture = Path.Combine(root, "fixture");
        Directory.CreateDirectory(fixture);
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(mock)!)) File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)));
        string apiHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(steamApi)));
        File.Copy(steamApi, Path.Combine(fixture, "steam_api64.dll"), true);
        var session = SteamSession.ConnectAsync(Storage.DataRoot).GetAwaiter().GetResult();
        try
        {
            var before = AchievementSync.ReadRemoteAsync(session, 480).GetAwaiter().GetResult();
            var schema = AchievementSync.ReadSchema(before);
            var target = schema.Achievements.FirstOrDefault(a => (AchievementSync.RemoteMask(before, a.Block) & (1u << a.Bit)) == 0)
                ?? throw new InvalidOperationException("Spacewar 的全部成就已解锁；测试不会重置账号成就。");
            var profile = new GameProfile { Name = "Spacewar realtime achievement fixture", AppId = "480", SteamId = session.SteamId.ToString(),
                UseColdClient = true, SyncAchievements = true, ReportSteamPlaytime = false, LoaderSource = Path.GetFullPath(loader),
                Executable = Path.Combine(fixture, Path.GetFileName(mock)), WorkingDirectory = fixture,
                Arguments = "steam-achievement-realtime \"" + fixture + "\"" };
            var initial = AchievementSync.SynchronizeAsync(profile, state, session).GetAwaiter().GetResult();
            Assert(initial.Uploaded == 0, "realtime fixture prepares Spacewar without unlocking a server achievement");
            string output = AchievementSync.OutputFile(profile, state), loginPath = SteamSession.LoginPath(state);
            byte[] original = File.ReadAllBytes(output);
            Write(Path.Combine(fixture, "achievement-name.txt"), target.Name);
            Storage.Write(Path.Combine(state, "profiles.json"), new[] { profile });
            Directory.CreateDirectory(Path.GetDirectoryName(loginPath)!);
            File.Copy(SteamSession.LoginPath(Storage.DataRoot), loginPath);
            string registryBefore = JsonSerializer.Serialize(LoaderRegistry.Capture());
            try
            {
                using var form = new MainForm(state) { ShowInTaskbar = false, Opacity = 0 };
                Exception? failure = null;
                string messages = "";
                var log = (TextBox)typeof(MainForm).GetField("log", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
                form.Shown += async (_, _) => {
                    Task? launch = null;
                    try
                    {
                        launch = (Task)typeof(MainForm).GetMethod("LaunchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, [false, false])!;
                        async Task Until(Func<bool> ready)
                        {
                            DateTime deadline = DateTime.UtcNow.AddSeconds(40);
                            while (!ready())
                            {
                                if (launch.IsCompleted) { await launch; throw new IOException("Fixture exited before the realtime check completed."); }
                                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Realtime achievement fixture timed out.");
                                await Task.Delay(50);
                            }
                        }
                        await Until(() => File.Exists(Path.Combine(fixture, "achievement-ready.txt")));
                        using (var writer = new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                        {
                            await Task.Delay(3500);
                            Assert(!log.Text.Contains("实时成就同步已停止"), "an emulator save lock does not stop realtime monitoring");
                        }
                        Write(Path.Combine(fixture, "achievement-unlock.txt"), "unlock");
                        await Until(() => File.Exists(Path.Combine(fixture, "achievement-earned.txt")));
                        byte[] earned = File.ReadAllBytes(output);
                        DateTime written = File.GetLastWriteTimeUtc(output);
                        await Until(() => log.Text.Contains("Steam 实时成就同步：新上传 1 项"));
                        var remote = await AchievementSync.ReadRemoteAsync(session, 480);
                        Assert(!launch.IsCompleted && (AchievementSync.RemoteMask(remote, target.Block) & (1u << target.Bit)) != 0,
                            "Steam readback confirms a new achievement while the game is still running without playtime reporting");
                        Assert(File.ReadAllBytes(output).SequenceEqual(earned) && File.GetLastWriteTimeUtc(output) == written,
                            "realtime uploading does not rewrite the game's achievement file");
                        await Task.Delay(2300);
                        Assert(log.Text.Split("Steam 实时成就同步：新上传").Length == 2, "unchanged local records do not produce duplicate realtime uploads");
                        var blocks = schema.Achievements.Select(a => a.Block).ToHashSet();
                        Assert(schema.Achievements.Where(a => a.Name != target.Name).All(a =>
                            (AchievementSync.RemoteMask(before, a.Block) & (1u << a.Bit)) == (AchievementSync.RemoteMask(remote, a.Block) & (1u << a.Bit)))
                            && before.stats.Where(s => !blocks.Contains(s.stat_id)).All(s => remote.stats.Any(a => a.stat_id == s.stat_id && a.stat_value == s.stat_value)),
                            "realtime synchronization preserves other achievements and ordinary Steam statistics");
                        if (corruptDuringPlay)
                        {
                            Write(output, "{");
                            await Until(() => log.Text.Contains("Steam 实时成就同步已停止"));
                            Assert(!launch.IsCompleted, "invalid achievement data reports a realtime error without stopping the game");
                            File.WriteAllBytes(output, earned);
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                    finally
                    {
                        Write(Path.Combine(fixture, "achievement-unlock.txt"), "release");
                        Write(Path.Combine(fixture, "achievement-release.txt"), "release");
                        if (launch != null)
                        {
                            try { await launch; }
                            catch (AggregateException ex) when (corruptDuringPlay && ex.InnerExceptions.Any(e => e is JsonException))
                            { Assert(true, "the launch reports the realtime data failure after final synchronization"); }
                            catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
                        }
                        messages = log.Text;
                        form.Close();
                    }
                };
                Application.Run(form);
                if (failure != null) throw failure;
                Assert(messages.Split("Steam 成就同步完成").Length == 3 && messages.Contains("新上传 0 项"),
                    "automatic achievement synchronization keeps startup and final reconciliation without duplicate uploads");
                Assert(JsonSerializer.Serialize(LoaderRegistry.Capture()) == registryBefore, "realtime achievement launch restores the Steam registry");
                Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(steamApi))) == apiHash, "realtime fixture leaves the original Steam API binary unchanged");
                Storage.Write(Path.Combine(root, "realtime-verification.json"), new { AppId = 480, Achievement = target.Name,
                    UploadedWhilePlaying = true, PlaytimeReporting = false, LocalFileUnchanged = true, CorruptDuringPlay = corruptDuringPlay });
            }
            finally
            {
                try { RestoreTestAchievement(session, target).GetAwaiter().GetResult(); }
                finally { File.WriteAllBytes(output, original); File.Delete(loginPath); }
                Assert(File.ReadAllBytes(output).SequenceEqual(original), "realtime test restores the original local achievement records");
                Storage.Write(Path.Combine(root, "spacewar-cleanup.json"), new { AppId = 480, Achievement = target.Name, Restored = true });
            }
        }
        finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
