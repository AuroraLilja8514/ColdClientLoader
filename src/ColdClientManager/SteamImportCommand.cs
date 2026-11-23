using SteamKit2;

namespace ColdClientManager;

public static class SteamImportCommand
{
    public const string Help = """
        import "Steam 游戏安装目录" [--launch 序号] [--loader 启动器.exe] [--obs-exe obs64.exe]
          [--recording-dir 录像目录] [--format mkv|mp4] [--target-user 目标Windows用户目录]
          [--save-dir 已确认的存档目录] [--target 单条规则的目标路径] [--offline]
        默认复用本机 Steam 登录、ColdClientLoader 和 OBS 设置。完成导入时进行首次双向目录同步。
        有多个启动项时会列出序号。未知存档可在图形界面查找，也可用 --save-dir 明确指定。
        """;

    public static async Task<int> RunAsync(string[] args, string dataRoot)
    {
        try
        {
            string folder = SyncEngine.Normalize(args[0]);
            var values = new Dictionary<string, string>();
            for (int i = 1; i < args.Length; i++)
            {
                string key = args[i];
                if (key is not ("--launch" or "--loader" or "--obs-exe" or "--recording-dir" or "--format" or "--target-user" or "--save-dir" or "--target" or "--offline"))
                    throw new ArgumentException("未知的导入选项：" + key);
                if (key != "--offline" && (++i == args.Length || args[i].StartsWith("--"))) throw new ArgumentException("导入选项缺少值：" + key);
                if (!values.TryAdd(key, key == "--offline" ? "" : args[i])) throw new ArgumentException("重复的导入选项：" + key);
            }
            string config = Path.Combine(dataRoot, "profiles.json");
            List<GameProfile> profiles = File.Exists(config) ? Storage.Read<List<GameProfile>>(config) : [];
            var draft = SteamGameImport.ReadInstall(folder);
            KeyValue? app = null;
            if (!values.ContainsKey("--offline"))
            {
                if (!File.Exists(SteamSession.LoginPath(dataRoot))) throw new IOException("请先在管理器中登录 Steam，或用 --offline 读取本地安装文件。");
                await using var steam = await SteamSession.ConnectAsync(dataRoot);
                if (ulong.TryParse(draft.SteamId, out ulong owner) && owner != 0 && owner != steam.SteamId)
                    throw new InvalidOperationException("安装记录和登录账号不同，请通过“从 Steam 导入”确认存档账号。");
                draft.SteamId = steam.SteamId.ToString();
                app = await steam.GetAppInfoAsync(uint.Parse(draft.AppId));
            }
            if (SteamGameImport.AlreadySaved(profiles, draft, folder) is { } existing)
            {
                if (existing.Rules.Count == 0 && !existing.UseSteamCloud)
                    throw new InvalidOperationException("已保存此游戏的启动设置。要继续配置存档，请在主页面选择“从 Steam 导入”。");
                Console.WriteLine("条目已存在：" + existing.Name); return 0;
            }
            var launches = SteamGameImport.FindLaunchOptions(folder, app);
            int choice = 1;
            bool specified = values.TryGetValue("--launch", out var number);
            if (specified && !int.TryParse(number, out choice)) throw new ArgumentException("--launch 需要启动项的序号。");
            if ((!specified && launches.Count != 1) || choice < 1 || choice > launches.Count)
            {
                for (int i = 0; i < launches.Count; i++)
                    Console.Error.WriteLine($"{i + 1}. {Path.GetRelativePath(folder, launches[i].Executable)} {launches[i].Arguments}  （{launches[i].Directory}）");
                throw new InvalidOperationException(launches.Count == 0 ? "没有识别到游戏启动项，请通过图形界面选择实际程序。" : "请用 --launch 指定上面一个启动项的序号。");
            }
            SteamGameImport.SetLaunch(draft, launches[choice - 1], profiles, dataRoot);
            if (values.TryGetValue("--loader", out var loader)) draft.LoaderSource = SyncEngine.Normalize(loader);
            if (values.TryGetValue("--obs-exe", out var obs)) draft.ObsExecutable = SyncEngine.Normalize(obs);
            if (values.TryGetValue("--recording-dir", out var videos)) draft.RecordingDirectory = SyncEngine.Normalize(videos);
            if (values.TryGetValue("--format", out var format)) draft.RecordingFormat = format.ToLowerInvariant();
            if (draft.RecordingFormat is not ("mkv" or "mp4")) throw new ArgumentException("录像格式请选择 mkv 或 mp4。");
            SteamGameImport.ValidateLaunch(draft);
            if (SteamGameImport.NeedsPortableObs(draft, dataRoot)) throw new IOException("请指定已安装的 --obs-exe，或先执行 Record-Game.ps1 install 安装专用 OBS。");

            string? targetUser = values.TryGetValue("--target-user", out var targetRoot) ? SyncEngine.Normalize(targetRoot)
                : SteamGameImport.PreviousTargetUser(profiles, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dataRoot);
            if (values.TryGetValue("--save-dir", out var saves))
                draft.Rules.Add(SteamGameImport.MakeRule(draft, dataRoot, targetUser, SyncEngine.Normalize(saves), "*"));
            else if (app != null) draft.Rules = SteamGameImport.KnownRules(draft, app["ufs"], dataRoot, targetUser);
            if (draft.Rules.Count == 0) throw new InvalidOperationException("没有已知存档规则。请通过“保存并查找存档”确认候选目录，或用 --save-dir 指定。");
            if (values.TryGetValue("--target", out var target))
            {
                if (draft.Rules.Count != 1) throw new ArgumentException("--target 需要恰好一条存档规则。多个目录请使用 --target-user 或在预览表中设置。");
                draft.Rules[0].SharedPath = SyncEngine.Normalize(target);
            }
            var plan = SyncEngine.Plan(draft, dataRoot);
            if (plan.Items.Any(i => i.Action == "Conflict"))
                throw new IOException("两侧已有不同存档，请通过图形界面导入并选择保留版本。");
            SteamGameImport.Save(dataRoot, profiles, draft);
            SyncEngine.Apply(draft, dataRoot, plan);
            Console.WriteLine($"已导入 {draft.Name} (AppID {draft.AppId})。首次同步处理 {plan.Changes} 项。");
            foreach (var rule in draft.Rules) Console.WriteLine($"{rule.LocalPath} → {rule.SharedPath}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
