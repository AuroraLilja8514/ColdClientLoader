using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ColdClientManager;

public sealed class MainForm : Form
{
    readonly string dataRoot;
    readonly List<GameProfile> profiles;
    readonly ComboBox games = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly TextBox name = new(), executable = new(), working = new(), arguments = new(), loader = new(), appId = new(), steamId = new();
    readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly DataGridView rules = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = Color.White, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.FromArgb(247, 249, 251) };
    readonly Label status = new() { Text = "就绪", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4, 7, 4, 0) };
    readonly List<Control> editors = [];
    bool busy, loading;
    GameProfile? editing;
    GameProfile? Current => games.SelectedItem as GameProfile;

    public MainForm(string dataRoot)
    {
        this.dataRoot = dataRoot;
        Text = "游戏存档同步 · ColdClientManager";
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1160, 860); MinimumSize = new Size(940, 760);
        string config = Path.Combine(dataRoot, "profiles.json");
        profiles = File.Exists(config) ? Storage.Read<List<GameProfile>>(config) : [];
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 8 };
        foreach (var row in new[] { new RowStyle(SizeType.Absolute, 44), new RowStyle(SizeType.Absolute, 264), new RowStyle(SizeType.Absolute, 42),
            new RowStyle(SizeType.Percent, 100), new RowStyle(SizeType.Absolute, 40), new RowStyle(SizeType.Absolute, 38), new RowStyle(SizeType.Absolute, 108), new RowStyle(SizeType.Absolute, 28) }) layout.RowStyles.Add(row);
        Controls.Add(layout);
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        top.RowStyles.Add(new(SizeType.Percent, 100));
        top.ColumnStyles.Add(new(SizeType.Percent, 100));
        top.ColumnStyles.Add(new(SizeType.Absolute, 96)); top.ColumnStyles.Add(new(SizeType.Absolute, 96)); top.ColumnStyles.Add(new(SizeType.Absolute, 96));
        top.Controls.Add(games, 0, 0);
        top.Controls.Add(Button("添加游戏", () => { SaveCurrent(); profiles.Add(new GameProfile()); ReloadGames(profiles[^1]); }), 1, 0);
        top.Controls.Add(Button("保存设置", SaveCurrent), 2, 0);
        top.Controls.Add(Button("移除配置", () => {
            if (Current == null) return;
            if (MessageBox.Show(this, "只移除游戏配置，存档和备份会保留。", "移除配置", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            profiles.Remove(Current); Persist(); ReloadGames(profiles.FirstOrDefault());
        }), 3, 0);
        layout.Controls.Add(top, 0, 0);
        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 8 };
        settings.ColumnStyles.Add(new(SizeType.Absolute, 132)); settings.ColumnStyles.Add(new(SizeType.Percent, 100)); settings.ColumnStyles.Add(new(SizeType.Absolute, 90));
        for (int i = 0; i < 8; i++) settings.RowStyles.Add(new(SizeType.Percent, 12.5F));
        void Field(int row, string label, Control input, Action? browse = null)
        {
            settings.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            input.Dock = DockStyle.Fill; settings.Controls.Add(input, 1, row); editors.Add(input);
            if (browse != null) settings.Controls.Add(Button("选择…", browse), 2, row);
        }
        Field(0, "游戏名称", name);
        Field(1, "游戏启动程序", executable, () => {
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
        layout.Controls.Add(new Label { Text = "同步规则：两侧填写互相对应的存档目录（或同一个存档文件）。只会同步下表选定的内容。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "本系统存档路径", DataPropertyName = "LocalPath", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 42 });
        rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "共享 / 另一系统对应路径", DataPropertyName = "SharedPath", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 42 });
        rules.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "单文件", DataPropertyName = "SingleFile", Width = 82 });
        rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "文件筛选", DataPropertyName = "Patterns", Width = 125 });
        layout.Controls.Add(rules, 0, 3);
        var ruleButtons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        ruleButtons.Controls.Add(Button("手动添加目录", () => AddRule(false)));
        ruleButtons.Controls.Add(Button("手动添加文件", () => AddRule(true)));
        ruleButtons.Controls.Add(Button("移除选中规则", () => { if (rules.CurrentRow?.DataBoundItem is SyncRule rule) { Current!.Rules.Remove(rule); BindRules(); } }));
        ruleButtons.Controls.Add(Button("恢复启动器环境", () => { LoaderRegistry.Recover(dataRoot); Log("已恢复上次启动前的 Steam 注册表值。"); }));
        layout.Controls.Add(ruleButtons, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        actions.Controls.Add(AsyncButton("查找存档并启动", () => LaunchAsync(true)));
        actions.Controls.Add(AsyncButton("立即同步", () => SynchronizeAsync(true)));
        actions.Controls.Add(AsyncButton("同步并游玩", () => LaunchAsync(false)));
        actions.Controls.Add(Button("打开备份目录", () => {
            if (Current == null) return;
            string folder = Storage.ProfileFolder(dataRoot, Current); Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }));
        layout.Controls.Add(actions, 0, 5); layout.Controls.Add(log, 0, 6); layout.Controls.Add(status, 0, 7);
        games.SelectedIndexChanged += (_, _) => { if (!loading) { SaveCurrent(); LoadCurrent(); } };
        FormClosing += (_, e) => {
            if (busy) { e.Cancel = true; MessageBox.Show(this, "请等游戏及子进程退出、同步完成后再关闭应用。", "会话仍在运行"); }
            else { try { SaveCurrent(); } catch (Exception ex) { e.Cancel = true; ShowError(ex); } }
        };
        ReloadGames(profiles.FirstOrDefault());
        Log("首次先查找存档，在游戏中产生一次进度并正常退出，然后确认候选目录。默认不传播删除；覆盖前会备份。新版存档可能来自任意一侧，冲突时需要你选择。");
        if (File.Exists(Path.Combine(dataRoot, "loader-registry-recovery.json"))) Log("检测到上次启动的恢复记录，请先点击“恢复启动器环境”。");
    }

    Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 30, MinimumSize = new Size(84, 28), Dock = DockStyle.Fill };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { ShowError(ex); } };
        editors.Add(button); return button;
    }
    Button AsyncButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 30, MinimumSize = new Size(110, 28) };
        button.Click += async (_, _) => {
            SetBusy(true);
            try { await action(); }
            catch (OperationCanceledException) { Log("操作已取消。"); }
            catch (Exception ex) { ShowError(ex); }
            finally { SetBusy(false); }
        };
        editors.Add(button); return button;
    }
    void SetBusy(bool value)
    {
        busy = value; foreach (var control in editors) control.Enabled = !value;
        games.Enabled = rules.Enabled = !value;
        status.Text = value ? "正在处理；游玩期间请保留此窗口。" : "就绪";
    }
    void ShowError(Exception ex) { Log("未完成：" + ex.Message); MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    void Log(string message) => log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    void PickFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog { ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
    }
    void ReloadGames(GameProfile? selected)
    {
        loading = true; games.DataSource = null; games.DataSource = profiles.ToList();
        if (selected != null) games.SelectedItem = selected;
        loading = false; LoadCurrent();
    }
    void LoadCurrent()
    {
        if (loading) return;
        var p = Current;
        editing = p;
        name.Text = p?.Name ?? ""; executable.Text = p?.Executable ?? ""; working.Text = p?.WorkingDirectory ?? "";
        arguments.Text = p?.Arguments ?? ""; mode.SelectedIndex = p?.UseColdClient == true ? 1 : 0;
        loader.Text = p?.LoaderSource ?? ""; appId.Text = p?.AppId ?? ""; steamId.Text = p?.SteamId ?? "";
        BindRules();
    }
    void BindRules() => rules.DataSource = new BindingList<SyncRule>(Current?.Rules ?? []);
    void Persist() => Storage.Write(Path.Combine(dataRoot, "profiles.json"), profiles);
    void SaveCurrent()
    {
        if (editing is not { } p) return;
        rules.EndEdit();
        p.Name = name.Text.Trim(); p.Executable = executable.Text.Trim(); p.WorkingDirectory = working.Text.Trim();
        p.Arguments = arguments.Text; p.UseColdClient = mode.SelectedIndex == 1; p.LoaderSource = loader.Text.Trim();
        p.AppId = appId.Text.Trim(); p.SteamId = steamId.Text.Trim();
        Persist(); ReloadGames(Current);
    }
    void AddRule(bool file)
    {
        if (Current == null) throw new InvalidOperationException("请先添加游戏。");
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
        Current.Rules.Add(new SyncRule { LocalPath = path, SingleFile = file }); BindRules();
    }
    bool AskElevation() => MessageBox.Show(this, "选定的存档目录拒绝访问。是否通过 UAC 只为本次同步请求管理员权限？游戏仍以普通权限启动，应用不会修改目录所有权或权限。", "目录访问权限", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    async Task SynchronizeAsync(bool showCompletion)
    {
        SaveCurrent(); var p = Current ?? throw new InvalidOperationException("请先添加游戏。");
        if (p.Rules.Count == 0) { if (showCompletion) Log("尚未选择同步目录。"); return; }
        SyncPlan plan; bool elevated = false;
        try { plan = await Task.Run(() => SyncEngine.Plan(p, dataRoot)); }
        catch (UnauthorizedAccessException)
        {
            if (!AskElevation()) throw new OperationCanceledException();
            plan = (await Program.ElevatedSync(p, dataRoot, null)).Plan!; elevated = true;
        }
        if (plan.Items.Any(i => i.Action == "Conflict"))
        {
            using var dialog = new ConflictForm(p, plan);
            if (dialog.ShowDialog(this) != DialogResult.OK) throw new OperationCanceledException("尚未解决存档冲突。");
        }
        string backup;
        try { backup = elevated ? (await Program.ElevatedSync(p, dataRoot, plan)).Backup! : await Task.Run(() => SyncEngine.Apply(p, dataRoot, plan)); }
        catch (UnauthorizedAccessException)
        {
            if (!AskElevation()) throw new OperationCanceledException();
            // A prior partial copy invalidates the plan; rebuilding it avoids applying stale choices.
            var fresh = (await Program.ElevatedSync(p, dataRoot, null)).Plan!;
            if (fresh.Items.Any(i => i.Action == "Conflict"))
            {
                using var dialog = new ConflictForm(p, fresh);
                if (dialog.ShowDialog(this) != DialogResult.OK) throw new OperationCanceledException();
            }
            backup = (await Program.ElevatedSync(p, dataRoot, fresh)).Backup!;
        }
        Log($"同步完成：处理 {plan.Changes} 项；覆盖文件的备份保存在游戏配置目录中。{(Directory.Exists(backup) ? " 备份：" + backup : "")}");
    }

    async Task LaunchAsync(bool discover)
    {
        SaveCurrent(); var p = Current ?? throw new InvalidOperationException("请先添加游戏。");
        p.Executable = SyncEngine.Normalize(p.Executable);
        p.WorkingDirectory = string.IsNullOrWhiteSpace(p.WorkingDirectory) ? Path.GetDirectoryName(p.Executable)! : SyncEngine.Normalize(p.WorkingDirectory);
        executable.Text = p.Executable; working.Text = p.WorkingDirectory;
        if (!File.Exists(p.Executable)) throw new FileNotFoundException("找不到游戏启动程序。", p.Executable);
        if (GetCurrentPackageFullName(ref packageBufferLength, IntPtr.Zero) != 15700)
            throw new IOException("当前继承了打包应用的目录虚拟化环境。请从资源管理器打开本应用，再启动真实游戏，避免存档被写进宿主应用的私有目录。");
        if (!discover && p.Rules.Count == 0 && MessageBox.Show(this, "尚未选择同步目录，本次仅启动游戏。", "没有同步规则", MessageBoxButtons.OKCancel) != DialogResult.OK)
            throw new OperationCanceledException();
        await SynchronizeAsync(false);
        DiscoveryCapture? capture = null; LoaderRegistry? registry = null; DiscoveryResult? result = null;
        int exitCode = 0; bool exited = false;
        var errors = new List<Exception>();
        try
        {
            if (discover)
            {
                Log("正在启动文件采集器，需要一次 UAC。进入游戏后产生一次存档，再正常退出。");
                capture = await DiscoveryCapture.StartAsync(dataRoot);
            }
            string launch = p.Executable, args = p.Arguments, directory = p.WorkingDirectory;
            if (p.UseColdClient)
            {
                launch = await Task.Run(() => ColdClient.Prepare(p, dataRoot)); args = ""; directory = Path.GetDirectoryName(launch)!;
                registry = new LoaderRegistry(dataRoot);
            }
            using var game = new GameProcess(launch, args, directory);
            if (capture != null) await capture.AttachAsync(game.Id);
            game.Resume();
            Log($"已启动 {p.Name}。正在等待整个进程树退出…");
            exitCode = await game.WaitAsync();
            exited = true;
            Log($"全部游戏进程已退出，启动程序退出码：{exitCode}。");
        }
        catch (Exception ex) { errors.Add(ex); }
        finally
        {
            if (capture != null)
            {
                try { result = await capture.FinishAsync(); }
                catch (Exception ex) { errors.Add(ex); }
                try { await capture.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            }
            if (registry != null) { try { registry.Dispose(); } catch (Exception ex) { errors.Add(ex); } }
        }
        if (exited) { try { await SynchronizeAsync(false); } catch (Exception ex) { errors.Add(ex); } }
        if (result != null)
        {
            Log($"采集完成：{result.Processes.Count} 个进程，{result.Files.Count} 个创建 / 写入路径，丢失事件 {result.EventsLost}。报告：{capture!.Folder}");
            using var dialog = new CandidateForm(result);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                foreach (var candidate in dialog.Selected)
                    if (!p.Rules.Any(r => r.LocalPath.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase)))
                        p.Rules.Add(new SyncRule { LocalPath = candidate.Path, Patterns = candidate.Patterns });
                BindRules(); Persist(); Log("已加入候选规则。请填写各规则对应的共享路径，再点击“立即同步”检查首次差异。");
            }
        }
        if (errors.Count > 0) throw new AggregateException(string.Join(Environment.NewLine, errors.Select(e => e.Message)), errors);
    }
    static uint packageBufferLength;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);
}
