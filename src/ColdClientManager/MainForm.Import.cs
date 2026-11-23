using System.Net.Http;
using SteamKit2;

namespace ColdClientManager;

public sealed partial class MainForm
{
    async Task ImportGameAsync(string? initialDirectory)
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var source = new SteamImportForm(initialDirectory, SteamGameImport.PreviousTargetUser(profiles, user, dataRoot));
        if (source.ShowDialog(this) != DialogResult.OK) return;
        string folder = SyncEngine.Normalize(source.InstallDirectory);
        var draft = SteamGameImport.ReadInstall(folder);
        KeyValue? app = null;
        if (source.ReadSteamInfo)
        {
            Log($"正在读取“{draft.Name}”的 Steam 启动选项和存档位置。");
            try
            {
                await using var steam = await OpenSteamAsync();
                string account = steam.SteamId.ToString();
                if (ulong.TryParse(draft.SteamId, out ulong owner) && owner != 0 && owner != steam.SteamId
                    && MessageBox.Show(this, "安装记录中的存档账号与本次登录不同。是否用当前登录账号配置此游戏？", "确认存档账号",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                draft.SteamId = account;
                await WaitForSteamAsync(async token => { app = await steam.GetAppInfoAsync(uint.Parse(draft.AppId), token); });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (MessageBox.Show(this, ex.Message + "\n是否仅根据本地安装文件继续导入？可在预览中启动游戏查找存档。", "未能读取 Steam 游戏信息",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
        }
        string? targetUser = source.TargetUser;
        var existing = SteamGameImport.AlreadySaved(profiles, draft, folder);
        if (existing != null)
        {
            ReloadGames(existing);
            if (existing.Rules.Count > 0 || existing.UseSteamCloud) { Log($"已选中已保存的“{existing.Name}”。可通过“编辑设置”修改。"); return; }
            draft = existing.Copy();
        }
        else
        {
            var launches = await Task.Run(() => SteamGameImport.FindLaunchOptions(folder, app));
            var launch = SteamImportForm.ChooseLaunch(this, folder, launches);
            var previous = Current is { } selected ? profiles.Prepend(selected).Distinct().ToList() : profiles;
            SteamGameImport.SetLaunch(draft, launch, previous, dataRoot);
        }
        string? notice = null;
        if (app != null)
        {
            try { draft.Rules = SteamGameImport.KnownRules(draft, app["ufs"], dataRoot, targetUser); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            {
                notice = ex.Message + " 可点“保存并查找存档”。"; Log(notice);
            }
        }
        while (true)
        {
            string note = draft.Rules.Count == 0 && !draft.UseSteamCloud
                ? "尚无存档规则。可点“保存并查找存档”，进游戏保存一次再退出。也可手动添加规则，或只保留启动设置。"
                : "请核对本地存档和同步目标。“完成导入”会保存条目并做首次双向同步，两侧内容不同时由你选择。";
            using var editor = new ProfileForm(draft, notice ?? note);
            if (editor.ShowDialog(this) != DialogResult.OK) return;
            notice = null;
            draft = editor.Profile;
            if (SteamGameImport.NeedsPortableObs(draft, dataRoot))
            {
                if (MessageBox.Show(this, "录制需要 OBS。是否下载并安装本应用的便携 OBS？选择“否”可返回设置，选择已安装的 OBS 或关闭录制。", "配置 OBS",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) continue;
                try { await ObsRecorder.InstallAsync(dataRoot); }
                catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or TaskCanceledException)
                { notice = "便携 OBS 安装未完成：" + ex.Message; Log(notice); continue; }
            }
            SteamGameImport.Save(dataRoot, profiles, draft);
            ReloadGames(draft); Log($"已保存导入的“{draft.Name}”。");
            if (!editor.DiscoverAfterSave)
            {
                if (draft.Rules.Count > 0 || draft.UseSteamCloud) await SynchronizeAsync(true);
                return;
            }
            // Keep first-run progress associated with its saved loader even if the player cancels the review.
            draft = draft.Copy();
            try { await LaunchAsync(true, false, draft, targetUser); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { notice = "首次运行未完整结束：" + ex.Message; Log(notice); }
        }
    }
}
