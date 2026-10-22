using ColdClientManager;

internal static partial class Tests
{
    static void TestLibraryUi(string root, string mock)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
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
            Rules = [new SyncRule { LocalPath = @"C:\Users\玩家\AppData\Roaming\游戏\saves", SharedPath = @"E:\GameSaves\游戏\saves", Patterns = "*.sav;*.dat" }] };
        Storage.Write(config, new[] { original, new GameProfile { Name = "第二个配置" } });
        using (var form = new MainForm(uiRoot))
        {
            form.Show(); Application.DoEvents();
            var gameList = Descendants(form).OfType<ListBox>().Single();
            Assert(gameList.Items.Count == 2 && gameList.SelectedItem is GameProfile p && p.Id == original.Id, "home page loads previously saved applications");
            Assert(Descendants(form).OfType<TextBox>().All(t => t.ReadOnly) && !Descendants(form).OfType<DataGridView>().Any(),
                "home page shows the application library without editable configuration fields");
            Assert(new[] { "同步并启动", "仅同步", "仅启动", "编辑设置", "添加应用" }.All(text => Descendants(form).OfType<Button>().Any(b => b.Text == text && b.Enabled)),
                "saved applications expose all three actions plus edit and add");
            Capture(form, "ui-preview.png");
            form.Size = form.MinimumSize; Capture(form, "ui-minimum-preview.png");
            Assert(Descendants(form).OfType<Button>().Where(b => b.Parent is FlowLayoutPanel).All(b => b.Right <= b.Parent!.ClientSize.Width && b.Bottom <= b.Parent.ClientSize.Height),
                "library actions fit the minimum supported window size");
            var summary = Descendants(form).OfType<Label>().Single(l => l.Text.Contains("Steam 成就同步："));
            Assert(summary.GetPreferredSize(new Size(summary.ClientSize.Width, 0)).Height <= summary.ClientSize.Height,
                "selected application's saved settings remain visible at minimum window size");
            byte[] beforeCancel = File.ReadAllBytes(config);
            InEditor(form, "编辑设置", editor => {
                var name = Descendants(editor).OfType<TextBox>().Single(t => t.Text == original.Name);
                name.Text = "discarded edit";
                Descendants(editor).OfType<DataGridView>().Single().Rows[0].Cells[0].Value = @"C:\discarded";
                Descendants(editor).OfType<CheckBox>().First().Checked = true;
                Descendants(editor).OfType<Button>().Single(b => b.Text == "取消").PerformClick();
            });
            Assert(File.ReadAllBytes(config).SequenceEqual(beforeCancel) && ((GameProfile)gameList.SelectedItem!).Rules[0].LocalPath == original.Rules[0].LocalPath,
                "canceling an edit preserves both saved configuration and in-memory synchronization rules");
            InEditor(form, "编辑设置", editor => {
                var fields = Descendants(editor).OfType<TableLayoutPanel>().Single(p => p.ColumnCount == 3 && p.RowCount == 8);
                ((TextBox)fields.GetControlFromPosition(1, 0)!).Text = "模拟游戏 · 已保存修改";
                ((TextBox)fields.GetControlFromPosition(1, 2)!).Text = Path.GetDirectoryName(mock)!;
                ((TextBox)fields.GetControlFromPosition(1, 3)!).Text = "saved arguments";
                ((ComboBox)fields.GetControlFromPosition(1, 4)!).SelectedIndex = 1;
                ((TextBox)fields.GetControlFromPosition(1, 5)!).Text = @"C:\Tools\loader.exe";
                ((TextBox)fields.GetControlFromPosition(1, 6)!).Text = "480";
                ((TextBox)fields.GetControlFromPosition(1, 7)!).Text = "76561198000000000";
                foreach (var option in Descendants(editor).OfType<CheckBox>()) option.Checked = true;
                var recording = Descendants(editor).OfType<TableLayoutPanel>().Single(p => p.ColumnCount == 3 && p.RowCount == 3);
                ((TextBox)recording.GetControlFromPosition(1, 1)!).Text = @"D:\Game Videos";
                ((TextBox)recording.GetControlFromPosition(1, 2)!).Text = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
                Descendants(recording).OfType<ComboBox>().Single().SelectedItem = "MP4";
                Descendants(editor).OfType<DataGridView>().Single().Rows[0].Cells[3].Value = "*.sav";
                Capture(editor, "editor-preview.png");
                editor.Size = editor.MinimumSize; Capture(editor, "editor-minimum-preview.png");
                var overflow = Descendants(editor).OfType<Button>().Where(b => b.Parent is FlowLayoutPanel && (b.Right > b.Parent.ClientSize.Width || b.Bottom > b.Parent.ClientSize.Height)).ToList();
                Assert(overflow.Count == 0, "editor actions fit the minimum supported window size: " + string.Join(", ", overflow.Select(b => b.Text + " " + b.Bounds + " in " + b.Parent!.ClientSize)));
                var grid = Descendants(editor).OfType<DataGridView>().Single();
                Assert(grid.ClientSize.Height >= grid.ColumnHeadersHeight + grid.Rows[0].Height, "editor retains a full synchronization rule row at minimum size");
                Descendants(editor).OfType<Button>().Single(b => b.Text == "保存").PerformClick();
            });
            var saved = Storage.Read<List<GameProfile>>(config);
            Assert(saved[0].Id == original.Id && saved[0].Rules[0].Id == original.Rules[0].Id && saved[0].Name == "模拟游戏 · 已保存修改"
                && saved[0].Executable == mock && saved[0].WorkingDirectory == Path.GetDirectoryName(mock) && saved[0].Arguments == "saved arguments"
                && saved[0].UseColdClient && saved[0].LoaderSource == @"C:\Tools\loader.exe" && saved[0].AppId == "480" && saved[0].SteamId == "76561198000000000"
                && saved[0].SyncAchievements && saved[0].ReportSteamPlaytime && saved[0].RecordGame && saved[0].RecordingDirectory == @"D:\Game Videos"
                && saved[0].RecordingFormat == "mp4"
                && saved[0].ObsExecutable == @"C:\Program Files\obs-studio\bin\64bit\obs64.exe" && saved[0].Rules[0].Patterns == "*.sav",
                "saving an edit persists all per-game settings and preserves profile and rule identities");
            Assert(((GameProfile)gameList.SelectedItem!).Name == saved[0].Name && !saved[1].SyncAchievements && !saved[1].ReportSteamPlaytime,
                "library refreshes the edited entry without changing another application's settings");
            gameList.SelectedIndex = 1; gameList.SelectedIndex = 0;
            InEditor(form, "编辑设置", editor => {
                Assert(Descendants(editor).OfType<TextBox>().Any(t => t.Text == "saved arguments") && Descendants(editor).OfType<CheckBox>().All(c => c.Checked)
                    && Descendants(editor).OfType<TextBox>().Any(t => t.Text == saved[0].ObsExecutable),
                    "reopening an editor restores saved launch settings and Steam options");
                Assert(Descendants(editor).OfType<ComboBox>().Any(c => c.Text == "MP4"), "reopening the game settings restores its recording format");
                Descendants(editor).OfType<Button>().Single(b => b.Text == "取消").PerformClick();
            });
            InEditor(form, "添加应用", editor => {
                Assert(!editor.Profile.SyncAchievements && !editor.Profile.ReportSteamPlaytime && !editor.Profile.RecordGame, "new entries keep Steam automation and recording opt-in");
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
        checklist.SetItemChecked(0, true);
        Assert(candidate.Selected.Count == 1 && candidate.Selected[0].Patterns == "*.sav", "user selection carries the approved directory and file filter");
        Capture(candidate, "candidates-preview.png"); candidate.Close();
        var conflictProfile = Storage.Read<List<GameProfile>>(config)[0];
        var conflicts = new SyncPlan { Items = [new SyncItem { RuleId = conflictProfile.Rules[0].Id, RelativePath = "slot1.sav", LocalHash = "a", SharedHash = "b", Action = "Conflict" },
            new SyncItem { RuleId = conflictProfile.Rules[0].Id, RelativePath = "slot2.sav", LocalHash = "c", SharedHash = "d", Action = "Conflict" }] };
        using var conflict = new ConflictForm(conflictProfile, conflicts);
        conflict.Show(); Application.DoEvents();
        var conflictGrid = Descendants(conflict).OfType<DataGridView>().Single();
        Assert((string?)conflictGrid.Rows[0].Cells[2].Value == "请选择", "conflicts have no preselected winning side");
        Capture(conflict, "conflicts-preview.png");
        conflictGrid.Rows[0].Cells[2].Value = "本地"; conflictGrid.Rows[1].Cells[2].Value = "同步目标";
        Descendants(conflict).OfType<Button>().Single(b => b.Text == "确认同步").PerformClick();
        Assert(conflicts.Items[0].Action == "Local" && conflicts.Items[1].Action == "Shared", "conflict choices retain their original synchronization direction");
        conflict.Close();
    }
}
