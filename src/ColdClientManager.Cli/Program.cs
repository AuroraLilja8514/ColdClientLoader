using ColdClientManager;

if (args is ["import", .. var options])
{
    if (options is [] or ["help"] or ["--help"]) { Console.WriteLine(SteamImportCommand.Help); return 0; }
    using var instance = new Mutex(false, Storage.InstanceMutex, out bool created);
    if (!created) { Console.Error.WriteLine("请先关闭正在运行的管理器，再使用命令行导入。"); return 1; }
    return await SteamImportCommand.RunAsync(options, Storage.DataRoot);
}
return await RecordingCommand.RunAsync(args, Storage.DataRoot);
