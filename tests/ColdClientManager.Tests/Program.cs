using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ColdClientManager;

internal static class Tests
{
    static readonly List<string> passed = [];
    static void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed.Add(name); Console.WriteLine("PASS: " + name); }
    static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { Assert(true, name); return; }
        throw new Exception("FAIL (no expected exception): " + name);
    }
    static void Write(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--collect") { EtwCollector.Run(args[1]); return 0; }
        if (args.Length < 2) { Console.WriteLine("Tests <output-directory> <MockGame.exe> [--etw] [--ui]"); return 2; }
        string root = Path.GetFullPath(args[0]), mock = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(root);
        try
        {
            if (args.Contains("--recover"))
            {
                string path = args[Array.IndexOf(args, "--recover") + 1];
                LoaderRegistry.Recover(path);
                Assert(!File.Exists(Path.Combine(path, "loader-registry-recovery.json")), "sandbox-failed loader test recovery completed");
            }
            if (args.Contains("--ui"))
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                string uiRoot = Path.Combine(root, "ui");
                Storage.Write(Path.Combine(uiRoot, "profiles.json"), new[] { new GameProfile { Name = "模拟游戏 · 双系统存档", Executable = mock,
                    Rules = [new SyncRule { LocalPath = @"C:\Users\玩家\AppData\Roaming\游戏\saves", SharedPath = @"E:\GameSaves\游戏\saves", Patterns = "*.sav;*.dat" }] }, new GameProfile { Name = "第二个配置" } });
                using var form = new MainForm(uiRoot);
                form.Show(); Application.DoEvents();
                IEnumerable<Control> Descendants(Control parent)
                {
                    foreach (Control child in parent.Controls)
                    {
                        yield return child;
                        foreach (var nested in Descendants(child)) yield return nested;
                    }
                }
                var nameBox = Descendants(form).OfType<TextBox>().Single(t => t.Text == "模拟游戏 · 双系统存档");
                var gameList = Descendants(form).OfType<ComboBox>().Single(c => c.Items.Count > 0 && c.Items[0] is GameProfile);
                nameBox.Text = "模拟游戏 · 已保存修改"; gameList.SelectedIndex = 1;
                Assert(Storage.Read<List<GameProfile>>(Path.Combine(uiRoot, "profiles.json"))[0].Name == "模拟游戏 · 已保存修改", "switching game profiles preserves edits to previous profile");
                gameList.SelectedIndex = 0;
                Assert(gameList.Text == "模拟游戏 · 已保存修改", "saved game name refreshes in the game selector");
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(root, "ui-preview.png")); form.Close();
                Assert(true, "WinForms form creation and rendering");
                var sample = new DiscoveryResult { Files = [new(@"C:\Users\玩家\AppData\Roaming\游戏\saves\slot1.sav", 1, true, true, false)] };
                using var candidate = new CandidateForm(sample);
                candidate.Show(); Application.DoEvents();
                var checklist = Descendants(candidate).OfType<CheckedListBox>().Single();
                Assert(candidate.Selected.Count == 0, "discovered directories are never selected without user approval");
                checklist.SetItemChecked(0, true);
                Assert(candidate.Selected.Count == 1 && candidate.Selected[0].Patterns == "*.sav", "user selection carries the approved directory and file filter");
                using var candidateImage = new Bitmap(candidate.Width, candidate.Height);
                candidate.DrawToBitmap(candidateImage, new Rectangle(Point.Empty, candidate.Size)); candidateImage.Save(Path.Combine(root, "candidates-preview.png")); candidate.Close();
                var conflictProfile = Storage.Read<List<GameProfile>>(Path.Combine(uiRoot, "profiles.json"))[0];
                using var conflict = new ConflictForm(conflictProfile, new SyncPlan { Items = [new SyncItem { RuleId = conflictProfile.Rules[0].Id, RelativePath = "slot1.sav", LocalHash = "a", SharedHash = "b", Action = "Conflict" }] });
                conflict.Show(); Application.DoEvents();
                Assert((string?)Descendants(conflict).OfType<DataGridView>().Single().Rows[0].Cells[2].Value == "请选择", "conflicts have no preselected winning side");
                using var conflictImage = new Bitmap(conflict.Width, conflict.Height);
                conflict.DrawToBitmap(conflictImage, new Rectangle(Point.Empty, conflict.Size)); conflictImage.Save(Path.Combine(root, "conflicts-preview.png")); conflict.Close();
            }
            if (args.Contains("--etw")) TestCapture(root, mock).GetAwaiter().GetResult();
            else if (args.Contains("--cold")) TestRealCold(root, mock, args[Array.IndexOf(args, "--cold") + 1]).GetAwaiter().GetResult();
            else if (args.Contains("--permissions")) TestPermissions(root, args[Array.IndexOf(args, "--permissions") + 1]).GetAwaiter().GetResult();
            else if (!args.Contains("--ui") && !args.Contains("--recover")) { TestSync(root); TestCandidates(); TestColdPreparation(root, mock); TestProcessTree(root, mock).GetAwaiter().GetResult(); }
            Storage.Write(Path.Combine(root, "test-results.json"), new { Success = true, TimeUtc = DateTime.UtcNow, Tests = passed });
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Storage.Write(Path.Combine(root, "test-results.json"), new { Success = false, TimeUtc = DateTime.UtcNow, Tests = passed, Error = ex.ToString() });
            return 1;
        }
    }
    static void TestSync(string root)
    {
        string local = Path.Combine(root, "sync", "local"), shared = Path.Combine(root, "sync", "shared"), state = Path.Combine(root, "state");
        var rule = new SyncRule { LocalPath = local, SharedPath = shared, Patterns = "*.sav;*.dat" };
        var p = new GameProfile { Rules = [rule] };
        Write(Path.Combine(shared, "slot1.sav"), "other-os");
        Write(Path.Combine(shared, "cache.bin"), "not-a-save");
        var plan = SyncEngine.Plan(p, state); SyncEngine.Apply(p, state, plan);
        Assert(File.ReadAllText(Path.Combine(local, "slot1.sav")) == "other-os", "pre-play pull from other OS");
        Assert(!File.Exists(Path.Combine(local, "cache.bin")), "unselected extensions excluded");
        Write(Path.Combine(local, "slot1.sav"), "played-local");
        plan = SyncEngine.Plan(p, state); string backup = SyncEngine.Apply(p, state, plan);
        Assert(File.ReadAllText(Path.Combine(shared, "slot1.sav")) == "played-local", "post-play push to shared save");
        Assert(Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories).Any(f => File.ReadAllText(f) == "other-os"), "overwritten save backed up");
        Write(Path.Combine(local, "slot1.sav"), "local-conflict"); Write(Path.Combine(shared, "slot1.sav"), "shared-conflict");
        plan = SyncEngine.Plan(p, state);
        Assert(plan.Items.Single().Action == "Conflict", "both OS changes are a conflict");
        Throws<InvalidDataException>(() => SyncEngine.Apply(p, state, plan), "unresolved conflict cannot overwrite");
        plan.Items[0].Action = "Shared"; SyncEngine.Apply(p, state, plan);
        Assert(File.ReadAllText(Path.Combine(local, "slot1.sav")) == "shared-conflict", "explicit conflict selection honored");
        Write(Path.Combine(shared, "slot1.sav"), "new-other-os");
        plan = SyncEngine.Plan(p, state); Assert(plan.Items.Single().Action == "Shared", "three-way baseline identifies remote change");
        SyncEngine.Apply(p, state, plan);
        File.Delete(Path.Combine(local, "slot1.sav"));
        SyncEngine.Apply(p, state, SyncEngine.Plan(p, state));
        Assert(File.ReadAllText(Path.Combine(local, "slot1.sav")) == "new-other-os", "missing file restored, deletion not propagated");
        Write(Path.Combine(local, "nested", "new.dat"), "new slot");
        SyncEngine.Apply(p, state, SyncEngine.Plan(p, state));
        Assert(File.ReadAllText(Path.Combine(shared, "nested", "new.dat")) == "new slot", "approved directory includes new nested slots");
        Write(Path.Combine(local, "slot1.sav"), "planned"); plan = SyncEngine.Plan(p, state);
        Write(Path.Combine(local, "slot1.sav"), "changed-again");
        Throws<IOException>(() => SyncEngine.Apply(p, state, plan), "stale preview rejected before copying");
        Assert(File.ReadAllText(Path.Combine(shared, "slot1.sav")) == "new-other-os", "stale plan leaves other side unchanged");
        string changedMapping = Path.Combine(root, "sync", "other-shared"); Write(Path.Combine(changedMapping, "slot1.sav"), "new-other-os");
        rule.SharedPath = changedMapping;
        Assert(SyncEngine.Plan(p, state).Items.Single(i => i.RelativePath == "slot1.sav").Action == "Conflict", "changing mapping resets baseline interpretation");
        rule.SharedPath = Path.Combine(local, "inside");
        Throws<InvalidDataException>(() => SyncEngine.Plan(p, state), "overlapping roots rejected");
        rule.SharedPath = shared; rule.LocalPath = Path.GetPathRoot(local)!;
        Throws<InvalidDataException>(() => SyncEngine.Plan(p, state), "volume root rejected");
        rule.LocalPath = local;
        string fileA = Path.Combine(root, "single", "local.sav"), fileB = Path.Combine(root, "single", "other-name.sav");
        Write(fileA, "single");
        p.Rules = [new SyncRule { LocalPath = fileA, SharedPath = fileB, SingleFile = true }];
        SyncEngine.Apply(p, state, SyncEngine.Plan(p, state));
        Assert(File.ReadAllText(fileB) == "single", "single-file mapping can use different names");
        Write(fileB, "first-run-conflict"); p.Id = Guid.NewGuid().ToString("N");
        Assert(SyncEngine.Plan(p, state).Items.Single().Action == "Conflict", "first-run difference requires a choice");
        p.Rules[0].LocalPath = @"Z:\missing-volume\save.sav";
        if (!Directory.Exists(@"Z:\")) Throws<IOException>(() => SyncEngine.Plan(p, state), "unavailable volume is not an empty save folder");
        Assert(File.ReadAllText(Path.Combine(shared, "cache.bin")) == "not-a-save", "unrelated original data untouched");
        string temporaryParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!Path.GetPathRoot(temporaryParent)!.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
        {
            string crossRoot = Path.GetFullPath(Path.Combine(temporaryParent, "ColdClientManager-tests-" + Guid.NewGuid().ToString("N")));
            try
            {
                var cross = new GameProfile { Rules = [new SyncRule { LocalPath = Path.Combine(crossRoot, "local"), SharedPath = Path.Combine(root, "cross-volume-shared"), Patterns = "*.sav" }] };
                string crossState = Path.Combine(crossRoot, "state");
                Write(Path.Combine(cross.Rules[0].SharedPath, "slot.sav"), "initial");
                SyncEngine.Apply(cross, crossState, SyncEngine.Plan(cross, crossState));
                Write(Path.Combine(cross.Rules[0].LocalPath, "slot.sav"), "local-cross-volume");
                string crossBackup = SyncEngine.Apply(cross, crossState, SyncEngine.Plan(cross, crossState));
                Assert(File.ReadAllText(Path.Combine(cross.Rules[0].SharedPath, "slot.sav")) == "local-cross-volume"
                    && Directory.EnumerateFiles(crossBackup, "*", SearchOption.AllDirectories).Any(f => File.ReadAllText(f) == "initial"), "cross-volume backup and atomic replacement work (OS state C, shared E)");
            }
            finally
            {
                if (Path.GetDirectoryName(crossRoot) != temporaryParent || !Path.GetFileName(crossRoot).StartsWith("ColdClientManager-tests-")) throw new IOException("Unexpected temporary cleanup path");
                if (Directory.Exists(crossRoot)) Directory.Delete(crossRoot, true);
            }
        }
    }
    static void TestCandidates()
    {
        var result = new DiscoveryResult { Files = [new(@"C:\Users\player\AppData\Roaming\Game\saves\slot.sav", 12, true, true, false),
            new(@"C:\Users\player\AppData\Local\Game\GPUCache\data.bin", 12, true, true, false),
            new(@"C:\Windows\Temp\junk.sav", 12, true, true, false), new(@"C:\Games\Game\logs\x.log", 12, true, true, false),
            new(@"C:\Users\player\Documents\OtherGame\config.json", 18, false, true, false)] };
        var candidates = CandidateFinder.Find(result);
        Assert(candidates.Count == 2 && candidates[0].Patterns == "*.sav", "save candidates ranked, cache/log/temp excluded");
        Assert(candidates[1].Reason.Contains("人工判断"), "ambiguous written directory is a candidate, not auto-selected");
        result.Files.Add(new(@"C:\Users\player\AppData\Roaming\Game\saves\nested\slot.dat", 12, true, true, false));
        candidates = CandidateFinder.Find(result);
        Assert(candidates.Count == 2 && candidates[0].FileCount == 2 && candidates[0].Patterns.Contains("*.dat"), "nested candidate folders merge without overlapping sync rules");
    }
    static void TestColdPreparation(string root, string mock)
    {
        string source = Path.Combine(root, "loader-source"); Directory.CreateDirectory(source);
        foreach (string file in new[] { "steamclient_loader_x64.exe", "steamclient.dll", "steamclient64.dll" }) File.WriteAllText(Path.Combine(source, file), "fixture-component");
        var before = Directory.EnumerateFiles(source).ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        var p = new GameProfile { Executable = mock, WorkingDirectory = Path.GetDirectoryName(mock)!, Arguments = "root \"mock path\"",
            LoaderSource = Path.Combine(source, "steamclient_loader_x64.exe"), AppId = "123", SteamId = "76561198000000000" };
        string loader = ColdClient.Prepare(p, Path.Combine(root, "cold-state"));
        string ini = File.ReadAllText(Path.Combine(Path.GetDirectoryName(loader)!, "ColdClientLoader.ini"));
        Assert(ini.Contains(mock) && ini.Contains("Mode=0") && ini.Contains("AppId=123"), "ColdClient dedicated configuration points to selected program");
        Assert(before.All(pair => pair.Value == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key)))) && Directory.EnumerateFiles(source).Count() == 3,
            "ColdClient source components untouched");
        Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(mock)!, "steam_api64.dll")), "preparation does not install DLLs into target program");
    }
    static async Task TestProcessTree(string root, string mock)
    {
        string fixture = Path.Combine(root, "process-tree");
        using var game = new GameProcess(mock, "root \"" + fixture + "\"", Path.GetDirectoryName(mock)!);
        var watch = Stopwatch.StartNew(); game.Resume(); int exit = await game.WaitAsync();
        Assert(exit == 0 && watch.ElapsedMilliseconds >= 1400, "job waits for grandchild after root and child have exited");
        Assert(File.ReadAllText(Path.Combine(fixture, "profile", "saves", "slot1.sav")) == "grandchild-final", "late grandchild write captured before post-sync");
        Assert(Directory.EnumerateFiles(Path.Combine(fixture, "profile", "saves"), "*", SearchOption.AllDirectories).Count() == 7, "all short-lived branches complete");
        string shared = Path.Combine(root, "process-shared");
        var p = new GameProfile { Rules = [new SyncRule { LocalPath = Path.Combine(fixture, "profile", "saves"), SharedPath = shared, Patterns = "*.sav;*.dat" }] };
        SyncEngine.Apply(p, Path.Combine(root, "process-state"), SyncEngine.Plan(p, Path.Combine(root, "process-state")));
        Assert(File.ReadAllText(Path.Combine(shared, "slot1.sav")) == "grandchild-final", "post-exit sync includes final descendant progress");
    }
    static async Task TestCapture(string root, string mock)
    {
        string fixture = Path.Combine(root, "etw-game");
        await using var capture = await DiscoveryCapture.StartAsync(Path.Combine(root, "capture-data"));
        using var game = new GameProcess(mock, "root \"" + fixture + "\"", Path.GetDirectoryName(mock)!);
        await capture.AttachAsync(game.Id); game.Resume(); await game.WaitAsync();
        var result = await capture.FinishAsync();
        Storage.Write(Path.Combine(root, "captured-files.json"), result);
        Assert(result.EventsLost == 0, "realtime ETW reports zero lost events");
        Assert(result.Processes.Count == 7, "ETW attributes root, child, grandchild and four fast branches");
        var expected = Directory.EnumerateFiles(Path.Combine(fixture, "profile", "saves"), "*", SearchOption.AllDirectories).ToList();
        Assert(expected.Count == 7 && expected.All(p => result.Files.Any(f => f.Path.Equals(p, StringComparison.OrdinalIgnoreCase) && f.Created && f.Written)), "ETW extracts creation and successful write for all seven simulated save files");
        Assert(result.Files.Any(f => f.Directory && f.Created), "ETW distinguishes newly created directories");
        Assert(CandidateFinder.Find(result).All(c => !c.Path.Contains("cache", StringComparison.OrdinalIgnoreCase) && !c.Path.Contains("logs", StringComparison.OrdinalIgnoreCase)), "real capture candidate filtering excludes fixture noise");
    }
    static async Task TestRealCold(string root, string mock, string source)
    {
        var before = LoaderRegistry.Capture();
        string sourceFolder = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var hashes = Directory.EnumerateFiles(sourceFolder).ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        var profile = new GameProfile { Executable = mock, WorkingDirectory = Path.GetDirectoryName(mock)!,
            Arguments = "root \"" + Path.Combine(root, "mock-cold-game") + "\"", LoaderSource = source, AppId = "123", SteamId = "76561198000000000", UseColdClient = true };
        string data = Path.Combine(root, "cold-real-state"), launch = ColdClient.Prepare(profile, data);
        using (var registry = new LoaderRegistry(data))
        using (var game = new GameProcess(launch, "", Path.GetDirectoryName(launch)!))
        {
            game.Resume(); int exit = await game.WaitAsync();
            Assert(exit == 0, "actual ColdClientLoader launches only the mock program");
        }
        Assert(File.ReadAllText(Path.Combine(root, "mock-cold-game", "profile", "saves", "slot1.sav")) == "grandchild-final", "ColdClient launch waits for all mock descendants");
        Assert(JsonSerializer.Serialize(before) == JsonSerializer.Serialize(LoaderRegistry.Capture()), "all nine Steam registry values restored including missing values and original types");
        Assert(!File.Exists(Path.Combine(data, "loader-registry-recovery.json")), "successful restoration clears recovery record");
        Assert(hashes.All(pair => pair.Value == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key)))), "actual ColdClient source component hashes unchanged");
    }
    static async Task TestPermissions(string root, string app)
    {
        var local = Directory.CreateDirectory(Path.Combine(root, "admin-only-save"));
        string original = local.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        string shared = Path.Combine(root, "shared-save"), state = Path.Combine(root, "permission-state");
        string cleanup = Path.Combine(root, "Restore-FixtureAcl.ps1");
        File.WriteAllText(cleanup, """
            param([string]$Folder, [string]$Sddl)
            $ErrorActionPreference = 'Stop'
            $slot = Join-Path $Folder 'slot.sav'
            if (Test-Path -LiteralPath $slot) {
                $acl = [System.Security.AccessControl.FileSecurity]::new()
                $acl.SetSecurityDescriptorSddlForm($Sddl, [System.Security.AccessControl.AccessControlSections]::Access)
                Set-Acl -LiteralPath $slot -AclObject $acl
            }
            """);
        Write(Path.Combine(shared, "slot.sav"), "other-os-admin-protected");
        var profile = new GameProfile { Rules = [new SyncRule { LocalPath = local.FullName, SharedPath = shared, Patterns = "*.sav" }] };
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        async Task<ElevatedSyncResult> Helper(SyncPlan? plan)
        {
            string request = Path.Combine(root, "permission-request-" + Guid.NewGuid().ToString("N") + ".json");
            Storage.Write(request, new ElevatedSyncRequest(profile, state, plan));
            var info = new ProcessStartInfo(Path.GetFullPath(app)) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            info.ArgumentList.Add("--sync"); info.ArgumentList.Add(request);
            using var helper = Process.Start(info)!; await helper.WaitForExitAsync();
            var response = Storage.Read<ElevatedSyncResult>(request + ".result");
            if (response.Error != null) throw new IOException(response.Error);
            return response;
        }
        try
        {
            local.SetAccessControl(acl);
            Throws<UnauthorizedAccessException>(() => SyncEngine.Plan(profile, state), "normal user receives access denied for admin-only fixture");
            var plan = (await Helper(null)).Plan!;
            Assert(plan.Items.Single().Action == "Shared", "elevated app helper previews protected directory");
            await Helper(plan);
            Assert((await Helper(null)).Plan!.Items.All(i => i.Action == "Equal"), "elevated app helper verifies both protected copies match");
        }
        finally
        {
            var restore = new DirectorySecurity(); restore.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
            local.SetAccessControl(restore);
            var info = new ProcessStartInfo("pwsh.exe") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string argument in new[] { "-NoProfile", "-File", cleanup, local.FullName, original }) info.ArgumentList.Add(argument);
            using var helper = Process.Start(info)!; await helper.WaitForExitAsync();
            if (helper.ExitCode != 0) throw new IOException("临时测试文件的 ACL 恢复失败。");
        }
        Assert(File.ReadAllText(Path.Combine(local.FullName, "slot.sav")) == "other-os-admin-protected", "one-operation elevated sync succeeds and test ACL is restored");
    }
}
