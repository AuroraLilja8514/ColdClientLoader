using System.ComponentModel;

namespace ColdClientManager;

internal enum SettingsPage { Launch, Saves, Recording }

public sealed class ProfileForm : Form
{
    readonly TabControl tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 6), AccessibleName = "应用设置" };
    internal void SelectPage(SettingsPage page) => tabs.SelectedIndex = (int)page;
    readonly TextBox name = new(), executable = new(), working = new(), arguments = new(), loader = new(), appId = new(), steamId = new();
    readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox syncAchievements = new() { Text = "自动同步 Steam 成就（含实时）", AutoSize = true, Margin = new Padding(4, 8, 16, 0) };
    readonly CheckBox reportSteamPlaytime = new() { Text = "上报 Steam 状态与时长", AutoSize = true, Margin = new Padding(4, 8, 0, 0) };
    readonly CheckBox useSteamCloud = new() { Text = "使用 Steam 云存档", AutoSize = true, Margin = new Padding(16, 8, 0, 0) };
    readonly CheckBox recordGame = new() { Text = "启动时用 OBS 录制（720p / 30 帧）", AutoSize = true };
    readonly ComboBox recordingFormat = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
    readonly TextBox recordingDirectory = new() { PlaceholderText = "留空使用用户目录下 Videos\\ColdClientManager" };
    readonly TextBox obsExecutable = new() { PlaceholderText = "选择已安装的 obs64.exe；留空使用本应用的专用便携版" };
    readonly DataGridView rules = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false, BackgroundColor = Color.White, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
    public GameProfile Profile { get; }
    public bool DiscoverAfterSave { get; private set; }
    readonly bool importing;

    public ProfileForm(GameProfile? profile, string? importNote = null)
    {
        importing = importNote != null;
        Profile = profile?.Copy() ?? new GameProfile();
        Text = importing ? "确认导入 · " + Profile.Name : profile == null ? "添加应用" : "编辑应用 · " + profile.Name;
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(980, 640); MinimumSize = new Size(780, 540);
        BackColor = DesktopUi.Canvas; ForeColor = DesktopUi.Ink;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new(SizeType.Absolute, importing ? 52 : 0)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 48));
        Controls.Add(layout);
        if (importNote != null)
            layout.Controls.Add(new TextBox { Text = importNote, AccessibleName = "导入提示", Dock = DockStyle.Fill, Multiline = true, BorderStyle = BorderStyle.None,
                ReadOnly = true, BackColor = DesktopUi.Canvas, ForeColor = DesktopUi.Ink, ScrollBars = ScrollBars.Vertical }, 0, 0);
        var launchTab = new TabPage("启动设置") { Padding = new Padding(12), BackColor = DesktopUi.Surface, AutoScroll = true };
        var savesTab = new TabPage("存档与 Steam") { Padding = new Padding(10), BackColor = DesktopUi.Surface };
        var recordTab = new TabPage("OBS 录制") { Padding = new Padding(12), BackColor = DesktopUi.Surface, AutoScroll = true };
        tabs.TabPages.AddRange([launchTab, savesTab, recordTab]); layout.Controls.Add(tabs, 0, 1);
        var saveLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        saveLayout.RowStyles.Add(new(SizeType.AutoSize)); saveLayout.RowStyles.Add(new(SizeType.Absolute, 60));
        saveLayout.RowStyles.Add(new(SizeType.Percent, 100)); saveLayout.RowStyles.Add(new(SizeType.Absolute, 48));
        savesTab.Controls.Add(saveLayout);
        var settings = new TableLayoutPanel { Dock = DockStyle.Top, Height = Math.Max(36, Font.Height + 14) * 8, ColumnCount = 3, RowCount = 8 };
        settings.ColumnStyles.Add(new(SizeType.Absolute, 132)); settings.ColumnStyles.Add(new(SizeType.Percent, 100)); settings.ColumnStyles.Add(new(SizeType.Absolute, 100));
        for (int i = 0; i < 8; i++) settings.RowStyles.Add(new(SizeType.Percent, 12.5F));
        void Field(int row, string label, Control input, Action? browse = null)
        {
            settings.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            input.Dock = DockStyle.Fill; settings.Controls.Add(input, 1, row);
            if (browse != null) settings.Controls.Add(Button("选择…", browse), 2, row);
        }
        Field(0, "应用名称", name);
        Field(1, "应用启动程序", executable, () => {
            using var dialog = new OpenFileDialog { Filter = "程序 (*.exe)|*.exe", CheckFileExists = true };
            if (importing) dialog.InitialDirectory = SteamCloudSync.InstallDirectory(Profile);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (importing)
            {
                string path = SteamCloudSync.Under(dialog.InitialDirectory, Path.GetRelativePath(dialog.InitialDirectory, dialog.FileName));
                SyncEngine.RejectLinks(path); executable.Text = path; return;
            }
            executable.Text = dialog.FileName; working.Text = Path.GetDirectoryName(dialog.FileName)!;
            var metadata = new GameProfile { Executable = dialog.FileName }; ColdClient.ReadSteamManifest(metadata);
            if (metadata.AppId.Length > 0) appId.Text = metadata.AppId;
            if (metadata.SteamId.Length > 0) steamId.Text = metadata.SteamId;
            name.Text = metadata.Name == "新游戏" ? Path.GetFileNameWithoutExtension(dialog.FileName) : metadata.Name;
        });
        Field(2, "运行目录", working, () => PickFolder(working));
        Field(3, "启动参数", arguments);
        mode.Items.AddRange(["直接启动", "ColdClientLoader"]); Field(4, "启动方式", mode);
        Field(5, "启动器程序", loader, () => {
            using var dialog = new OpenFileDialog { Filter = "ColdClientLoader (*.exe)|*.exe" };
            if (dialog.ShowDialog(this) == DialogResult.OK) loader.Text = dialog.FileName;
        });
        Field(6, "Steam AppID", appId); Field(7, "存档账号 ID", steamId);
        if (importing)
        {
            executable.ReadOnly = appId.ReadOnly = true;
            steamId.ReadOnly = ulong.TryParse(Profile.SteamId, out ulong account) && account != 0;
        }
        launchTab.Controls.Add(settings);
        const string ruleHint = "同步规则：填写本地存档与同步目标的对应目录（或文件）。只有下表选定的内容会同步。单击路径后按 Ctrl+C 可复制。";
        var ruleDescription = new Label { Text = ruleHint, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        saveLayout.Controls.Add(ruleDescription, 0, 1);
        rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "本地存档路径", DataPropertyName = "LocalPath", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 42 });
        rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "同步目标路径", DataPropertyName = "SharedPath", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 42 });
        rules.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "单文件", DataPropertyName = "SingleFile", Width = 82 });
        rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "文件筛选", DataPropertyName = "Patterns", Width = 125 });
        rules.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "含子目录", DataPropertyName = "Recursive", Width = 78 });
        CopyMenu.ForPaths(rules, cell => cell.OwningColumn!.DataPropertyName is "LocalPath" or "SharedPath"
            && cell.OwningRow!.DataBoundItem is SyncRule { SingleFile: true });
        DesktopUi.StyleGrid(rules); saveLayout.Controls.Add(rules, 0, 2);
        var ruleButtons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        ruleButtons.Controls.Add(Button("手动添加目录", () => AddRule(false)));
        ruleButtons.Controls.Add(Button("手动添加文件", () => AddRule(true)));
        ruleButtons.Controls.Add(Button("移除选中规则", () => {
            if (rules.CurrentRow?.DataBoundItem is SyncRule rule) { Profile.Rules.Remove(rule); BindRules(); }
        }));
        saveLayout.Controls.Add(ruleButtons, 0, 3);
        var steamOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        steamOptions.Controls.Add(useSteamCloud); steamOptions.Controls.Add(syncAchievements); steamOptions.Controls.Add(reportSteamPlaytime);
        useSteamCloud.CheckedChanged += (_, _) => {
            rules.ReadOnly = useSteamCloud.Checked;
            ruleButtons.Enabled = !useSteamCloud.Checked;
            ruleDescription.Text = useSteamCloud.Checked ? "Steam 云存档：按该游戏的 Windows 云存档规则同步，启动前和退出后自动处理。下方目录规则暂不使用，仍可单击路径后按 Ctrl+C 复制。" : ruleHint;
        };
        saveLayout.Controls.Add(steamOptions, 0, 0);
        var recordingOptions = new TableLayoutPanel { Dock = DockStyle.Top, Height = 126, ColumnCount = 3, RowCount = 3 };
        recordingOptions.ColumnStyles.Add(new(SizeType.Absolute, 132)); recordingOptions.ColumnStyles.Add(new(SizeType.Percent, 100)); recordingOptions.ColumnStyles.Add(new(SizeType.Absolute, 100));
        recordingOptions.RowStyles.Add(new(SizeType.Absolute, 36)); recordingOptions.RowStyles.Add(new(SizeType.Absolute, 40));
        recordingOptions.RowStyles.Add(new(SizeType.Absolute, 40));
        var recordingHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        recordingHeader.Controls.Add(recordGame);
        recordingHeader.Controls.Add(new Label { Text = "格式", AutoSize = true, Margin = new Padding(16, 4, 4, 0) });
        recordingFormat.Items.AddRange(["MKV", "MP4"]); recordingHeader.Controls.Add(recordingFormat);
        recordingOptions.Controls.Add(recordingHeader, 0, 0); recordingOptions.SetColumnSpan(recordingHeader, 3);
        recordingOptions.Controls.Add(new Label { Text = "录像输出目录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        recordingDirectory.Dock = DockStyle.Fill; recordingOptions.Controls.Add(recordingDirectory, 1, 1);
        recordingOptions.Controls.Add(Button("选择…", () => PickFolder(recordingDirectory)), 2, 1);
        recordingOptions.Controls.Add(new Label { Text = "OBS 程序", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        obsExecutable.Dock = DockStyle.Fill; recordingOptions.Controls.Add(obsExecutable, 1, 2);
        recordingOptions.Controls.Add(Button("选择…", () => {
            using var dialog = new OpenFileDialog { Filter = "OBS Studio (obs64.exe)|obs64.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) obsExecutable.Text = dialog.FileName;
        }), 2, 2);
        recordTab.Controls.Add(recordingOptions);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.AutoSize));
        var discovery = Button("保存并查找存档", () => Save(true)); discovery.Anchor = AnchorStyles.Left; footer.Controls.Add(discovery, 0, 0);
        useSteamCloud.CheckedChanged += (_, _) => discovery.Enabled = !useSteamCloud.Checked;
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        var save = Button(importing ? "完成导入" : "保存", () => Save(false));
        var cancel = new Button { Text = "取消", AutoSize = true, MinimumSize = new Size(90, 32), DialogResult = DialogResult.Cancel };
        DesktopUi.StyleButton(save, true); DesktopUi.StyleButton(cancel); DesktopUi.StyleButton(discovery);
        buttons.Controls.Add(cancel); buttons.Controls.Add(save);
        footer.Controls.Add(buttons, 1, 0); layout.Controls.Add(footer, 0, 2);
        AcceptButton = importing && Profile.Rules.Count == 0 && !Profile.UseSteamCloud ? discovery : save; CancelButton = cancel;
        name.Text = Profile.Name; executable.Text = Profile.Executable; working.Text = Profile.WorkingDirectory; arguments.Text = Profile.Arguments;
        loader.Text = Profile.LoaderSource; appId.Text = Profile.AppId; steamId.Text = Profile.SteamId; mode.SelectedIndex = Profile.UseColdClient ? 1 : 0;
        syncAchievements.Checked = Profile.SyncAchievements; reportSteamPlaytime.Checked = Profile.ReportSteamPlaytime;
        useSteamCloud.Checked = Profile.UseSteamCloud;
        recordGame.Checked = Profile.RecordGame; recordingDirectory.Text = Profile.RecordingDirectory;
        recordingFormat.SelectedItem = Profile.RecordingFormat.ToUpperInvariant();
        obsExecutable.Text = Profile.ObsExecutable;
        BindRules();
        if (importing || Profile.Rules.Count > 0 || Profile.UseSteamCloud) tabs.SelectedTab = savesTab;
    }

    Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(90, 32), Anchor = AnchorStyles.Left | AnchorStyles.Right };
        button.Click += (_, _) => {
            try { action(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        return button;
    }
    void PickFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog { ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
    }
    void BindRules() => rules.DataSource = new BindingList<SyncRule>(Profile.Rules);
    void AddRule(bool file)
    {
        string path;
        if (file)
        {
            using var dialog = new OpenFileDialog { Filter = "所有文件|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return; path = dialog.FileName;
        }
        else
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog(this) != DialogResult.OK) return; path = dialog.SelectedPath;
        }
        Profile.Rules.Add(new SyncRule { LocalPath = path, SingleFile = file }); BindRules();
    }
    void Save(bool discover)
    {
        if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidOperationException("请填写应用名称。");
        rules.EndEdit();
        Profile.Name = name.Text.Trim(); Profile.Executable = executable.Text.Trim(); Profile.WorkingDirectory = working.Text.Trim();
        Profile.Arguments = arguments.Text; Profile.UseColdClient = mode.SelectedIndex == 1; Profile.LoaderSource = loader.Text.Trim();
        Profile.AppId = appId.Text.Trim(); Profile.SteamId = steamId.Text.Trim();
        Profile.SyncAchievements = syncAchievements.Checked; Profile.ReportSteamPlaytime = reportSteamPlaytime.Checked;
        Profile.UseSteamCloud = useSteamCloud.Checked;
        Profile.RecordGame = recordGame.Checked; Profile.RecordingDirectory = recordingDirectory.Text.Trim();
        Profile.RecordingFormat = recordingFormat.Text.ToLowerInvariant();
        Profile.ObsExecutable = obsExecutable.Text.Trim();
        if (importing)
        {
            SteamGameImport.ValidateLaunch(Profile);
            if (!Profile.UseSteamCloud) SyncEngine.Validate(Profile, checkLinks: false);
        }
        DiscoverAfterSave = discover; DialogResult = DialogResult.OK;
    }
}
