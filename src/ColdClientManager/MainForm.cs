using System.Runtime.InteropServices;
using System.Globalization;

namespace ColdClientManager;

public sealed partial class MainForm : Form
{
    readonly string dataRoot;
    readonly List<GameProfile> profiles;
    readonly ListBox games = new() { AccessibleName = "已保存的应用", Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None,
        DrawMode = DrawMode.OwnerDrawFixed, BackColor = DesktopUi.Side, ForeColor = DesktopUi.OnSide, Font = new Font("Microsoft YaHei UI", 10F) };
    readonly Label listTitle = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = DesktopUi.SideMuted };
    readonly TextBox selectedName = new() { AccessibleName = "应用名称", ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = DesktopUi.Surface, ForeColor = DesktopUi.Ink,
        Anchor = AnchorStyles.Left | AnchorStyles.Right, Font = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold) };
    readonly TextBox selectedProgram = new() { AccessibleName = "启动程序路径", ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = DesktopUi.Surface,
        Anchor = AnchorStyles.Left | AnchorStyles.Right, ForeColor = DesktopUi.Muted };
    static readonly (string Title, string Icon, Func<GameProfile, (bool Active, string Value)> State, SettingsPage Page)[] features = [
        ("存档同步", "\uE895", p => (p.UseSteamCloud || p.Rules.Count > 0, p.UseSteamCloud ? "Steam 云存档" : p.Rules.Count == 0 ? "尚无目录规则" : $"{p.Rules.Count} 条存档规则"), SettingsPage.Saves),
        ("Steam 成就", "\uE734", p => (p.SyncAchievements, p.SyncAchievements ? "已开启" : "未开启"), SettingsPage.Saves),
        ("状态与时长", "\uE823", p => (p.ReportSteamPlaytime, p.ReportSteamPlaytime ? "已开启" : "未开启"), SettingsPage.Saves),
        ("OBS 录制", "\uE714", p => (p.RecordGame, p.RecordGame ? $"720p / 30 帧 · {p.RecordingFormat.ToUpperInvariant()}" : "未开启"), SettingsPage.Recording)];
    readonly TextBox[] featureValues = new TextBox[features.Length];
    readonly TableLayoutPanel featureGrid = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 2 };
    readonly Label guidance = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = DesktopUi.Muted };
    readonly TextBox log = new() { AccessibleName = "活动记录", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = DesktopUi.Surface, ForeColor = DesktopUi.Ink, BorderStyle = BorderStyle.FixedSingle };
    readonly Label status = new() { Text = "就绪", AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = DesktopUi.Accent };
    readonly List<Control> editors = [], profileActions = [];
    bool busy;
    CancellationTokenSource? cancelOperation;
    bool waitingForSteam;
    GameProfile? Current => games.SelectedItem as GameProfile;

    public MainForm(string dataRoot, string? importDirectory = null)
    {
        this.dataRoot = dataRoot;
        Text = "我的应用 · ColdClientManager";
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = DesktopUi.Canvas; ForeColor = DesktopUi.Ink;
        Size = new Size(1100, 760); MinimumSize = new Size(900, 696);
        string config = Path.Combine(dataRoot, "profiles.json");
        profiles = File.Exists(config) ? Storage.Read<List<GameProfile>>(config) : [];
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 10, 16, 12), ColumnCount = 1, RowCount = 5 };
        foreach (var row in new[] { new RowStyle(SizeType.Absolute, 66), new RowStyle(SizeType.Percent, 100), new RowStyle(SizeType.Absolute, 48),
            new RowStyle(SizeType.Absolute, 29), new RowStyle(SizeType.Absolute, 78) }) layout.RowStyles.Add(row);
        Controls.Add(layout);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        header.ColumnStyles.Add(new(SizeType.Absolute, 50)); header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.AutoSize)); header.ColumnStyles.Add(new(SizeType.AutoSize));
        var libraryIcon = DesktopUi.Icon("\uE7FC", 20); libraryIcon.ForeColor = DesktopUi.Accent;
        header.Controls.Add(libraryIcon, 0, 0);
        header.Controls.Add(new Label { Text = "我的应用", Font = new Font(Font.FontFamily, 18F, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 1, 0);
        var add = AsyncButton("添加应用", () => EditProfileAsync(null)); add.Anchor = AnchorStyles.Right;
        var import = AsyncButton("从 Steam 导入", () => {
            string? initial = importDirectory; importDirectory = null;
            return ImportGameAsync(initial);
        });
        import.Anchor = AnchorStyles.Right;
        DesktopUi.StyleButton(import, true, "\uE8B5"); DesktopUi.StyleButton(add, glyph: "\uE710");
        header.Controls.Add(import, 2, 0); header.Controls.Add(add, 3, 0); layout.Controls.Add(header, 0, 0);
        if (importDirectory != null) Shown += (_, _) => import.PerformClick();
        var library = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        library.ColumnStyles.Add(new(SizeType.Percent, 29)); library.ColumnStyles.Add(new(SizeType.Percent, 71));
        var listPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = DesktopUi.Side, Padding = new Padding(10), Margin = new Padding(0, 0, 10, 0) };
        listPanel.RowStyles.Add(new(SizeType.Absolute, 36)); listPanel.RowStyles.Add(new(SizeType.Percent, 100));
        listPanel.Controls.Add(listTitle, 0, 0);
        games.ItemHeight = Math.Max(54, games.Font.Height + Font.Height + 12);
        games.DrawItem += DrawGame;
        CopyMenu.ForList(games, ("复制应用名称", i => ((GameProfile)games.Items[i]!).Name),
            ("复制程序路径", i => ((GameProfile)games.Items[i]!).Executable),
            ("复制备份目录路径", i => Storage.ProfileFolder(dataRoot, (GameProfile)games.Items[i]!)),
            ("复制设置摘要", i => {
                var p = (GameProfile)games.Items[i]!;
                return string.Join(Environment.NewLine, features.Select(f => f.Title + "：" + f.State(p).Value).Prepend(p.UseColdClient ? "ColdClientLoader" : "直接启动"));
            }));
        listPanel.Controls.Add(games, 0, 1); library.Controls.Add(listPanel, 0, 0);
        var details = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1, BackColor = DesktopUi.Surface, Padding = new Padding(14), Margin = Padding.Empty };
        foreach (var row in new[] { new RowStyle(SizeType.Absolute, 46), new RowStyle(SizeType.Absolute, 31), new RowStyle(SizeType.Absolute, 48),
            new RowStyle(SizeType.Absolute, 34), new RowStyle(SizeType.Percent, 100), new RowStyle(SizeType.Absolute, 44) }) details.RowStyles.Add(row);
        details.Controls.Add(selectedName, 0, 0); details.Controls.Add(selectedProgram, 0, 1);
        details.Controls.Add(guidance, 0, 3);
        featureGrid.ColumnStyles.Add(new(SizeType.Percent, 50)); featureGrid.ColumnStyles.Add(new(SizeType.Percent, 50));
        featureGrid.RowStyles.Add(new(SizeType.Percent, 50)); featureGrid.RowStyles.Add(new(SizeType.Percent, 50));
        for (int i = 0; i < features.Length; i++)
        {
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = DesktopUi.Canvas, Margin = new Padding(3, 3, 5, 5) };
            card.ColumnStyles.Add(new(SizeType.Absolute, 46)); card.ColumnStyles.Add(new(SizeType.Percent, 100));
            card.RowStyles.Add(new(SizeType.Percent, 50)); card.RowStyles.Add(new(SizeType.Percent, 50));
            var feature = features[i];
            var open = AsyncButton(feature.Title + "  ›", () => EditProfileAsync(Current!, feature.Page));
            open.AccessibleName = "编辑" + feature.Title + "设置"; open.FlatStyle = FlatStyle.Flat; open.FlatAppearance.BorderSize = 0;
            open.Dock = DockStyle.Fill; open.Margin = Padding.Empty; open.MinimumSize = Size.Empty; open.AutoSize = false;
            open.TextAlign = ContentAlignment.BottomLeft; open.ForeColor = DesktopUi.Muted; open.Cursor = Cursors.Hand;
            profileActions.Add(open);
            var icon = DesktopUi.Icon(feature.Icon, 15); icon.Cursor = card.Cursor = Cursors.Hand;
            icon.Click += (_, _) => open.PerformClick(); card.Click += (_, _) => open.PerformClick();
            card.Controls.Add(icon, 0, 0); card.SetRowSpan(icon, 2);
            card.Controls.Add(open, 1, 0);
            var value = new TextBox { AccessibleName = features[i].Title, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Top,
                BackColor = DesktopUi.Canvas, ForeColor = DesktopUi.Ink, Margin = new Padding(3, 0, 3, 0) };
            featureValues[i] = value; card.Controls.Add(value, 1, 1); featureGrid.Controls.Add(card, i % 2, i / 2);
        }
        details.Controls.Add(featureGrid, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        var play = AsyncButton("同步并启动", () => LaunchAsync(false, true));
        DesktopUi.StyleButton(play, true, "\uE768");
        actions.Controls.Add(play);
        var sync = AsyncButton("仅同步", () => SynchronizeAsync(true)); DesktopUi.StyleButton(sync, glyph: "\uE895");
        var start = AsyncButton("仅启动", () => LaunchAsync(false, false)); DesktopUi.StyleButton(start, glyph: "\uE7FC");
        actions.Controls.Add(sync); actions.Controls.Add(start);
        profileActions.AddRange(actions.Controls.Cast<Control>());
        details.Controls.Add(actions, 0, 2);
        var manage = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        manage.Controls.Add(AsyncButton("编辑设置", () => EditProfileAsync(Current!)));
        manage.Controls.Add(Button("打开备份目录", () => {
            string folder = Storage.ProfileFolder(dataRoot, Current!); Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }));
        manage.Controls.Add(Button("移除条目", () => {
            if (MessageBox.Show(this, "只移除应用条目，存档和备份会保留。", "移除条目", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            profiles.Remove(Current!); Persist(); ReloadGames(profiles.FirstOrDefault());
        }));
        foreach (Button button in manage.Controls) DesktopUi.StyleButton(button);
        profileActions.AddRange(manage.Controls.Cast<Control>()); details.Controls.Add(manage, 0, 5);
        library.Controls.Add(details, 1, 0); layout.Controls.Add(library, 0, 1);
        var steamActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        steamActions.Controls.Add(AsyncButton("Steam 登录", async () => {
            await using var session = SteamLoginForm.SignIn(this, dataRoot);
            Log($"Steam 登录成功：{session.AccountName}；账号 ID：{session.SteamId}。下次自动复用加密令牌。");
        }));
        var achievements = AsyncButton("同步 Steam 成就", SynchronizeAchievementsAsync);
        steamActions.Controls.Add(achievements); profileActions.Add(achievements);
        steamActions.Controls.Add(Button("退出 Steam 账号", () => {
            File.Delete(SteamSession.LoginPath(dataRoot)); Log("已移除本机保存的 Steam 登录令牌。");
        }));
        steamActions.Controls.Add(Button("恢复启动器环境", () => { LoaderRegistry.Recover(dataRoot); Log("已恢复上次启动前的 Steam 注册表值。"); }));
        foreach (Button button in steamActions.Controls) DesktopUi.StyleButton(button);
        layout.Controls.Add(steamActions, 0, 2);
        var logTitle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        logTitle.ColumnStyles.Add(new(SizeType.Percent, 100)); logTitle.ColumnStyles.Add(new(SizeType.AutoSize));
        logTitle.Controls.Add(new Label { Text = "活动记录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        logTitle.Controls.Add(status, 1, 0); layout.Controls.Add(logTitle, 0, 3); layout.Controls.Add(log, 0, 4);
        games.SelectedIndexChanged += (_, _) => UpdateSelection();
        FormClosing += (_, e) => {
            if (!busy) return;
            e.Cancel = true;
            if (waitingForSteam && cancelOperation != null)
            {
                if (MessageBox.Show(this, "正在等待 Steam 完成本次操作。要取消等待并关闭管理器吗？本地进度会保留。", "正在等待 Steam",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) cancelOperation.Cancel();
            }
            else MessageBox.Show(this, "请等游戏及子进程退出、同步完成后再关闭应用。", "会话仍在运行");
        };
        ReloadGames(profiles.FirstOrDefault());
        Log("选择已保存的应用即可同步或启动。Steam 游戏可点击“从 Steam 导入”，其他程序可点击“添加应用”。");
        if (File.Exists(Path.Combine(dataRoot, "loader-registry-recovery.json"))) Log("检测到上次启动的恢复记录，请先点击“恢复启动器环境”。");
    }

    void DrawGame(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var profile = (GameProfile)games.Items[e.Index]!;
        bool selected = e.State.HasFlag(DrawItemState.Selected);
        Color back = selected ? DesktopUi.SideSelection : DesktopUi.Side;
        Color fore = selected && SystemInformation.HighContrast ? SystemColors.HighlightText : DesktopUi.OnSide;
        using var background = new SolidBrush(back); e.Graphics.FillRectangle(background, e.Bounds);
        int size = e.Bounds.Height - 14, left = e.Bounds.Left + 6;
        var tile = new Rectangle(left, e.Bounds.Top + 7, size, size);
        using var fill = new SolidBrush(selected ? DesktopUi.Accent : DesktopUi.SideSelection); e.Graphics.FillRectangle(fill, tile);
        const TextFormatFlags text = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(e.Graphics, StringInfo.GetNextTextElement(profile.Name), games.Font, tile, DesktopUi.OnAccent, text | TextFormatFlags.HorizontalCenter);
        left = tile.Right + 8;
        var title = new Rectangle(left, e.Bounds.Top + 4, Math.Max(0, e.Bounds.Right - left - 4), games.Font.Height + 2);
        TextRenderer.DrawText(e.Graphics, profile.Name, games.Font, title, fore, text);
        var mode = new Rectangle(left, title.Bottom, title.Width, Font.Height + 2);
        TextRenderer.DrawText(e.Graphics, profile.UseColdClient ? "ColdClientLoader" : "直接启动", Font, mode, SystemInformation.HighContrast ? fore : DesktopUi.SideMuted, text);
        if (e.State.HasFlag(DrawItemState.Focus)) ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, fore, back);
    }

    Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(100, 34) };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { ShowError(ex); } };
        editors.Add(button); return button;
    }
    Button AsyncButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(110, 36) };
        button.Click += async (_, _) => {
            using var cancel = new CancellationTokenSource();
            cancelOperation = cancel;
            SetBusy(true);
            try { await action(); }
            catch (OperationCanceledException) { Log("操作已取消。"); }
            catch (Exception ex) { ShowError(ex); }
            finally { cancelOperation = null; SetBusy(false); if (cancel.IsCancellationRequested) Close(); }
        };
        editors.Add(button); return button;
    }
    void SetBusy(bool value)
    {
        busy = value; foreach (var control in editors) control.Enabled = !value;
        games.Enabled = !value; UpdateSelection();
        status.Text = value ? "正在处理；游玩期间请保留此窗口。" : "就绪";
    }
    void ShowError(Exception ex) { Log("未完成：" + ex.Message); MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    void Log(string message) => log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    async Task<SteamSession> OpenSteamAsync()
    {
        var session = File.Exists(SteamSession.LoginPath(dataRoot)) ? await SteamSession.ConnectAsync(dataRoot) : SteamLoginForm.SignIn(this, dataRoot);
        session.ConnectionProgress = new Progress<string>(Log);
        return session;
    }
    async Task WaitForSteamAsync(Func<CancellationToken, Task> action)
    {
        waitingForSteam = true;
        var token = cancelOperation?.Token ?? CancellationToken.None;
        try { await action(token); token.ThrowIfCancellationRequested(); }
        finally { waitingForSteam = false; }
    }
    void ReloadGames(GameProfile? selected)
    {
        games.DataSource = null; games.DataSource = profiles.ToList();
        listTitle.Text = $"已保存的应用   {profiles.Count}";
        if (selected != null) games.SelectedItem = selected;
        UpdateSelection();
    }
    void UpdateSelection()
    {
        foreach (var control in profileActions)
        {
            control.Visible = Current != null;
            control.Enabled = !busy && Current != null;
        }
        featureGrid.Visible = Current != null;
        if (Current is not { } profile)
        {
            selectedName.Text = "还没有保存的应用";
            selectedProgram.Text = "从右上角导入 Steam 游戏，或点“添加应用”选择启动程序。";
            guidance.Text = "";
            return;
        }
        selectedName.Text = profile.Name;
        selectedProgram.Text = profile.Executable;
        guidance.Text = "同步并启动会在游玩前后同步。仅同步会双向比较。";
        for (int i = 0; i < features.Length; i++)
        {
            var state = features[i].State(profile);
            var value = featureValues[i];
            var card = (TableLayoutPanel)value.Parent!;
            card.BackColor = value.BackColor = state.Active ? DesktopUi.Tint : DesktopUi.Canvas;
            card.GetControlFromPosition(0, 0)!.ForeColor = state.Active ? DesktopUi.Accent : DesktopUi.Muted;
            value.Text = state.Value;
        }
    }
    void Persist() => Storage.Write(Path.Combine(dataRoot, "profiles.json"), profiles);
    async Task EditProfileAsync(GameProfile? profile, SettingsPage? page = null)
    {
        using var dialog = new ProfileForm(profile);
        if (page.HasValue) dialog.SelectPage(page.Value);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (profile == null) profiles.Add(dialog.Profile);
        else profiles[profiles.IndexOf(profile)] = dialog.Profile;
        Persist(); ReloadGames(dialog.Profile); Log($"已保存“{dialog.Profile.Name}”的设置。");
        if (dialog.DiscoverAfterSave) await LaunchAsync(true, true);
    }
    bool AskElevation() => MessageBox.Show(this, "选定的存档目录拒绝访问。是否通过 UAC 只为本次同步请求管理员权限？游戏仍以普通权限启动，应用不会修改目录所有权或权限。", "目录访问权限", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    async Task SynchronizeAsync(bool showCompletion, SyncDirection direction = SyncDirection.Both)
    {
        var p = Current ?? throw new InvalidOperationException("请先添加应用。");
        if (p.UseSteamCloud)
        {
            await using var steam = await OpenSteamAsync();
            await SynchronizeCloudAsync(p, steam); return;
        }
        if (p.Rules.Count == 0) { if (showCompletion) Log("尚未选择同步目录。"); return; }
        SyncPlan plan; bool elevated = false;
        try { plan = await Task.Run(() => SyncEngine.Plan(p, dataRoot, direction)); }
        catch (UnauthorizedAccessException)
        {
            if (!AskElevation()) throw new OperationCanceledException();
            plan = (await Program.ElevatedSync(p, dataRoot, null, direction)).Plan!; elevated = true;
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
            var fresh = (await Program.ElevatedSync(p, dataRoot, null, direction)).Plan!;
            if (fresh.Items.Any(i => i.Action == "Conflict"))
            {
                using var dialog = new ConflictForm(p, fresh);
                if (dialog.ShowDialog(this) != DialogResult.OK) throw new OperationCanceledException();
            }
            backup = (await Program.ElevatedSync(p, dataRoot, fresh)).Backup!;
        }
        Log($"同步完成：处理 {plan.Changes} 项；覆盖文件的备份保存在游戏配置目录中。{(Directory.Exists(backup) ? " 备份：" + backup : "")}");
    }

    Task SynchronizeCloudAsync(GameProfile profile, SteamSession steam) =>
        WaitForSteamAsync(token => steam.RunConnectedAsync(async connection =>
    {
        Log("正在读取 Steam 云存档并核对本机进度…");
        using var sync = await SteamCloudSync.CreateAsync(profile, dataRoot, connection);
        if (sync.Plan.Items.Any(i => i.Action == "Conflict"))
        {
            using var dialog = new ConflictForm(sync.Profile, sync.Plan, sync.Files);
            if (dialog.ShowDialog(this) != DialogResult.OK) throw new OperationCanceledException("尚未解决 Steam 云存档冲突。");
        }
        string backup = await sync.ApplyAsync();
        Log($"Steam 云存档同步完成：处理 {sync.Plan.Changes} 项。{(Directory.Exists(backup) ? " 备份：" + backup : "")}");
        return backup;
    }, token));

    async Task LaunchAsync(bool discover, bool synchronizeSaves, GameProfile? importDraft = null, string? targetUser = null)
    {
        var p = importDraft ?? Current ?? throw new InvalidOperationException("请先添加应用。");
        p.Executable = SyncEngine.Normalize(p.Executable);
        p.WorkingDirectory = string.IsNullOrWhiteSpace(p.WorkingDirectory) ? Path.GetDirectoryName(p.Executable)! : SyncEngine.Normalize(p.WorkingDirectory);
        if (!File.Exists(p.Executable)) throw new FileNotFoundException("找不到游戏启动程序。", p.Executable);
        if (GetCurrentPackageFullName(ref packageBufferLength, IntPtr.Zero) != 15700)
            throw new IOException("当前继承了打包应用的目录虚拟化环境。请从资源管理器打开本应用，再启动真实游戏，避免存档被写进宿主应用的私有目录。");
        if (synchronizeSaves && !p.UseSteamCloud && !discover && p.Rules.Count == 0 && MessageBox.Show(this, "尚未选择同步目录，本次仅启动游戏。", "没有同步规则", MessageBoxButtons.OKCancel) != DialogResult.OK)
            throw new OperationCanceledException();
        if (synchronizeSaves && !p.UseSteamCloud) await SynchronizeAsync(false, SyncDirection.TargetToLocal);
        await using var steam = p.ReportSteamPlaytime || p.SyncAchievements || (synchronizeSaves && p.UseSteamCloud)
            ? await OpenSteamAsync() : null;
        if (synchronizeSaves && p.UseSteamCloud) await SynchronizeCloudAsync(p, steam!);
        if (p.SyncAchievements) await SynchronizeAchievementsAsync(p, steam!);
        DiscoveryCapture? capture = null; LoaderRegistry? registry = null; DiscoveryResult? result = null;
        int exitCode = 0; bool exited = false, reported = false; string? recording = null;
        var errors = new List<Exception>();
        using var achievementStop = new CancellationTokenSource();
        Task? achievements = null;
        Task<bool>? endedPlaying = null;
        async Task<int> WaitForGameAsync(GameProcess game)
        {
            int code = await game.WaitAsync();
            achievementStop.Cancel();
            if (reported) endedPlaying = steam!.StopPlayingAsync();
            return code;
        }
        async Task MonitorAchievementsAsync()
        {
            try
            {
                await AchievementSync.MonitorAsync(p, dataRoot, steam!, new Progress<AchievementSyncResult>(result =>
                    Log($"Steam 实时成就同步：新上传 {result.Uploaded} 项；Steam 已解锁 {result.Unlocked}/{result.Total}。")), achievementStop.Token);
            }
            catch (OperationCanceledException) when (achievementStop.IsCancellationRequested) { }
            catch (Exception ex) { errors.Add(ex); Log("Steam 实时成就同步已停止：" + ex.Message + " 本地记录保留，退出游戏后再次同步。"); }
        }
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
            if (p.ReportSteamPlaytime) { await WaitForSteamAsync(token => steam!.StartPlayingAsync(p, game.Id, token)); reported = true; }
            game.Resume();
            var wait = WaitForGameAsync(game);
            if (p.SyncAchievements) { achievements = MonitorAchievementsAsync(); Log("Steam 实时成就同步已开启，每两秒检查本地新解锁记录。"); }
            Log($"已启动 {p.Name}。正在等待整个进程树退出…");
            if (p.ReportSteamPlaytime) Log("Steam 已确认正在游玩；连接保持期间上报游玩时长。");
            if (p.RecordGame)
            {
                try
                {
                    int pid = await ObsRecorder.FindGameProcessAsync(game, p.Executable);
                    var started = await ObsRecorder.StartAsync(dataRoot, new RecordingOptions { ProcessId = pid, ObsExecutable = p.ObsExecutable,
                        Output = ObsRecorder.CreateOutputPath(p, DateTimeOffset.Now) }, p);
                    recording = started.WorkingFile; Log("OBS 已开始录制：720p / 30 帧，仅游戏窗口及游戏音频。录像：" + started.Output);
                }
                catch (Exception ex) { errors.Add(ex); Log("OBS 录制未开始：" + ex.Message + " 游戏会继续运行。"); }
            }
            if (p.ReportSteamPlaytime && await Task.WhenAny(wait, steam!.PlayingInterrupted) == steam.PlayingInterrupted)
            {
                var error = await steam.PlayingInterrupted; errors.Add(error); Log(error.Message);
            }
            exitCode = await wait;
            exited = true;
            Log($"全部游戏进程已退出，启动程序退出码：{exitCode}。");
        }
        catch (OperationCanceledException) when (cancelOperation?.IsCancellationRequested == true) { throw; }
        catch (Exception ex) { errors.Add(ex); }
        finally
        {
            achievementStop.Cancel();
            if (p.ReportSteamPlaytime)
            {
                try
                {
                    bool cleared = await (endedPlaying ?? steam!.StopPlayingAsync());
                    if (reported) Log(cleared ? "Steam 已确认结束游玩状态。" : "本局游玩上报已结束。");
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (recording != null)
            {
                try { var saved = await ObsRecorder.StopAsync(dataRoot, recording); Log("OBS 录制结束：" + (saved.Output ?? saved.Message)); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (capture != null)
            {
                try { result = await capture.FinishAsync(); }
                catch (Exception ex) { errors.Add(ex); }
                try { await capture.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            }
            if (registry != null) { try { registry.Dispose(); } catch (Exception ex) { errors.Add(ex); } }
            if (achievements != null) await achievements;
        }
        if (exited && synchronizeSaves)
        {
            try
            {
                if (p.UseSteamCloud) await SynchronizeCloudAsync(p, steam!);
                else await SynchronizeAsync(false, SyncDirection.LocalToTarget);
            }
            catch (OperationCanceledException) when (cancelOperation?.IsCancellationRequested == true) { throw; }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (exited && p.SyncAchievements)
        {
            try
            {
                await SynchronizeAchievementsAsync(p, steam!);
            }
            catch (OperationCanceledException) when (cancelOperation?.IsCancellationRequested == true) { throw; }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (result != null)
        {
            Log($"采集完成：{result.Processes.Count} 个进程，{result.Files.Count} 个创建 / 写入路径，丢失事件 {result.EventsLost}。报告：{capture!.Folder}");
            using var dialog = new CandidateForm(result);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                foreach (var candidate in dialog.Selected)
                    if (!p.Rules.Any(r => r.LocalPath.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase)))
                        p.Rules.Add(importDraft == null ? new SyncRule { LocalPath = candidate.Path, Patterns = candidate.Patterns }
                            : SteamGameImport.MakeRule(p, dataRoot, targetUser, candidate.Path, candidate.Patterns));
                if (importDraft == null)
                {
                    Persist(); UpdateSelection(); Log("已加入候选规则。请点击“编辑设置”填写对应的同步目标路径，再使用“仅同步”检查首次差异。");
                }
            }
        }
        if (errors.Count > 0) throw new AggregateException(string.Join(Environment.NewLine, errors.Select(e => e.Message)), errors);
    }
    async Task SynchronizeAchievementsAsync()
    {
        var profile = Current ?? throw new InvalidOperationException("请先添加应用。");
        await using var session = await OpenSteamAsync();
        await SynchronizeAchievementsAsync(profile, session);
    }
    Task SynchronizeAchievementsAsync(GameProfile profile, SteamSession session) => WaitForSteamAsync(async token =>
    {
        var result = await AchievementSync.SynchronizeAsync(profile, dataRoot, session, token);
        Log($"Steam 成就同步完成：新上传 {result.Uploaded} 项；Steam 已解锁 {result.Unlocked}/{result.Total}。本地已有成就已保留。");
    });
    static uint packageBufferLength;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);
}
