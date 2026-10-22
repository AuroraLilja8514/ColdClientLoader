using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ColdClientManager;
using SteamKit2;
using SteamKit2.Internal;

internal static partial class Tests
{
    static async Task RestoreTestAchievement(SteamSession session, SteamAchievement target)
    {
        var before = await AchievementSync.ReadRemoteAsync(session, 480);
        uint mask = AchievementSync.RemoteMask(before, target.Block) & ~(1u << target.Bit);
        var message = new ClientMsgProtobuf<CMsgClientStoreUserStats2>(EMsg.ClientStoreUserStats2) { SourceJobID = session.Client.GetNextJobID() };
        message.ProtoHeader.routing_appid = 480;
        message.Body.game_id = 480; message.Body.settor_steam_id = message.Body.settee_steam_id = session.SteamId;
        message.Body.crc_stats = before.crc_stats; message.Body.explicit_reset = true;
        message.Body.stats.Add(new() { stat_id = target.Block, stat_value = mask });
        var job = new AsyncJob<SteamStatsHandler.Stored>(session.Client, message.SourceJobID);
        session.Client.Send(message);
        var stored = (await job.ToTask().WaitAsync(TimeSpan.FromSeconds(30))).Body;
        Assert((EResult)stored.eresult == EResult.OK && !stored.stats_out_of_date && stored.stats_failed_validation.Count == 0,
            "Steam accepted cleanup of only the test achievement");
        var after = await AchievementSync.ReadRemoteAsync(session, 480);
        Assert(AchievementSync.RemoteMask(after, target.Block) == mask, "server readback confirms the test achievement is locked again");
        var schema = AchievementSync.ReadSchema(before);
        Assert(schema.Achievements.Where(a => a.Name != target.Name).All(a =>
            (AchievementSync.RemoteMask(before, a.Block) & (1u << a.Bit)) == (AchievementSync.RemoteMask(after, a.Block) & (1u << a.Bit))),
            "cleanup preserves every other achievement");
        var blocks = schema.Achievements.Select(a => a.Block).ToHashSet();
        Assert(before.stats.Where(s => !blocks.Contains(s.stat_id)).All(s => after.stats.Any(a => a.stat_id == s.stat_id && a.stat_value == s.stat_value)),
            "cleanup preserves ordinary Steam statistics");
    }

    static async Task TestSteamLive(string root, string mock, string loader, string steamApi, bool failAfterUpload)
    {
        string stateRoot = Path.Combine(root, "state"), fixture = Path.Combine(root, "fixture");
        Directory.CreateDirectory(fixture);
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(mock)!)) File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)), true);
        string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(steamApi)));
        File.Copy(steamApi, Path.Combine(fixture, "steam_api64.dll"), true);
        var registryBefore = JsonSerializer.Serialize(LoaderRegistry.Capture());
        await using var session = await SteamSession.ConnectAsync(Storage.DataRoot);
        Assert(session.SteamId != 0, "live test reuses saved Steam session without another login");
        var before = await AchievementSync.ReadRemoteAsync(session, 480);
        Storage.Write(Path.Combine(root, "spacewar-before.json"), before);
        var schema = AchievementSync.ReadSchema(before);
        var target = schema.Achievements.FirstOrDefault(a => (AchievementSync.RemoteMask(before, a.Block) & (1u << a.Bit)) == 0)
            ?? throw new InvalidOperationException("Spacewar 的全部成就已解锁；测试不会重置账号成就。");
        var profile = new GameProfile { Name = "Spacewar achievement fixture", AppId = "480", SteamId = session.SteamId.ToString(),
            UseColdClient = true, LoaderSource = Path.GetFullPath(loader), Executable = Path.Combine(fixture, Path.GetFileName(mock)),
            WorkingDirectory = fixture, Arguments = "steam-achievement " + target.Name };
        string launch = ColdClient.Prepare(profile, stateRoot);
        var initial = await AchievementSync.SynchronizeAsync(profile, stateRoot, session);
        Assert(initial.Uploaded == 0 && initial.Total == schema.Achievements.Count, "Spacewar schema and current server state prepared without an upload");
        string outputFile = AchievementSync.OutputFile(profile, stateRoot);
        byte[] originalLocal = File.ReadAllBytes(outputFile);
        try
        {
            using (var registry = new LoaderRegistry(stateRoot))
            {
                using var game = new GameProcess(launch, "", Path.GetDirectoryName(launch)!);
                game.Resume(); int exit = await game.WaitAsync();
                Assert(exit == 0, "ColdClient Spacewar fixture initializes Steam API and earns an achievement");
            }
            Assert(JsonSerializer.Serialize(LoaderRegistry.Capture()) == registryBefore, "Steam registry restored after the achievement fixture exits");
            var earned = AchievementSync.ReadLocal(AchievementSync.OutputFile(profile, stateRoot));
            Assert(earned[target.Name]?["earned"]?.GetValue<bool>() == true, "Goldberg output contains the fixture's newly earned Spacewar achievement");
            var result = await AchievementSync.SynchronizeAsync(profile, stateRoot, session);
            Assert(result.Uploaded == 1, "exactly one locally earned Spacewar achievement uploaded");
            var after = await AchievementSync.ReadRemoteAsync(session, 480);
            Storage.Write(Path.Combine(root, "spacewar-after.json"), after);
            Assert((AchievementSync.RemoteMask(after, target.Block) & (1u << target.Bit)) != 0, "Steam server readback confirms the new Spacewar unlock");
            if (failAfterUpload) throw new IOException("Intentional achievement fixture failure.");
            Assert(schema.Achievements.All(a => (AchievementSync.RemoteMask(before, a.Block) & (1u << a.Bit)) == 0
                || (AchievementSync.RemoteMask(after, a.Block) & (1u << a.Bit)) != 0), "pre-existing Steam achievement unlocks remain unlocked");
            var blocks = schema.Achievements.Select(a => a.Block).ToHashSet();
            Assert(before.stats.Where(s => !blocks.Contains(s.stat_id)).All(s => after.stats.Any(a => a.stat_id == s.stat_id && a.stat_value == s.stat_value)),
                "live achievement synchronization leaves ordinary Steam statistics unchanged");
            var repeated = await AchievementSync.SynchronizeAsync(profile, stateRoot, session);
            Assert(repeated.Uploaded == 0, "second live synchronization is idempotent");
            Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(steamApi))) == originalHash, "original game's Steam API binary is unchanged");
            Storage.Write(Path.Combine(root, "spacewar-verification.json"), new { AppId = 480, Achievement = target.Name, result.Uploaded, result.Unlocked, result.Total,
                PersistentLoginReused = true, ServerReadbackVerified = true, OrdinaryStatsUnchanged = true, OriginalSteamApiUnchanged = true });
        }
        finally
        {
            try { await RestoreTestAchievement(session, target); }
            finally { File.WriteAllBytes(outputFile, originalLocal); }
            Assert(File.ReadAllBytes(outputFile).SequenceEqual(originalLocal), "local test achievement state restored after the test");
            Storage.Write(Path.Combine(root, "spacewar-cleanup.json"), new { AppId = 480, Achievement = target.Name, Restored = true });
        }
    }

    static async Task TestSteamRead(string root)
    {
        await using var session = await SteamSession.ConnectAsync(Storage.DataRoot);
        Assert(session.SteamId != 0, "saved Steam login reused without password or Guard");
        var remote = await AchievementSync.ReadRemoteAsync(session, 480);
        Storage.Write(Path.Combine(root, "spacewar-before.json"), remote);
        var rawSchema = new KeyValue();
        using (var binary = new MemoryStream(remote.schema)) rawSchema.TryReadAsBinary(binary);
        using (var text = File.Create(Path.Combine(root, "spacewar-schema.vdf"))) rawSchema.SaveToStream(text, false);
        var schema = AchievementSync.ReadSchema(remote);
        Storage.Write(Path.Combine(root, "spacewar-achievements.json"), schema.Achievements);
        Storage.Write(Path.Combine(root, "spacewar-stats.json"), schema.Stats);
        Assert(schema.Achievements.Count > 0, "live Spacewar achievement schema received and parsed");
        Console.WriteLine($"Spacewar achievements: {schema.Achievements.Count}; locked: {schema.Achievements.Count(a => (AchievementSync.RemoteMask(remote, a.Block) & (1u << a.Bit)) == 0)}");
    }

    static void TestAchievements(string root)
    {
        var vdf = new KeyValue("480");
        var statRoot = new KeyValue("stats"); vdf.Children.Add(statRoot);
        var achievementBlock = new KeyValue("7"); statRoot.Children.Add(achievementBlock);
        achievementBlock.Children.Add(new("type", "4"));
        var bits = new KeyValue("bits"); achievementBlock.Children.Add(bits);
        foreach (var (name, bit) in new[] { ("REMOTE", 0), ("LOCAL", 1), ("LOCKED", 2), ("HIGH_BIT", 31) })
        {
            var item = new KeyValue(bit.ToString()); item.Children.Add(new("name", name));
            var display = new KeyValue("display"); display.Children.Add(new("name", name)); item.Children.Add(display);
            bits.Children.Add(item);
        }
        var integerStat = new KeyValue("99");
        integerStat.Children.Add(new("name", "GamesPlayed")); integerStat.Children.Add(new("type", "INT"));
        statRoot.Children.Add(integerStat);
        using var stream = new MemoryStream(); vdf.SaveToStream(stream, true);
        var remote = new CMsgClientGetUserStatsResponse { game_id = 480, eresult = 1, schema = stream.ToArray(), crc_stats = 42 };
        remote.stats.Add(new() { stat_id = 7, stat_value = 1 });
        var block = new CMsgClientGetUserStatsResponse.Achievement_Blocks { achievement_id = 7 };
        block.unlock_time.AddRange([1700000000, 0, 0, 0]); remote.achievement_blocks.Add(block);
        var schema = AchievementSync.ReadSchema(remote);
        Assert(schema.Achievements.Count == 4 && schema.Achievements[3].Bit == 31, "binary Steam schema preserves achievement bit indices including bit 31");
        achievementBlock["type"].Value = "ACHIEVEMENTS";
        stream.SetLength(0); stream.Position = 0; vdf.SaveToStream(stream, true); remote.schema = stream.ToArray();
        schema = AchievementSync.ReadSchema(remote);
        Assert(schema.Achievements.Count == 4 && schema.Stats[0]!["type"]!.GetValue<string>() == "int", "textual Steam schema types from live Spacewar are parsed correctly");
        var local = JsonNode.Parse("""{"LOCAL":{"earned":true,"earned_time":1700000100,"progress":5},"LOCKED":{"earned":false,"earned_time":0},"HIGH_BIT":{"earned":true,"earned_time":1700000200}}""")!.AsObject();
        var upload = AchievementSync.BuildUpload(schema, remote, local);
        Assert(upload.Count == 1 && upload[7] == 0x80000003u, "upload merges local unlocks with remote achievements without unlocking locked entries");
        AchievementSync.MergeLocal(schema, remote, local);
        Assert(local["REMOTE"]!["earned"]!.GetValue<bool>() && local["LOCAL"]!["earned"]!.GetValue<bool>()
            && local["LOCAL"]!["progress"]!.GetValue<int>() == 5, "download merges server unlocks and preserves offline unlocks and local metadata");
        remote.stats[0].stat_value = 0;
        Assert(AchievementSync.RemoteMask(remote, 7) == 0, "a retained unlock timestamp cannot re-unlock a cleared Steam achievement");
        remote.stats[0].stat_value = 0x80000003u;
        Assert(AchievementSync.BuildUpload(schema, remote, local).Count == 0, "already stored achievements do not cause repeat uploads");
        remote.stats.Add(new() { stat_id = 99, stat_value = 1234 });
        local["LOCKED"]!["earned"] = true;
        upload = AchievementSync.BuildUpload(schema, remote, local);
        Assert(upload.Count == 1 && upload[7] == 0x80000007u && !upload.ContainsKey(99), "achievement upload leaves ordinary Steam stats unchanged");
        local["UNKNOWN"] = new JsonObject { ["earned"] = true };
        Throws<InvalidDataException>(() => AchievementSync.BuildUpload(schema, remote, local), "unknown earned names are reported instead of silently omitted");
        local.Remove("UNKNOWN"); local["LOCAL"]!["earned"] = "invalid";
        Throws<InvalidDataException>(() => AchievementSync.BuildUpload(schema, remote, local), "malformed external achievement records cannot be uploaded");
        string broken = Path.Combine(root, "achievements", "invalid.json"); Write(broken, "[]");
        Throws<InvalidDataException>(() => AchievementSync.ReadLocal(broken), "array format cannot be mistaken for emulator earned-state object");
        var profile = new GameProfile { UseColdClient = true, AppId = "480", SteamId = "76561198000000000" };
        Throws<InvalidDataException>(() => SteamSession.ValidateProfile(new GameProfile { AppId = "0", SteamId = profile.SteamId }, 76561198000000000),
            "playing presence rejects an invalid AppID before starting the game");
        Assert(SteamSession.ValidateProfile(new GameProfile { AppId = "480", SteamId = profile.SteamId }, 76561198000000000) == 480,
            "playing presence supports direct launch without requiring achievement synchronization");
        Throws<InvalidDataException>(() => AchievementSync.ValidateProfile(profile, root, 76561198000000001), "account mismatch prevents achievement synchronization");
        Storage.Write(AchievementSync.AccountFile(profile, root), new AchievementAccount(480, 76561198000000001));
        Throws<InvalidDataException>(() => AchievementSync.ValidateProfile(profile, root, 76561198000000000), "changing profile account cannot upload the previous account's local achievements");
        string loginRoot = Path.Combine(root, "login-encryption");
        var login = new SteamLogin("fixture-user", "fixture-refresh-token"); SteamSession.SaveLogin(loginRoot, login);
        Assert(!File.ReadAllText(SteamSession.LoginPath(loginRoot)).Contains(login.RefreshToken)
            && SteamSession.ReadLogin(loginRoot) == login, "Windows encrypted session persists and round-trips without storing a plaintext refresh token");
    }
}
