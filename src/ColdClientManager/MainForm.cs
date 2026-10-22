using System.Runtime.InteropServices;

namespace ColdClientManager;

public sealed class MainForm : Form
{
    readonly string dataRoot;
    readonly List<GameProfile> profiles;
    readonly ListBox games = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 11F) };
    readonly Label selectedName = new() { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label selectedProgram = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label summary = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new Padding(2, 8, 2, 0) };
    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.FromArgb(247, 249, 251) };
    readonly Label status = new() { Text = "就绪", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4, 7, 4, 0) };
    readonly List<Control> editors = [], profileActions = [];
    bool busy;
    GameProfile? Current => games.SelectedItem as GameProfile;

    public MainForm(string dataRoot)
    {
        this.dataRoot = dataRoot;
        Text = "我的应用 · ColdClientManager";
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1100, 780); MinimumSize = new Size(940, 740);
        string config = Path.Combine(dataRoot, "profiles.json");
        profiles = File.Exists(config) ? Storage.Read<List<GameProfile>>(config) : [];
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
        foreach (var row in new[] { new RowStyle(SizeType.Absolute, 64), new RowStyle(SizeType.Percent, 100), new RowStyle(SizeType.Absolute, 48),
            new RowStyle(SizeType.Absolute, 28), new RowStyle(SizeType.Absolute, 100), new RowStyle(SizeType.Absolute, 28) }) layout.RowStyles.Add(row);
        Controls.Add(layout);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.Absolute, 130));
        header.Controls.Add(new Label { Text = "我的应用", Font = new Font(Font.FontFamily, 20F, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var add = AsyncButton("添加应用", () => EditProfileAsync(null)); add.Anchor = AnchorStyles.Right;
        header.Controls.Add(add, 1, 0); layout.Controls.Add(header, 0, 0);
        var library = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        library.ColumnStyles.Add(new(SizeType.Percent, 32)); library.ColumnStyles.Add(new(SizeType.Percent, 68));
        var listPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Color.White, Padding = new Padding(12), Margin = new Padding(3, 3, 12, 3) };
        listPanel.RowStyles.Add(new(SizeType.Absolute, 36)); listPanel.RowStyles.Add(new(SizeType.Percent, 100));
        listPanel.Controls.Add(new Label { Text = "已保存的应用", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        listPanel.Controls.Add(games, 0, 1); library.Controls.Add(listPanel, 0, 0);
        var details = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1, BackColor = Color.White, Padding = new Padding(16) };
        foreach (var row in new[] { new RowStyle(SizeType.Absolute, 46), new RowStyle(SizeType.Absolute, 26), new RowStyle(SizeType.Percent, 100), new RowStyle(SizeType.Absolute, 48),
            new RowStyle(SizeType.Absolute, 60), new RowStyle(SizeType.Absolute, 46) }) details.RowStyles.Add(row);
        details.Controls.Add(selectedName, 0, 0); details.Controls.Add(selectedProgram, 0, 1); details.Controls.Add(summary, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var play = AsyncButton("同步并启动", () => LaunchAsync(false, true));
        play.BackColor = Color.FromArgb(35, 100, 180); play.ForeColor = Color.White; play.FlatStyle = FlatStyle.Flat;
        actions.Controls.Add(play);
        actions.Controls.Add(AsyncButton("仅同步", () => SynchronizeAsync(true)));
        actions.Controls.Add(AsyncButton("仅启动", () => LaunchAsync(false, false)));
        profileActions.AddRange(actions.Controls.Cast<Control>());
        details.Controls.Add(actions, 0, 3);
        details.Controls.Add(new Label { Text = "同步并启动：启动前、退出后各同步一次存档。\n仅同步不启动；仅启动不处理存档。", Dock = DockStyle.Fill, Padding = new Padding(3, 5, 0, 0) }, 0, 4);
        var manage = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        manage.Controls.Add(AsyncButton("编辑设置", () => EditProfileAsync(Current!)));
        manage.Controls.Add(Button("打开备份目录", () => {
            string folder = Storage.ProfileFolder(dataRoot, Current!); Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }));
        manage.Controls.Add(Button("移除条目", () => {
            if (MessageBox.Show(this, "只移除应用条目，存档和备份会保留。", "移除条目", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            profiles.Remove(Current!); Persist(); ReloadGames(profiles.FirstOrDefault());
        }));
        profileActions.AddRange(manage.Controls.Cast<Control>()); details.Controls.Add(manage, 0, 5);
        library.Controls.Add(details, 1, 0); layout.Controls.Add(library, 0, 1);
        var steamActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
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
        layout.Controls.Add(steamActions, 0, 2);
        layout.Controls.Add(new Label { Text = "活动记录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
        layout.Controls.Add(log, 0, 4); layout.Controls.Add(status, 0, 5);
        games.SelectedIndexChanged += (_, _) => UpdateSelection();
        FormClosing += (_, e) => {
            if (busy) { e.Cancel = true; MessageBox.Show(this, "请等游戏及子进程退出、同步完成后再关闭应用。", "会话仍在运行"); }
        };
        ReloadGames(profiles.FirstOrDefault());
        Log("选择已保存的应用即可同步或启动；添加或修改路径、启动参数和同步规则，请使用“添加应用”或“编辑设置”。");
        if (File.Exists(Path.Combine(dataRoot, "loader-registry-recovery.json"))) Log("检测到上次启动的恢复记录，请先点击“恢复启动器环境”。");
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
        games.Enabled = !value; UpdateSelection();
        status.Text = value ? "正在处理；游玩期间请保留此窗口。" : "就绪";
    }
    void ShowError(Exception ex) { Log("未完成：" + ex.Message); MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    void Log(string message) => log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    void ReloadGames(GameProfile? selected)
    {
        games.DataSource = null; games.DataSource = profiles.ToList();
        if (selected != null) games.SelectedItem = selected;
        UpdateSelection();
    }
    void UpdateSelection()
    {
        foreach (var control in profileActions) control.Enabled = !busy && Current != null;
        if (Current is not { } profile)
        {
            selectedName.Text = "还没有保存的应用";
            selectedProgram.Text = "";
            summary.Text = "点击右上角“添加应用”，设置启动程序和同步规则后保存。\n以后可以直接在这里选择并启动。";
            return;
        }
        selectedName.Text = profile.Name;
        selectedProgram.Text = profile.Executable;
        summary.Text = $"{(profile.UseColdClient ? "ColdClientLoader" : "直接启动")} · {profile.Rules.Count} 条存档同步规则\nSteam 成就同步：{(profile.SyncAchievements ? "已开启" : "未开启")}\nSteam 状态与时长：{(profile.ReportSteamPlaytime ? "已开启" : "未开启")}\nOBS 录制：{(profile.RecordGame ? "720p / 30 帧" : "未开启")}";
    }
    void Persist() => Storage.Write(Path.Combine(dataRoot, "profiles.json"), profiles);
    async Task EditProfileAsync(GameProfile? profile)
    {
        using var dialog = new ProfileForm(profile);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (profile == null) profiles.Add(dialog.Profile);
        else profiles[profiles.IndexOf(profile)] = dialog.Profile;
        Persist(); ReloadGames(dialog.Profile); Log($"已保存“{dialog.Profile.Name}”的设置。");
        if (dialog.DiscoverAfterSave) await LaunchAsync(true, true);
    }
    bool AskElevation() => MessageBox.Show(this, "选定的存档目录拒绝访问。是否通过 UAC 只为本次同步请求管理员权限？游戏仍以普通权限启动，应用不会修改目录所有权或权限。", "目录访问权限", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    async Task SynchronizeAsync(bool showCompletion)
    {
        var p = Current ?? throw new InvalidOperationException("请先添加应用。");
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

    async Task LaunchAsync(bool discover, bool synchronizeSaves)
    {
        var p = Current ?? throw new InvalidOperationException("请先添加应用。");
        p.Executable = SyncEngine.Normalize(p.Executable);
        p.WorkingDirectory = string.IsNullOrWhiteSpace(p.WorkingDirectory) ? Path.GetDirectoryName(p.Executable)! : SyncEngine.Normalize(p.WorkingDirectory);
        if (!File.Exists(p.Executable)) throw new FileNotFoundException("找不到游戏启动程序。", p.Executable);
        if (GetCurrentPackageFullName(ref packageBufferLength, IntPtr.Zero) != 15700)
            throw new IOException("当前继承了打包应用的目录虚拟化环境。请从资源管理器打开本应用，再启动真实游戏，避免存档被写进宿主应用的私有目录。");
        if (synchronizeSaves && !discover && p.Rules.Count == 0 && MessageBox.Show(this, "尚未选择同步目录，本次仅启动游戏。", "没有同步规则", MessageBoxButtons.OKCancel) != DialogResult.OK)
            throw new OperationCanceledException();
        if (synchronizeSaves) await SynchronizeAsync(false);
        await using var steam = p.ReportSteamPlaytime
            ? (File.Exists(SteamSession.LoginPath(dataRoot)) ? await SteamSession.ConnectAsync(dataRoot) : SteamLoginForm.SignIn(this, dataRoot)) : null;
        if (p.SyncAchievements)
        {
            if (steam != null) await SynchronizeAchievementsAsync(p, steam);
            else await SynchronizeAchievementsAsync();
        }
        DiscoveryCapture? capture = null; LoaderRegistry? registry = null; DiscoveryResult? result = null;
        int exitCode = 0; bool exited = false, reported = false; string? recording = null;
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
            if (p.ReportSteamPlaytime) { await steam!.StartPlayingAsync(p, game.Id); reported = true; }
            game.Resume();
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
            var wait = game.WaitAsync();
            if (p.ReportSteamPlaytime && await Task.WhenAny(wait, steam!.PlayingInterrupted) == steam.PlayingInterrupted)
            {
                var error = await steam.PlayingInterrupted; errors.Add(error); Log(error.Message);
            }
            exitCode = await wait;
            exited = true;
            Log($"全部游戏进程已退出，启动程序退出码：{exitCode}。");
        }
        catch (Exception ex) { errors.Add(ex); }
        finally
        {
            if (p.ReportSteamPlaytime)
            {
                try { await steam!.StopPlayingAsync(); if (reported) Log("Steam 已确认结束游玩状态。"); }
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
        }
        if (exited && synchronizeSaves) { try { await SynchronizeAsync(false); } catch (Exception ex) { errors.Add(ex); } }
        if (exited && p.SyncAchievements)
        {
            try
            {
                if (steam != null) await SynchronizeAchievementsAsync(p, steam);
                else await SynchronizeAchievementsAsync();
            }
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
                        p.Rules.Add(new SyncRule { LocalPath = candidate.Path, Patterns = candidate.Patterns });
                Persist(); UpdateSelection(); Log("已加入候选规则。请点击“编辑设置”填写对应的同步目标路径，再使用“仅同步”检查首次差异。");
            }
        }
        if (errors.Count > 0) throw new AggregateException(string.Join(Environment.NewLine, errors.Select(e => e.Message)), errors);
    }
    async Task SynchronizeAchievementsAsync()
    {
        var profile = Current ?? throw new InvalidOperationException("请先添加应用。");
        await using var session = File.Exists(SteamSession.LoginPath(dataRoot))
            ? await SteamSession.ConnectAsync(dataRoot) : SteamLoginForm.SignIn(this, dataRoot);
        await SynchronizeAchievementsAsync(profile, session);
    }
    async Task SynchronizeAchievementsAsync(GameProfile profile, SteamSession session)
    {
        var result = await AchievementSync.SynchronizeAsync(profile, dataRoot, session);
        Log($"Steam 成就同步完成：新上传 {result.Uploaded} 项；Steam 已解锁 {result.Unlocked}/{result.Total}。本地已有成就已保留。");
    }
    static uint packageBufferLength;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);
}
