using System.Diagnostics;

namespace ColdClientManager;

public sealed record ElevatedSyncRequest(GameProfile Profile, string DataRoot, SyncPlan? Plan);
public sealed record ElevatedSyncResult(SyncPlan? Plan, string? Backup, string? Error);

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--record") { Environment.ExitCode = RecordingCommand.RunAsync(args[1..], Storage.DataRoot).GetAwaiter().GetResult(); return; }
        if (args.Length == 2 && args[0] == "--collect") { EtwCollector.Run(args[1]); return; }
        if (args.Length == 2 && args[0] == "--sync")
        {
            string response = args[1] + ".result";
            try
            {
                var request = Storage.Read<ElevatedSyncRequest>(args[1]);
                var result = request.Plan == null
                    ? new ElevatedSyncResult(SyncEngine.Plan(request.Profile, request.DataRoot), null, null)
                    : new ElevatedSyncResult(null, SyncEngine.Apply(request.Profile, request.DataRoot, request.Plan), null);
                Storage.Write(response, result);
            }
            catch (Exception ex) { Storage.Write(response, new ElevatedSyncResult(null, null, ex.Message)); }
            return;
        }
        ApplicationConfiguration.Initialize();
        using var singleton = new Mutex(true, @"Local\ColdClientManager-" + Environment.UserName, out bool created);
        if (!created) { MessageBox.Show("应用已经在运行。", "存档同步"); return; }
        try { Application.Run(new MainForm(Storage.DataRoot)); }
        catch (Exception ex) { MessageBox.Show(ex.ToString(), "存档同步：发生错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    public static async Task<ElevatedSyncResult> ElevatedSync(GameProfile profile, string dataRoot, SyncPlan? plan)
    {
        string path = Path.Combine(dataRoot, "requests", Guid.NewGuid().ToString("N") + ".json");
        Storage.Write(path, new ElevatedSyncRequest(profile, dataRoot, plan));
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--sync"); info.ArgumentList.Add(path);
        using var helper = Process.Start(info)!;
        await helper.WaitForExitAsync();
        string response = path + ".result";
        if (!File.Exists(response)) throw new IOException("权限操作未完成，请检查 UAC 或安全软件提示。");
        var result = Storage.Read<ElevatedSyncResult>(response);
        File.Delete(path); File.Delete(response);
        if (result.Error != null) throw new IOException(result.Error);
        return result;
    }
}
