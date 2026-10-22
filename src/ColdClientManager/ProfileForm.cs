using System.ComponentModel;
using System.Text.Json;

namespace ColdClientManager;

public sealed class ProfileForm : Form
{
    readonly TextBox name = new(), executable = new(), working = new(), arguments = new(), loader = new(), appId = new(), steamId = new();
    readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox syncAchievements = new() { Text = "游玩前后同步 Steam 成就", AutoSize = true, Margin = new Padding(4, 8, 16, 0) };
    readonly CheckBox reportSteamPlaytime = new() { Text = "上报 Steam 状态与时长", AutoSize = true, Margin = new Padding(4, 8, 0, 0) };
    readonly CheckBox recordGame = new() { Text = "启动时用 OBS 录制（720p / 30 帧）", AutoSize = true };
    readonly ComboBox recordingFormat = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
    readonly TextBox recordingDirectory = new() { PlaceholderText = "留空使用用户目录下 Videos\\ColdClientManager" };
    readonly TextBox obsExecutable = new() { PlaceholderText = "选择已安装的 obs64.exe；留空使用本应用的专用便携版" };
    readonly DataGridView rules = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = Color.White, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
    public GameProfile Profile { get; }
    public bool DiscoverAfterSave { get; private set; }

    public ProfileForm(GameProfile? profile)
    {
        Profile = profile == null ? new GameProfile() : JsonSerializer.Deserialize<GameProfile>(JsonSerializer.Serialize(profile))!;
        Text = profile == null ? "添加应用" : "编辑应用 · " + profile.Name;
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1100, 970); MinimumSize = new Size(940, 930);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 8 };
        foreach (var row in new[] { new RowStyle(SizeType.Absolute, 42), new RowStyle(SizeType.Absolute, 320), new RowStyle(SizeType.Absolute, 42),
            new RowStyle(SizeType.Percent, 100), new RowStyle(SizeType.Absolute, 48), new RowStyle(SizeType.Absolute, 42), new RowStyle(SizeType.Absolute, 122), new RowStyle(SizeType.Absolute, 52) }) layout.RowStyles.Add(row);
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "保存后即可在主页面直接同步或启动。路径、启动参数和 Steam 选项会按应用分别保存。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 8 };
        settings.ColumnStyles.Add(new(SizeType.Absolute, 132)); settings.ColumnStyles.Add(new(SizeType.Percent, 100)); settings.ColumnStyles.Add(new(SizeType.Absolute, 90));
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
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
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
        layout.Controls.Add(settings, 0, 1);
        layout.Controls.Add(new Label { Text = "同步规则：填写本地存档与同步目标的对应目录（或文件）。只有下表选定的内容会同步。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "本地存档路径", DataPropertyName = "LocalPath", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 42 });
        rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "同步目标路径", DataPropertyName = "SharedPath", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 42 });
        rules.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "单文件", DataPropertyName = "SingleFile", Width = 82 });
        rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "文件筛选", DataPropertyName = "Patterns", Width = 125 });
        layout.Controls.Add(rules, 0, 3);
        var ruleButtons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        ruleButtons.Controls.Add(Button("手动添加目录", () => AddRule(false)));
        ruleButtons.Controls.Add(Button("手动添加文件", () => AddRule(true)));
        ruleButtons.Controls.Add(Button("移除选中规则", () => {
            if (rules.CurrentRow?.DataBoundItem is SyncRule rule) { Profile.Rules.Remove(rule); BindRules(); }
        }));
        layout.Controls.Add(ruleButtons, 0, 4);
        var steamOptions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        steamOptions.Controls.Add(syncAchievements); steamOptions.Controls.Add(reportSteamPlaytime);
        layout.Controls.Add(steamOptions, 0, 5);
        var recordingOptions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
        recordingOptions.ColumnStyles.Add(new(SizeType.Absolute, 132)); recordingOptions.ColumnStyles.Add(new(SizeType.Percent, 100)); recordingOptions.ColumnStyles.Add(new(SizeType.Absolute, 90));
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
        layout.Controls.Add(recordingOptions, 0, 6);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.AutoSize));
        var discovery = Button("保存并查找存档", () => Save(true)); discovery.Dock = DockStyle.None; footer.Controls.Add(discovery, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = Button("保存", () => Save(false));
        var cancel = new Button { Text = "取消", AutoSize = true, MinimumSize = new Size(90, 32), DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel); buttons.Controls.Add(save);
        footer.Controls.Add(buttons, 1, 0); layout.Controls.Add(footer, 0, 7);
        AcceptButton = save; CancelButton = cancel;
        name.Text = Profile.Name; executable.Text = Profile.Executable; working.Text = Profile.WorkingDirectory; arguments.Text = Profile.Arguments;
        loader.Text = Profile.LoaderSource; appId.Text = Profile.AppId; steamId.Text = Profile.SteamId; mode.SelectedIndex = Profile.UseColdClient ? 1 : 0;
        syncAchievements.Checked = Profile.SyncAchievements; reportSteamPlaytime.Checked = Profile.ReportSteamPlaytime;
        recordGame.Checked = Profile.RecordGame; recordingDirectory.Text = Profile.RecordingDirectory;
        recordingFormat.SelectedItem = Profile.RecordingFormat.ToUpperInvariant();
        obsExecutable.Text = Profile.ObsExecutable;
        BindRules();
    }

    Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(90, 32), Dock = DockStyle.Fill };
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
        Profile.RecordGame = recordGame.Checked; Profile.RecordingDirectory = recordingDirectory.Text.Trim();
        Profile.RecordingFormat = recordingFormat.Text.ToLowerInvariant();
        Profile.ObsExecutable = obsExecutable.Text.Trim();
        DiscoverAfterSave = discover; DialogResult = DialogResult.OK;
    }
}
