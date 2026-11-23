using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ColdClientManager;
using SteamKit2;
using SteamKit2.Internal;

internal static partial class Tests
{
    static void TestSteamCloud(string root)
    {
        string folder = Path.Combine(root, "cloud"), local = Path.Combine(folder, "game", "Saves");
        var profile = new GameProfile { AppId = "480", SteamId = "76561198000000000", UseColdClient = true,
            Executable = Path.Combine(folder, "game", "game.exe"), WorkingDirectory = Path.Combine(folder, "game") };
        var ufs = KeyValue.LoadFromString("\"ufs\" { \"savefiles\" { \"0\" { \"root\" \"gameinstall\" \"path\" \"Saves\" \"pattern\" \"*.sav\" } } }")!;
        var locations = SteamCloudSync.ReadLocations(ufs, profile, folder);
        Assert(locations.Single().CloudPrefix == "%GameInstall%Saves" && locations[0].LocalPath == local && !locations[0].Recursive,
            "Steam Auto-Cloud resolves canonical root tokens, installation directories and nonrecursive patterns");
        Write(Path.Combine(local, "local.sav"), "new-local-save");
        Write(Path.Combine(local, "ignored.log"), "log");
        Write(Path.Combine(local, "sub", "nested.sav"), "nested");
        var remote = new CCloud_GetAppFileChangelist_Response(); remote.path_prefixes.Add("%GameInstall%Saves/");
        remote.files.Add(new() { file_name = "remote.sav", path_prefix_index = 0, platforms_to_sync = 1 });
        remote.files.Add(new() { file_name = "deleted.sav", path_prefix_index = 0, platforms_to_sync = 1, persist_state = ECloudStoragePersistState.k_ECloudStoragePersistStateDeleted });
        var files = SteamCloudSync.SelectFiles(locations, remote);
        Assert(files.Count == 2 && files.Any(f => f.Name == "%GameInstall%Saves/local.sav" && f.Remote == null)
            && files.Any(f => f.Name == "%GameInstall%Saves/remote.sav" && f.LocalPath == Path.Combine(local, "remote.sav")),
            "cloud selection includes new local and cloud-only saves while excluding logs, deleted files and nonrecursive children");
        ufs["savefiles"]["0"].Children.Add(new KeyValue("recursive") { Value = "1" });
        Assert(SteamCloudSync.SelectFiles(SteamCloudSync.ReadLocations(ufs, profile, folder), remote).Count == 3,
            "recursive Steam rules include nested save files");
        remote.files[0].file_name = "../outside.sav";
        Throws<InvalidDataException>(() => SteamCloudSync.SelectFiles(locations, remote), "untrusted cloud names cannot escape the selected save folder");
        Throws<InvalidDataException>(() => SteamCloudSync.Under(local, "slot.sav:private"), "cloud filenames cannot use alternate data streams");
        Throws<InvalidDataException>(() => SteamCloudSync.Under(local, @"C:\outside.sav"), "absolute cloud paths are rejected");
        var api = SteamCloudSync.ReadLocations(new KeyValue("ufs"), profile, folder).Single();
        Assert(api.CloudPrefix == "" && api.LocalPath == Path.Combine(AchievementSync.LoaderDirectory(profile, folder), "emu-data", "480", "remote"),
            "Steam API cloud files map to the selected game's ColdClientLoader remote storage");
        profile.UseColdClient = false;
        Throws<InvalidOperationException>(() => SteamCloudSync.ReadLocations(new KeyValue("ufs"), profile, folder), "Steam API cloud sync requires a known local emulator storage location");
        profile.UseColdClient = true;
        ufs.Children.Add(KeyValue.LoadFromString("\"rootoverrides\" { \"0\" { \"root\" \"gameinstall\" \"os\" \"Windows\" \"useinstead\" \"WinAppDataLocal\" \"addpath\" \"CloudFixture\" } }")!);
        var replacement = SteamCloudSync.ReadLocations(ufs, profile, folder).Single();
        Assert(replacement.CloudPrefix == "%GameInstall%Saves" && replacement.LocalPath == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudFixture", "Saves"),
            "Windows root overrides change the local path without changing the cloud filename prefix");
        var savedGames = KeyValue.LoadFromString("\"ufs\" { \"savefiles\" { \"0\" { \"root\" \"WinSavedGames\" \"path\" \"CloudFixture\" \"pattern\" \"*.sav\" } } }")!;
        Assert(Path.IsPathFullyQualified(SteamCloudSync.ReadLocations(savedGames, profile, folder).Single().LocalPath),
            "Saved Games cloud roots resolve through the Windows known-folder API before a game has created its save folder");
        ufs["savefiles"]["0"]["path"].Value = "{Steam3AccountID}/{64BitSteamID}";
        locations = SteamCloudSync.ReadLocations(ufs, profile, folder);
        Assert(locations.Single().CloudPrefix.EndsWith("39734272/76561198000000000"), "Steam account placeholders are substituted in cloud paths");

        byte[] contents = Encoding.UTF8.GetBytes("validated cloud save"), sha = SHA1.HashData(contents);
        string destination = Path.Combine(folder, "download", "slot.sav");
        var download = new CCloud_ClientFileDownload_Response { file_size = (uint)contents.Length, raw_file_size = (uint)contents.Length, sha_file = sha, time_stamp = 1700000000 };
        using (var input = new MemoryStream(contents)) SteamCloudSync.ReadDownload(input, destination, download);
        Assert(File.ReadAllBytes(destination).SequenceEqual(contents) && new DateTimeOffset(File.GetLastWriteTimeUtc(destination)).ToUnixTimeSeconds() == 1700000000,
            "cloud downloads preserve verified contents and the server modification timestamp");
        download.sha_file = new byte[20];
        using (var input = new MemoryStream(contents)) Throws<InvalidDataException>(() => SteamCloudSync.ReadDownload(input, destination, download), "cloud hash mismatches leave the previous file intact");
        Assert(File.ReadAllBytes(destination).SequenceEqual(contents), "failed cloud download verification does not overwrite a save");
        download.sha_file = sha;
        using (var compressed = new MemoryStream())
        {
            using (var zip = new ZipArchive(compressed, ZipArchiveMode.Create, true))
            {
                using var entry = zip.CreateEntry("slot.sav").Open(); entry.Write(contents);
            }
            download.file_size = (uint)compressed.Length; compressed.Position = 0;
            SteamCloudSync.ReadDownload(compressed, destination, download);
        }
        Assert(File.ReadAllBytes(destination).SequenceEqual(contents), "Steam single-file ZIP downloads are decompressed and hash-verified");

        var mapped = new GameProfile { UseSteamCloud = true, Rules = [new SyncRule { LocalPath = Path.Combine(folder, "pending", "local.sav"), SharedPath = Path.Combine(folder, "pending", "cache.sav"), SingleFile = true }] };
        Write(mapped.Rules[0].SharedPath, "remote-version");
        SyncEngine.Apply(mapped, folder, SyncEngine.Plan(mapped, folder));
        string baseline = Path.Combine(Storage.ProfileFolder(folder, mapped), "cloud-baseline.json");
        byte[] previous = File.ReadAllBytes(baseline);
        Write(mapped.Rules[0].LocalPath, "unsent-progress");
        SyncEngine.Apply(mapped, folder, SyncEngine.Plan(mapped, folder), false);
        Assert(File.ReadAllBytes(baseline).SequenceEqual(previous), "staging an upload does not commit the last successful cloud baseline");
        Write(mapped.Rules[0].SharedPath, "remote-version");
        Assert(SyncEngine.Plan(mapped, folder).Items.Single().Action == "Local", "failed cloud upload keeps unsent local progress for the next synchronization");
        Assert(!File.Exists(Path.Combine(Storage.ProfileFolder(folder, mapped), "baseline.json")), "cloud synchronization uses a baseline separate from directory synchronization");
    }

    static async Task TestSteamCloudLive(string root, string mock)
    {
        await using var steam = await SteamSession.ConnectAsync(Storage.DataRoot);
        var service = steam.Client.GetHandler<SteamUnifiedMessages>()!.CreateService<Cloud>();
        async Task<CCloud_GetAppFileChangelist_Response> List()
        {
            var result = await service.GetAppFileChangelist(new() { appid = 480, synced_change_number = 0 });
            Assert(result.Result == EResult.OK && !result.Body.is_only_delta, "Spacewar cloud returns a complete file listing");
            return result.Body;
        }
        string name = "ccm-cloud-test-" + Guid.NewGuid().ToString("N") + ".sav";
        var profile = new GameProfile { AppId = "480", SteamId = steam.SteamId.ToString(), UseColdClient = true, UseSteamCloud = true, Name = "Spacewar cloud fixture", Executable = mock, WorkingDirectory = Path.GetDirectoryName(mock)! };
        var before = await List();
        var original = before.files.Where(f => f.persist_state == ECloudStoragePersistState.k_ECloudStoragePersistStatePersisted).ToDictionary(f => f.file_name, f => Convert.ToHexString(f.sha_file));
        try
        {
            using (var initial = await SteamCloudSync.CreateAsync(profile, root, steam))
            {
                Assert(initial.Plan.Items.All(i => i.Action is "Shared" or "Equal"), "initial cloud sync only downloads the existing Spacewar saves into an isolated fixture");
                await initial.ApplyAsync();
            }
            string remoteFolder = Path.Combine(AchievementSync.LoaderDirectory(profile, root), "emu-data", "480", "remote");
            string file = Path.Combine(remoteFolder, name), text = "ColdClientManager cloud roundtrip " + Guid.NewGuid().ToString("N");
            Write(file, text);
            using (var upload = await SteamCloudSync.CreateAsync(profile, root, steam))
            {
                Assert(upload.Plan.Items.Count(i => i.Action == "Local") == 1 && upload.Plan.Items.All(i => i.Action is "Local" or "Equal"), "live fixture uploads only its uniquely named test file");
                await upload.ApplyAsync();
            }
            File.Delete(file);
            using (var download = await SteamCloudSync.CreateAsync(profile, root, steam))
            {
                Assert(download.Plan.Items.Single(i => download.Files[i.RuleId].Name == name).Action == "Shared", "Steam confirms the uploaded fixture and offers it for download");
                await download.ApplyAsync();
            }
            Assert(File.ReadAllText(file) == text, "Spacewar cloud upload and fresh download roundtrip preserves the complete save");
        }
        finally
        {
            var current = await List();
            if (current.files.Any(f => f.file_name == name && f.persist_state == ECloudStoragePersistState.k_ECloudStoragePersistStatePersisted))
            {
                var deleted = await service.Delete(new() { appid = 480, filename = name });
                Assert(deleted.Result == EResult.OK, "Steam accepted deletion of only the generated cloud fixture");
            }
            var after = await List();
            var remaining = after.files.Where(f => f.persist_state == ECloudStoragePersistState.k_ECloudStoragePersistStatePersisted).ToDictionary(f => f.file_name, f => Convert.ToHexString(f.sha_file));
            bool clean = remaining.Count == original.Count && original.All(p => remaining.TryGetValue(p.Key, out string? hash) && hash == p.Value);
            Storage.Write(Path.Combine(root, "cloud-cleanup.json"), new { Success = clean, TestFile = name, OriginalFiles = original.Count, RemainingFiles = remaining.Count });
            Assert(clean, "cloud fixture cleanup preserves every pre-existing Spacewar file and removes the test save");
        }
    }
}
