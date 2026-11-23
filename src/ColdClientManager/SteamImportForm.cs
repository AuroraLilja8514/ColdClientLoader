namespace ColdClientManager;

public sealed class SteamImportForm : Form
{
    readonly TextBox game = new() { Dock = DockStyle.Fill, PlaceholderText = @"Steam 库\steamapps\common\游戏名" };
    readonly TextBox target = new() { Dock = DockStyle.Fill, PlaceholderText = "留空时在本机保存独立副本" };
    readonly CheckBox online = new() { Text = "从 Steam 读取 Windows 启动选项和已知存档位置", Checked = true, AutoSize = true };
    public string InstallDirectory => game.Text.Trim();
    public string? TargetUser => string.IsNullOrWhiteSpace(target.Text) ? null : SyncEngine.Normalize(target.Text.Trim());
    public bool ReadSteamInfo => online.Checked;

    public SteamImportForm(string? initialGame, string? targetUser)
    {
        Text = "从 Steam 导入"; Font = new Font("Microsoft YaHei UI", 9F); StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(690, 235); MinimumSize = Size;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 3, RowCount = 5 };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 108)); layout.ColumnStyles.Add(new(SizeType.Percent, 100)); layout.ColumnStyles.Add(new(SizeType.Absolute, 82));
        foreach (int height in new[] { 40, 40, 45, 35 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        void Folder(int row, string label, TextBox field)
        {
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            layout.Controls.Add(field, 1, row);
            var pick = new Button { Text = "选择…", AutoSize = true };
            pick.Click += (_, _) => {
                using var dialog = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = label, SelectedPath = field.Text };
                if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.SelectedPath;
            };
            layout.Controls.Add(pick, 2, row);
        }
        game.Text = initialGame ?? ""; target.Text = targetUser ?? "";
        Folder(0, "游戏安装目录", game); Folder(1, "目标用户目录", target);
        var hint = new Label { Dock = DockStyle.Fill, Text = @"可选。选择目标 Windows 用户的主目录（例如 F:\Users\玩家），用户目录内的存档会映射到相同位置。所有目标路径都可在下一步修改。" };
        layout.Controls.Add(hint, 0, 2); layout.SetColumnSpan(hint, 3);
        layout.Controls.Add(online, 0, 3); layout.SetColumnSpan(online, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var next = new Button { Text = "读取游戏", AutoSize = true };
        next.Click += (_, _) => {
            if (!Directory.Exists(InstallDirectory)) { MessageBox.Show(this, "请选择已安装的游戏目录。"); return; }
            if (target.Text.Trim().Length > 0 && !Path.IsPathFullyQualified(target.Text.Trim())) { MessageBox.Show(this, "请填写目标用户目录的完整路径。"); return; }
            if (string.Equals(TargetUser, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this, "目标用户目录与当前用户目录相同。要在本机保存独立副本，请清空此栏。"); return; }
            DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(cancel); buttons.Controls.Add(next);
        layout.Controls.Add(buttons, 0, 4); layout.SetColumnSpan(buttons, 3); Controls.Add(layout); AcceptButton = next; CancelButton = cancel;
    }

    public static SteamLaunchOption ChooseLaunch(IWin32Window owner, string folder, List<SteamLaunchOption> options)
    {
        if (options.Count == 1) return options[0];
        if (options.Count == 0)
        {
            using var pick = new OpenFileDialog { Title = "选择实际显示游戏画面的 Windows 程序", InitialDirectory = folder, Filter = "游戏程序 (*.exe)|*.exe" };
            if (pick.ShowDialog(owner) != DialogResult.OK) throw new OperationCanceledException();
            string executable = SteamCloudSync.Under(folder, Path.GetRelativePath(folder, pick.FileName));
            SyncEngine.RejectLinks(executable);
            return new(executable, folder, "");
        }
        using var dialog = new Form { Text = "选择实际显示游戏画面的启动项", Font = new Font("Microsoft YaHei UI", 9F),
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(760, 300) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 40));
        var list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        foreach (var item in options)
            list.Items.Add($"{Path.GetRelativePath(folder, item.Executable)}  {item.Arguments}    （运行目录 {Path.GetRelativePath(folder, item.Directory)}）");
        CopyMenu.ForList(list, ("复制程序路径", i => options[i].Executable), ("复制运行目录", i => options[i].Directory),
            ("复制启动参数", i => options[i].Arguments));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var confirm = new Button { Text = "使用此启动项", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
        list.SelectedIndexChanged += (_, _) => confirm.Enabled = list.SelectedIndex >= 0;
        buttons.Controls.Add(cancel); buttons.Controls.Add(confirm); layout.Controls.Add(list, 0, 0); layout.Controls.Add(buttons, 0, 1);
        dialog.Controls.Add(layout); dialog.AcceptButton = confirm; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(owner) != DialogResult.OK) throw new OperationCanceledException();
        return options[list.SelectedIndex];
    }
}
