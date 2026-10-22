namespace ColdClientManager;

public sealed class CandidateForm : Form
{
    readonly CheckedListBox list = new() { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
    readonly List<SaveCandidate> candidates;
    public List<SaveCandidate> Selected => list.CheckedIndices.Cast<int>().Select(i => candidates[i]).ToList();
    public CandidateForm(DiscoveryResult result)
    {
        Text = "确认需要同步的存档目录"; Size = new Size(1000, 500); StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        candidates = CandidateFinder.Find(result);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 80)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 40));
        string warning = result.EventsLost > 0 ? $"检测到 {result.EventsLost} 个事件丢失，结果可能不完整。 " : "";
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = warning + "请勾选确认的存档目录。未勾选的路径不会同步。缓存、日志等已过滤，但文件写入无法单独证明它是存档。\n内存映射写入、进程树外的 Steam / 服务代写，以及仅重命名的最终路径可能漏报，可手动补充。", AutoSize = false }, 0, 0);
        foreach (var c in candidates) list.Items.Add($"{c.Path}    [{c.Reason}]    {c.FileCount} 个文件：{c.Examples}", false);
        layout.Controls.Add(list, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "加入选中目录", AutoSize = true, DialogResult = DialogResult.OK };
        buttons.Controls.Add(ok); buttons.Controls.Add(new Button { Text = "暂不添加", AutoSize = true, DialogResult = DialogResult.Cancel });
        layout.Controls.Add(buttons, 0, 2); Controls.Add(layout); AcceptButton = ok;
    }
}

public sealed class ConflictForm : Form
{
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, RowHeadersVisible = false };
    readonly List<SyncItem> conflicts;
    public ConflictForm(GameProfile profile, SyncPlan plan)
    {
        conflicts = plan.Items.Where(i => i.Action == "Conflict").ToList();
        Text = "两侧存档冲突 · 请选择保留哪一侧"; Size = new Size(1000, 470); StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 48)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 40));
        layout.Controls.Add(new Label { Text = "两边内容不同，且无法确定哪份应保留。选择后的内容会复制到另一侧；被覆盖的文件会先备份。取消将停止本次启动 / 同步。", Dock = DockStyle.Fill }, 0, 0);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "本地文件", Name = "local", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "同步目标文件", Name = "shared", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        grid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "保留内容", Name = "choice", Width = 145, DataSource = new[] { "请选择", "本地", "同步目标" } });
        foreach (var item in conflicts)
        {
            var rule = profile.Rules.Single(r => r.Id == item.RuleId);
            grid.Rows.Add(rule.SingleFile ? rule.LocalPath : Path.Combine(rule.LocalPath, item.RelativePath),
                rule.SingleFile ? rule.SharedPath : Path.Combine(rule.SharedPath, item.RelativePath), "请选择");
        }
        layout.Controls.Add(grid, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "确认同步", AutoSize = true };
        ok.Click += (_, _) => {
            grid.EndEdit();
            if (grid.Rows.Cast<DataGridViewRow>().Any(r => (string?)r.Cells[2].Value == "请选择")) { MessageBox.Show(this, "请为每个冲突选择保留的一侧。"); return; }
            for (int i = 0; i < conflicts.Count; i++) conflicts[i].Action = (string?)grid.Rows[i].Cells[2].Value == "本地" ? "Local" : "Shared";
            DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(ok); buttons.Controls.Add(new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel });
        layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
    }
}
