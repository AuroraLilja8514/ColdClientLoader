using ColdClientManager;
using SteamKit2;

internal static partial class Tests
{
    static string ImportFixture(string root, string mock)
    {
        string apps = Path.Combine(root, "Steam Library", "steamapps");
        string folder = Path.Combine(apps, "common", "Fixture Game");
        Directory.CreateDirectory(folder);
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(mock)!, "MockGame.*"))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), true);
        Write(Path.Combine(apps, "appmanifest_480.acf"), """
            "AppState" { "appid" "480" "name" "Fixture from Steam" "installdir" "Fixture Game" "LastOwner" "76561198000000000" }
            """);
        return folder;
    }

    static async Task TestSteamImport(string root, string mock)
    {
        string fixture = Path.Combine(root, "import"), folder = ImportFixture(fixture, mock);
        string state = Path.Combine(fixture, "state"), exe = Path.Combine(folder, "MockGame.exe");
        var p = SteamGameImport.ReadInstall(folder);
        Assert(p.Name == "Fixture from Steam" && p.AppId == "480" && p.SteamId == "76561198000000000"
            && p.UseColdClient && p.SyncAchievements && p.ReportSteamPlaytime && p.RecordGame && !p.UseSteamCloud,
            "installed manifest populates the Steam import and enables its requested launch and recording features");
        Throws<InvalidDataException>(() => SteamGameImport.ReadInstall(fixture), "unmatched Steam installation cannot create an import");
        var app = KeyValue.LoadFromString("""
            "appinfo" { "config" { "launch" {
                "0" { "executable" "MockGame.exe" "workingdir" "work" "arguments" "-play" "config" { "oslist" "windows" } }
                "1" { "executable" "MockGame.exe" "arguments" "-linux" "config" { "oslist" "linux" } }
                "2" { "executable" "missing.exe" }
            } } "ufs" { "savefiles" {
                "0" { "root" "GameInstall" "path" "Saves" "pattern" "*.sav" }
                "1" { "root" "GameInstall" "path" "Saves" "pattern" "*.dat" }
            } } }
            """)!;
        Directory.CreateDirectory(Path.Combine(folder, "work"));
        var launch = SteamGameImport.FindLaunchOptions(folder, app).Single();
        Assert(launch == new SteamLaunchOption(exe, Path.Combine(folder, "work"), "-play"), "only installed Windows launch options qualify and keep Steam arguments and working directory");
        Write(Path.Combine(folder, "UnityCrashHandler64.exe"), "not a game");
        Write(Path.Combine(folder, "Engine", "Binaries", "Win64", "support.exe"), "not a game");
        Assert(SteamGameImport.FindLaunchOptions(folder, null).Single().Executable == exe, "offline launch search excludes engine tools and crash reporters");
        string packaged = Path.Combine(folder, "Project", "Binaries", "Win64", "Fixture-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(packaged)!); File.Copy(mock, packaged);
        var options = SteamGameImport.FindLaunchOptions(folder, app);
        Assert(options.Count == 2 && options[1] == launch with { Executable = packaged }, "Unreal bootstrapper and actual game are separate choices without losing launch settings");
        app["config"]["launch"]["0"]["executable"].Value = "../outside.exe";
        Throws<InvalidDataException>(() => SteamGameImport.FindLaunchOptions(folder, app), "a launch path cannot escape its installation");

        string tools = Path.Combine(fixture, "tools"), loader = Path.Combine(tools, "steamclient_loader_x64.exe"), obs = Path.Combine(tools, "obs64.exe");
        Directory.CreateDirectory(tools); File.Copy(mock, loader); File.Copy(mock, obs);
        Write(Path.Combine(tools, "steamclient.dll"), "fixture"); Write(Path.Combine(tools, "steamclient64.dll"), "fixture");
        string x86 = Path.Combine(tools, "steamclient_loader_x86.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "cmd.exe"), x86);
        var other = new GameProfile { UseColdClient = true, LoaderSource = x86, RecordGame = true,
            ObsExecutable = obs, RecordingFormat = "mp4", RecordingDirectory = Path.Combine(fixture, "videos") };
        SteamGameImport.SetLaunch(p, launch, [other], state);
        Assert(p.LoaderSource == loader && p.ObsExecutable == obs && p.RecordingDirectory == other.RecordingDirectory && p.RecordingFormat == "mp4",
            "import selects the matching loader architecture and reuses the recording setup");
        SteamGameImport.ValidateLaunch(p);
        Assert(!SteamGameImport.NeedsPortableObs(p, state), "configured external OBS requires no portable installation");
        p.LoaderSource = x86; Throws<InvalidDataException>(() => SteamGameImport.ValidateLaunch(p), "incompatible loader is rejected before import"); p.LoaderSource = loader;

        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), targetUser = Path.Combine(fixture, "target user");
        string relative = Path.Combine("Saved Games", "Fixture", "Slots");
        var prior = new GameProfile { Rules = [new() { LocalPath = Path.Combine(user, relative), SharedPath = Path.Combine(targetUser, relative) }] };
        Assert(SteamGameImport.PreviousTargetUser([prior], user, state) == targetUser, "matching suffix restores the previous Windows user target");
        var another = prior.Copy(); another.Rules[0].SharedPath = Path.Combine(fixture, "different user", relative);
        Assert(SteamGameImport.PreviousTargetUser([prior, another], user, state) == null, "ambiguous user mappings do not silently select a destination");
        Assert(SteamGameImport.MakeRule(p, state, targetUser, Path.Combine(user, relative), "*.sav").SharedPath == Path.Combine(targetUser, relative),
            "uncreated saves under the user directory map to the selected target's equivalent path");
        p.Rules = SteamGameImport.KnownRules(p, app["ufs"], state, targetUser);
        var rule = p.Rules.Single();
        Assert(rule.LocalPath == Path.Combine(folder, "Saves") && !rule.Recursive && rule.Patterns == "*.sav;*.dat"
            && rule.SharedPath.StartsWith(Storage.ProfileFolder(state, p)), "known directory rules merge exact filters and give non-user saves a separate local copy");
        Write(Path.Combine(rule.LocalPath, "slot.sav"), "progress"); Write(Path.Combine(rule.LocalPath, "sub", "nested.sav"), "other"); Write(Path.Combine(rule.LocalPath, "log.txt"), "noise");
        SyncEngine.Apply(p, state, SyncEngine.Plan(p, state));
        Assert(Directory.EnumerateFiles(rule.SharedPath, "*", SearchOption.AllDirectories).Single() == Path.Combine(rule.SharedPath, "slot.sav"),
            "first backup respects both nonrecursive Steam rules and their file filters");
        app["ufs"]["savefiles"]["0"].Children.Add(new KeyValue("recursive", "1"));
        Throws<InvalidDataException>(() => SteamGameImport.KnownRules(p, app["ufs"], state, null), "mixed recursion never widens a Steam save rule");
        Assert(SteamGameImport.KnownRules(p, new KeyValue("ufs"), state, null).Count == 0, "a game without UFS information needs save discovery");
        var api = KeyValue.LoadFromString(""" "ufs" { "quota" "1024" "maxnumfiles" "3" } """)!;
        Assert(SteamGameImport.KnownRules(p, api, state, null).Single().LocalPath == Path.Combine(AchievementSync.LoaderDirectory(p, state), "emu-data", "480", "remote"),
            "Steam API saves map to the imported profile's private loader");
        var exact = KeyValue.LoadFromString(""" "ufs" { "savefiles" { "0" { "root" "GameInstall" "path" "." "pattern" "progress.bin" } } } """)!;
        Assert(SteamGameImport.KnownRules(p, exact, state, null).Single() is { SingleFile: true } single && single.LocalPath == Path.Combine(folder, "progress.bin"),
            "an exact Steam save in the installation root imports as a single file");
        exact["savefiles"]["0"]["pattern"].Value = "*";
        Throws<InvalidDataException>(() => SteamGameImport.KnownRules(p, exact, state, null), "whole-installation wildcards need a confirmed save location");
        exact["savefiles"]["0"]["path"].Value = "Saves";
        p.Rules = SteamGameImport.KnownRules(p, exact, state, null);
        Write(Path.Combine(rule.LocalPath, "steam_autocloud.vdf"), "fixture marker");
        Assert(!SyncEngine.Plan(p, state).Items.Any(i => i.RelativePath == "steam_autocloud.vdf"), "Steam account marker files are excluded from imported wildcard rules");
        Assert(System.Text.Json.JsonSerializer.Deserialize<SyncRule>("{}")!.Recursive, "pre-import directory profiles still recurse by default");

        string config = Path.Combine(state, "profiles.json"); Storage.Write(config, new[] { other }); byte[] before = File.ReadAllBytes(config);
        string explicitTarget = Path.Combine(fixture, "command-target"), save = Path.Combine(fixture, "command-save");
        Write(Path.Combine(save, "slot.sav"), "new"); Write(Path.Combine(explicitTarget, "slot.sav"), "old");
        string[] args = [folder, "--offline", "--save-dir", save, "--target", explicitTarget, "--format", "mkv"];
        Assert(await SteamImportCommand.RunAsync(args, state) != 0 && File.ReadAllBytes(config).SequenceEqual(before), "unattended import waits for an explicit ambiguous executable choice");
        args = [.. args, "--launch", "1"];
        Assert(await SteamImportCommand.RunAsync(args, state) != 0 && File.ReadAllBytes(config).SequenceEqual(before) && File.ReadAllText(Path.Combine(explicitTarget, "slot.sav")) == "old",
            "unattended conflict preserves both saves and existing profiles");
        File.Delete(Path.Combine(explicitTarget, "slot.sav"));
        Assert(await SteamImportCommand.RunAsync(args, state) == 0 && File.ReadAllText(Path.Combine(explicitTarget, "slot.sav")) == "new",
            "unattended import produces a usable initial backup");
        var saved = Storage.Read<List<GameProfile>>(config);
        Assert(saved.Count == 2 && saved[0].Id == other.Id && saved[1].RecordGame && saved[1].SyncAchievements && saved[1].RecordingFormat == "mkv"
            && saved[1].Rules.Single().SharedPath == explicitTarget, "imported settings survive reloading alongside the original profiles");
        byte[] completed = File.ReadAllBytes(config);
        Assert(await SteamImportCommand.RunAsync(args, state) == 0 && File.ReadAllBytes(config).SequenceEqual(completed), "repeating an import preserves the existing entry");
    }

    static void TestImportUi(string root, string mock, string action)
    {
        string folder = ImportFixture(root, mock), state = Path.Combine(root, "state"), config = Path.Combine(state, "profiles.json");
        if (action == "cancel") File.Copy(mock, Path.Combine(folder, "第二个启动项.exe"), true);
        string firstRun = Path.Combine(root, "play"), local = Path.Combine(firstRun, "profile", "saves"), target = Path.Combine(root, "shared");
        int previews = 0;
        bool read = false, candidates = false;
        Exception? failure = null;
        using var home = new MainForm(state, folder) { ShowInTaskbar = false, Opacity = 0 };
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        var handled = new HashSet<Form>();
        var deadline = DateTime.UtcNow.AddMinutes(2);
        timer.Tick += (_, _) => {
            if (DateTime.UtcNow > deadline)
            {
                Storage.Write(Path.Combine(root, "test-results.json"), new { Success = false, Error = "导入界面测试超时", Log = Descendants(home).OfType<TextBox>().Single(t => t.AccessibleName == "活动记录").Text });
                Environment.Exit(1);
            }
            var modal = Application.OpenForms.Cast<Form>().LastOrDefault(f => f.Modal && f.Visible);
            if (modal == null)
            {
                if (read && Descendants(home).OfType<Button>().Single(b => b.Text == "从 Steam 导入").Enabled)
                { timer.Stop(); home.Close(); }
                return;
            }
            if (!handled.Add(modal)) return;
            try
            {
                var buttons = Descendants(modal).OfType<Button>();
                if (modal is SteamImportForm source)
                {
                    Assert(source.InstallDirectory == folder, "interactive import accepts a game directory supplied on the command line");
                    Descendants(source).OfType<CheckBox>().Single().Checked = false;
                    read = true; buttons.Single(b => b.Text == "读取游戏").PerformClick();
                }
                else if (modal.Text == "选择实际显示游戏画面的启动项")
                {
                    var list = Descendants(modal).OfType<ListBox>().Single();
                    list.SelectedIndex = list.FindString("第二个启动项.exe");
                    Assert(CopyValues(list).SequenceEqual([Path.Combine(folder, "第二个启动项.exe"), folder]),
                        "Steam launch choices copy the full executable and working directory separately and skip empty arguments");
                    buttons.Single(b => b.Text == "使用此启动项").PerformClick();
                }
                else if (modal is CandidateForm selection)
                {
                    var list = Descendants(selection).OfType<CheckedListBox>().Single();
                    Assert(selection.Selected.Count == 0, "first-run game writes require an explicit save-folder selection");
                    int index = Enumerable.Range(0, list.Items.Count).Single(i => string.Equals((string)list.Items[i]!, local, StringComparison.OrdinalIgnoreCase));
                    list.SetItemChecked(index, true); candidates = true;
                    buttons.Single(b => b.Text == "加入选中目录").PerformClick();
                }
                else if (modal is ProfileForm editor)
                {
                    previews++;
                    if (action == "cancel") { buttons.Single(b => b.Text == "取消").PerformClick(); return; }
                    var fields = Descendants(editor).OfType<TableLayoutPanel>().Single(p => p.ColumnCount == 3 && p.RowCount == 8);
                    if (previews == 1)
                    {
                        Assert(editor.Profile is { RecordGame: true, ReportSteamPlaytime: true, SyncAchievements: true, UseColdClient: true },
                            "interactive import previews enabled Steam and recording settings before any game starts");
                        ((ComboBox)fields.GetControlFromPosition(1, 4)!).SelectedIndex = 0;
                        foreach (var option in Descendants(editor).OfType<CheckBox>()) option.Checked = false;
                        if (action == "trace")
                        {
                            ((TextBox)fields.GetControlFromPosition(1, 3)!).Text = "root \"" + firstRun + "\"";
                            buttons.Single(b => b.Text == "保存并查找存档").PerformClick(); return;
                        }
                        Write(Path.Combine(local, "slot1.sav"), "fixture");
                        var grid = Descendants(editor).OfType<DataGridView>().Single();
                        ((System.ComponentModel.BindingList<SyncRule>)grid.DataSource!).Add(new SyncRule { LocalPath = local, SharedPath = target });
                    }
                    else
                    {
                        if (!candidates) throw new IOException("采集未提供候选目录。" + Environment.NewLine + Descendants(home).OfType<TextBox>().Single(t => t.AccessibleName == "活动记录").Text);
                        Assert(editor.Profile.Rules.Single().LocalPath.Equals(local, StringComparison.OrdinalIgnoreCase)
                            && Path.IsPathFullyQualified(editor.Profile.Rules.Single().SharedPath), "confirmed trace candidates return to the same import with an editable destination");
                        Descendants(editor).OfType<DataGridView>().Single().Rows[0].Cells[1].Value = target;
                    }
                    buttons.Single(b => b.Text == "完成导入").PerformClick();
                }
            }
            catch (Exception ex) { failure = ex; modal.Close(); }
        };
        timer.Start(); Application.Run(home);
        if (failure != null) throw failure;
        if (action == "cancel") Assert(previews == 1 && !File.Exists(config), "canceling import preview creates no profile");
        else
        {
            var saved = Storage.Read<List<GameProfile>>(config).Single();
            Assert(saved.Rules.Single().SharedPath == target && File.ReadAllText(Path.Combine(target, "slot1.sav")) == (action == "trace" ? "grandchild-final" : "fixture"),
                "the import confirmation persists one reusable game and performs its first backup (" + action + ")");
        }
    }

    static async Task TestSteamImportRead(string root, string folder)
    {
        var draft = SteamGameImport.ReadInstall(folder);
        await using var steam = await SteamSession.ConnectAsync(Storage.DataRoot);
        draft.SteamId = steam.SteamId.ToString();
        var info = await steam.GetAppInfoAsync(uint.Parse(draft.AppId));
        var launches = SteamGameImport.FindLaunchOptions(folder, info);
        Assert(launches.Count > 0 && launches.All(l => File.Exists(l.Executable)), "live Steam launch metadata resolves against the installed game");
        draft.Executable = launches[0].Executable; draft.WorkingDirectory = launches[0].Directory;
        var rules = SteamGameImport.KnownRules(draft, info["ufs"], root, null);
        Assert(rules.Count > 0, "live Steam save metadata resolves to directory backup rules");
        Storage.Write(Path.Combine(root, "read-only-import.json"), new { draft.AppId, draft.Name,
            Executables = launches.Select(l => Path.GetRelativePath(folder, l.Executable)), Rules = rules.Select(r => new { r.Patterns, r.Recursive }) });
    }
}
