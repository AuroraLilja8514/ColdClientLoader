using ColdClientManager;

internal static partial class Tests
{
    static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    static string[] CopyValues(Control control)
    {
        var menu = control.ContextMenuStrip!;
        menu.Show(control, Point.Empty);
        var values = menu.Items.Cast<ToolStripItem>().Where(i => i.Available && i.Enabled).Select(i => (string)i.Tag!).ToArray();
        menu.Close();
        return values;
    }
    static void TestLibraryUi(string root, string mock)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        void Capture(Form form, string file)
        {
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(root, file));
        }
        void InEditor(Form owner, string button, Action<ProfileForm> action)
        {
            Exception? failure = null;
            using var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, _) => {
                var editor = Application.OpenForms.OfType<ProfileForm>().SingleOrDefault();
                if (editor == null) return;
                timer.Stop();
                try { action(editor); }
                catch (Exception ex) { failure = ex; editor.Close(); }
            };
            timer.Start();
            Descendants(owner).OfType<Button>().Single(b => b.Text == button).PerformClick();
            if (failure != null) throw failure;
        }
        string uiRoot = Path.Combine(root, "ui"), config = Path.Combine(uiRoot, "profiles.json");
        var original = new GameProfile { Name = "模拟游戏 · 存档同步", Executable = mock, Arguments = "original arguments",
            Rules = [new SyncRule { LocalPath = Path.Combine(uiRoot, "模拟游戏 中文, 存档", "saves"), SharedPath = @"E:\GameSaves\游戏\saves", Patterns = "*.sav;*.dat" }] };
        Directory.CreateDirectory(original.Rules[0].LocalPath);
        Storage.Write(config, new[] { original, new GameProfile { Name = "第二个配置" } });
        using (var form = new MainForm(uiRoot))
        {
            form.Show(); Application.DoEvents();
            var gameList = Descendants(form).OfType<ListBox>().Single();
            Assert(gameList.Items.Count == 2 && gameList.SelectedItem is GameProfile p && p.Id == original.Id, "home page loads previously saved applications");
            var copied = CopyValues(gameList);
            Assert(copied.Take(3).SequenceEqual([original.Name, mock, Storage.ProfileFolder(uiRoot, original)]) && copied[3].Contains("存档同步：1 条存档规则")
                && Descendants(form).OfType<TextBox>().Any(t => t.ReadOnly && t.Text == mock), "library exposes the full program and backup paths for copying");
            Assert(Descendants(form).OfType<TextBox>().All(t => t.ReadOnly) && !Descendants(form).OfType<DataGridView>().Any(),
                "home page shows the application library without editable configuration fields");
            Assert(new[] { "同步并启动", "仅同步", "仅启动", "编辑设置", "添加应用" }.All(text => Descendants(form).OfType<Button>().Any(b => b.Text == text && b.Enabled)),
                "saved applications expose all three actions plus edit and add");
            Capture(form, "ui-preview.png");
            form.Size = form.MinimumSize; Capture(form, "ui-minimum-preview.png");
            var clipped = Descendants(form).OfType<Button>().Where(b => b.Parent is FlowLayoutPanel && (b.Right > b.Parent.ClientSize.Width || b.Bottom > b.Parent.ClientSize.Height));
            Assert(!clipped.Any(), "library actions fit the minimum supported window size: " + string.Join(", ", clipped.Select(b => b.Text)));
            var stateFields = Descendants(form).OfType<TextBox>().Where(t => t.AccessibleName is "存档同步" or "Steam 成就" or "状态与时长" or "OBS 录制").ToArray();
            Assert(stateFields.Length == 4 && stateFields.All(t => t.Visible && t.Top >= 0 && t.Bottom <= t.Parent!.ClientSize.Height && t.PreferredHeight <= t.Height)
                && stateFields.Single(t => t.AccessibleName == "存档同步").Text == "1 条存档规则",
                "each graphical feature includes a readable status at minimum window size");
            var cardLinks = Descendants(form).OfType<Button>().Where(b => b.AccessibleName?.StartsWith("编辑") == true).ToArray();
            Assert(cardLinks.Length == 4 && cardLinks.All(b => b.Height >= b.GetPreferredSize(new Size(b.Width, 0)).Height),
                "all four card titles fit their keyboard-accessible buttons at minimum size");
            byte[] beforeCancel = File.ReadAllBytes(config);
            InEditor(form, "OBS 录制  ›", editor => {
                Assert(Descendants(editor).OfType<TabControl>().Single().SelectedTab?.Text == "OBS 录制", "the recording card opens the game's recording settings directly");
                Descendants(editor).OfType<Button>().Single(b => b.Text == "取消").PerformClick();
            });
            InEditor(form, "编辑设置", editor => {
                var name = Descendants(editor).OfType<TextBox>().Single(t => t.Text == original.Name);
                name.Text = "discarded edit";
                var grid = Descendants(editor).OfType<DataGridView>().Single();
                grid.CurrentCell = grid.Rows[0].Cells[0];
                Assert(CopyValues(grid).SequenceEqual([original.Rules[0].LocalPath])
                    && grid.GetClipboardContent()?.GetText(TextDataFormat.UnicodeText) == original.Rules[0].LocalPath,
                    "copying a local directory returns only its full Unicode path");
                grid.CurrentCell.Value = Path.Combine(original.Rules[0].LocalPath, "进度 1.sav");
                grid.Rows[0].Cells[2].Value = true;
                Assert(CopyValues(grid).SequenceEqual([(string)grid.CurrentCell.Value!, original.Rules[0].LocalPath]),
                    "a single-file rule offers its file path and containing directory separately");
                grid.CurrentCell.Value = @"C:\discarded";
                Descendants(editor).OfType<CheckBox>().First().Checked = true;
                Descendants(editor).OfType<Button>().Single(b => b.Text == "取消").PerformClick();
            });
            Assert(File.ReadAllBytes(config).SequenceEqual(beforeCancel) && ((GameProfile)gameList.SelectedItem!).Rules[0].LocalPath == original.Rules[0].LocalPath,
                "canceling an edit preserves both saved configuration and in-memory synchronization rules");
            InEditor(form, "编辑设置", editor => {
                var tabs = Descendants(editor).OfType<TabControl>().Single();
                Assert(tabs.SelectedTab?.Text == "存档与 Steam", "existing save rules open directly on the storage tab");
                tabs.SelectedIndex = 0;
                var fields = Descendants(editor).OfType<TableLayoutPanel>().Single(p => p.ColumnCount == 3 && p.RowCount == 8);
                ((TextBox)fields.GetControlFromPosition(1, 0)!).Text = "模拟游戏 · 已保存修改";
                ((TextBox)fields.GetControlFromPosition(1, 2)!).Text = Path.GetDirectoryName(mock)!;
                ((TextBox)fields.GetControlFromPosition(1, 3)!).Text = "saved arguments";
                ((ComboBox)fields.GetControlFromPosition(1, 4)!).SelectedIndex = 1;
                ((TextBox)fields.GetControlFromPosition(1, 5)!).Text = @"C:\Tools\loader.exe";
                ((TextBox)fields.GetControlFromPosition(1, 6)!).Text = "480";
                ((TextBox)fields.GetControlFromPosition(1, 7)!).Text = "76561198000000000";
                tabs.SelectedIndex = 1;
                var grid = Descendants(editor).OfType<DataGridView>().Single();
                grid.Rows[0].Cells[3].Value = "*.sav";
                foreach (var option in Descendants(editor).OfType<CheckBox>()) option.Checked = true;
                grid.CurrentCell = grid.Rows[0].Cells[1];
                Assert(grid.Enabled && grid.ReadOnly && !grid.BeginEdit(false)
                    && Descendants(editor).OfType<Button>().Where(b => b.Text is "保存并查找存档" or "手动添加目录" or "手动添加文件" or "移除选中规则").All(b => !b.Enabled),
                    "Steam cloud mode retains selectable rules while disabling directory editing and discovery");
                Assert(CopyValues(grid).SequenceEqual([original.Rules[0].SharedPath])
                    && grid.GetClipboardContent()?.GetText(TextDataFormat.UnicodeText) == original.Rules[0].SharedPath,
                    "read-only cloud rules can copy the target path without headers, tabs or adjacent cells");
                tabs.SelectedIndex = 2;
                var recording = Descendants(editor).OfType<TableLayoutPanel>().Single(p => p.ColumnCount == 3 && p.RowCount == 3);
                ((TextBox)recording.GetControlFromPosition(1, 1)!).Text = @"D:\Game Videos";
                ((TextBox)recording.GetControlFromPosition(1, 2)!).Text = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
                Descendants(recording).OfType<ComboBox>().Single().SelectedItem = "MP4";
                Capture(editor, "editor-recording-preview.png");
                tabs.SelectedIndex = 0; Capture(editor, "editor-launch-preview.png");
                tabs.SelectedIndex = 1; Capture(editor, "editor-preview.png");
                editor.Size = editor.MinimumSize; Capture(editor, "editor-minimum-preview.png");
                var overflow = Descendants(editor).OfType<Button>().Where(b => b.Visible && b.Parent is FlowLayoutPanel && (b.Right > b.Parent.ClientSize.Width || b.Bottom > b.Parent.ClientSize.Height)).ToList();
                Assert(overflow.Count == 0, "editor actions fit the minimum supported window size: " + string.Join(", ", overflow.Select(b => b.Text + " " + b.Bounds + " in " + b.Parent!.ClientSize)));
                Assert(grid.ClientSize.Height >= grid.ColumnHeadersHeight + grid.Rows[0].Height, "editor retains a full synchronization rule row at minimum size");
                Descendants(editor).OfType<Button>().Single(b => b.Text == "保存").PerformClick();
            });
            var saved = Storage.Read<List<GameProfile>>(config);
            Assert(saved[0].Id == original.Id && saved[0].Rules[0].Id == original.Rules[0].Id && saved[0].Name == "模拟游戏 · 已保存修改"
                && saved[0].Executable == mock && saved[0].WorkingDirectory == Path.GetDirectoryName(mock) && saved[0].Arguments == "saved arguments"
                && saved[0].UseColdClient && saved[0].LoaderSource == @"C:\Tools\loader.exe" && saved[0].AppId == "480" && saved[0].SteamId == "76561198000000000"
                && saved[0].SyncAchievements && saved[0].ReportSteamPlaytime && saved[0].UseSteamCloud && saved[0].RecordGame && saved[0].RecordingDirectory == @"D:\Game Videos"
                && saved[0].RecordingFormat == "mp4"
                && saved[0].ObsExecutable == @"C:\Program Files\obs-studio\bin\64bit\obs64.exe" && saved[0].Rules[0].Patterns == "*.sav",
                "saving an edit persists all per-game settings and preserves profile and rule identities");
            Assert(((GameProfile)gameList.SelectedItem!).Name == saved[0].Name && !saved[1].SyncAchievements && !saved[1].ReportSteamPlaytime,
                "library refreshes the edited entry without changing another application's settings");
            gameList.SelectedIndex = 1;
            Assert(stateFields.Single(t => t.AccessibleName == "存档同步").Text == "尚无目录规则", "selecting a different game refreshes its feature values");
            gameList.SelectedIndex = 0;
            Assert(stateFields.Single(t => t.AccessibleName == "OBS 录制").Text.Contains("MP4") && stateFields.Single(t => t.AccessibleName == "存档同步").Text == "Steam 云存档",
                "library shows the selected game's recording format and cloud mode");
            InEditor(form, "编辑设置", editor => {
                Assert(Descendants(editor).OfType<TextBox>().Any(t => t.Text == "saved arguments") && Descendants(editor).OfType<CheckBox>().All(c => c.Checked)
                    && Descendants(editor).OfType<TextBox>().Any(t => t.Text == saved[0].ObsExecutable),
                    "reopening an editor restores saved launch settings and Steam options");
                Assert(Descendants(editor).OfType<ComboBox>().Any(c => c.Text == "MP4"), "reopening the game settings restores its recording format");
                Descendants(editor).OfType<Button>().Single(b => b.Text == "取消").PerformClick();
            });
            InEditor(form, "添加应用", editor => {
                Assert(!editor.Profile.SyncAchievements && !editor.Profile.ReportSteamPlaytime && !editor.Profile.UseSteamCloud && !editor.Profile.RecordGame, "new entries keep Steam automation, cloud synchronization and recording opt-in");
                Assert(Descendants(editor).OfType<ComboBox>().Any(c => c.Text == "MKV"), "new game recordings default to MKV");
                Descendants(editor).OfType<Button>().Single(b => b.Text == "取消").PerformClick();
            });
            Assert(Storage.Read<List<GameProfile>>(config).Count == 2, "canceling a new application does not create an entry");
            InEditor(form, "添加应用", editor => {
                var fields = Descendants(editor).OfType<TableLayoutPanel>().Single(p => p.ColumnCount == 3 && p.RowCount == 8);
                ((TextBox)fields.GetControlFromPosition(1, 0)!).Text = "新添加的应用";
                ((TextBox)fields.GetControlFromPosition(1, 1)!).Text = mock;
                Descendants(editor).OfType<Button>().Single(b => b.Text == "保存").PerformClick();
            });
            Assert(gameList.Items.Count == 3 && ((GameProfile)gameList.SelectedItem!).Name == "新添加的应用", "saving a new entry adds and selects it in the library");
            form.Close();
        }
        using (var reopened = new MainForm(uiRoot))
        {
            reopened.Show(); Application.DoEvents();
            var list = Descendants(reopened).OfType<ListBox>().Single();
            Assert(list.Items.Count == 3 && list.Items.Cast<GameProfile>().Any(p => p.Name == "模拟游戏 · 已保存修改"), "saved applications survive application restart");
            reopened.Close();
        }
        using (var empty = new MainForm(Path.Combine(root, "empty")))
        {
            empty.Show(); Application.DoEvents();
            Assert(Descendants(empty).OfType<Button>().Where(b => new[] { "同步并启动", "仅同步", "仅启动", "编辑设置", "移除条目" }.Contains(b.Text)).All(b => !b.Enabled)
                && Descendants(empty).OfType<Button>().Single(b => b.Text == "添加应用").Enabled, "empty library disables entry actions and keeps adding available");
            Capture(empty, "empty-library-preview.png"); empty.Close();
        }
        var sample = new DiscoveryResult { Files = [new(@"C:\Users\玩家\AppData\Roaming\游戏\saves\slot1.sav", 1, true, true, false)] };
        using var candidate = new CandidateForm(sample);
        candidate.Show(); Application.DoEvents();
        var checklist = Descendants(candidate).OfType<CheckedListBox>().Single();
        Assert(candidate.Selected.Count == 0, "discovered directories are never selected without user approval");
        checklist.SelectedIndex = 0;
        var candidateDetails = Descendants(candidate).OfType<TextBox>().Single(t => t.AccessibleName == "候选详情");
        Assert(checklist.Items[0]!.Equals(Path.GetDirectoryName(sample.Files[0].Path)) && candidateDetails.Text.Contains("slot1.sav")
            && CopyValues(checklist).SequenceEqual([(string)checklist.Items[0]!, checklist.Items[0] + Environment.NewLine + candidateDetails.Text]) && candidate.Selected.Count == 0,
            "a candidate exposes its raw folder separately from the details without checking it");
        checklist.SetItemChecked(0, true);
        Assert(candidate.Selected.Count == 1 && candidate.Selected[0].Patterns == "*.sav", "user selection carries the approved directory and file filter");
        Capture(candidate, "candidates-preview.png"); candidate.Close();
        var conflictProfile = Storage.Read<List<GameProfile>>(config)[0];
        var conflicts = new SyncPlan { Items = [new SyncItem { RuleId = conflictProfile.Rules[0].Id, RelativePath = "slot1.sav", LocalHash = "a", SharedHash = "b", Action = "Conflict" },
            new SyncItem { RuleId = conflictProfile.Rules[0].Id, RelativePath = "slot2.sav", LocalHash = "c", SharedHash = "d", Action = "Conflict" }] };
        using var conflict = new ConflictForm(conflictProfile, conflicts);
        conflict.Show(); Application.DoEvents();
        var conflictGrid = Descendants(conflict).OfType<DataGridView>().Single();
        conflictGrid.CurrentCell = conflictGrid.Rows[0].Cells[1];
        Assert(CopyValues(conflictGrid).SequenceEqual([Path.Combine(conflictProfile.Rules[0].SharedPath, "slot1.sav"), conflictProfile.Rules[0].SharedPath]),
            "directory conflicts offer the target file and its containing directory");
        Assert((string?)conflictGrid.Rows[0].Cells[2].Value == "请选择", "conflicts have no preselected winning side");
        Capture(conflict, "conflicts-preview.png");
        conflictGrid.Rows[0].Cells[2].Value = "本地"; conflictGrid.Rows[1].Cells[2].Value = "同步目标";
        Descendants(conflict).OfType<Button>().Single(b => b.Text == "确认同步").PerformClick();
        Assert(conflicts.Items[0].Action == "Local" && conflicts.Items[1].Action == "Shared", "conflict choices retain their original synchronization direction");
        conflict.Close();
        var cloudFiles = new Dictionary<string, CloudSaveFile> { [conflictProfile.Rules[0].Id] = new("%GameInstall%Saves/slot.sav", @"C:\fixture\slot.sav", null, uint.MaxValue) };
        conflicts.Items[0].Action = "Conflict";
        using var cloudConflict = new ConflictForm(conflictProfile, conflicts, cloudFiles);
        cloudConflict.Show(); Application.DoEvents();
        var cloudGrid = Descendants(cloudConflict).OfType<DataGridView>().Single();
        cloudGrid.CurrentCell = cloudGrid.Rows[0].Cells[1];
        Assert(cloudGrid.Columns[1].HeaderText == "Steam 云端文件" && CopyValues(cloudGrid).SequenceEqual([cloudFiles[conflictProfile.Rules[0].Id].Name]),
            "cloud conflicts copy the remote file name without offering a Windows directory");
        cloudConflict.Close();
        TestImportUi(Path.Combine(root, "import-cancel"), mock, "cancel");
        TestImportUi(Path.Combine(root, "import-save"), mock, "save");
    }
}
