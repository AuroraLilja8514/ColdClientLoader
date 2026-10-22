using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Length != 2) return 2;
string role = args[0], root = args[1];
void Write(string relative, string text)
{
    string path = Path.Combine(root, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
}
void Spawn(string next)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    info.ArgumentList.Add(next); info.ArgumentList.Add(root);
    using var process = Process.Start(info)!;
}
switch (role)
{
    case "launch-mode":
        string input = Path.Combine(root, "profile", "saves", "slot1.sav");
        Write("launch-input.txt", File.Exists(input) ? File.ReadAllText(input) : "missing");
        Spawn("child");
        break;
    case "playtime-root":
        Spawn("playtime-child");
        break;
    case "playtime-child":
        Write("playtime-started.txt", "started");
        Thread.Sleep(125000);
        Write("playtime-finished.txt", "finished");
        break;
    case "presence":
        Thread.Sleep(15000);
        break;
    case "steam-achievement":
        if (!SteamApi.Init()) return 10;
        try
        {
            IntPtr stats = SteamApi.UserStats();
            if (!SteamApi.RequestCurrentStats(stats)) return 11;
            for (int i = 0; i < 20; i++) { SteamApi.RunCallbacks(); Thread.Sleep(20); }
            if (!SteamApi.SetAchievement(stats, root)) return 12;
            if (!SteamApi.StoreStats(stats)) return 13;
            for (int i = 0; i < 20; i++) { SteamApi.RunCallbacks(); Thread.Sleep(20); }
        }
        finally { SteamApi.Shutdown(); }
        break;
    case "root":
        Write(@"profile\saves\slot1.sav", "root");
        Write(@"cache\gpu.bin", "cache noise");
        Write(@"logs\launch.log", "log noise");
        Spawn("child");
        for (int i = 0; i < 4; i++) Spawn("short" + i);
        Thread.Sleep(80);
        break;
    case "child":
        Spawn("grandchild");
        Thread.Sleep(300);
        Write(@"profile\saves\slot2.sav", "child");
        break;
    case "grandchild":
        Thread.Sleep(1600);
        Write(@"profile\saves\slot1.sav", "grandchild-final");
        Write(@"profile\saves\sub\slot3.dat", "grandchild-slot");
        break;
    default:
        Write(@"profile\saves\" + role + ".sav", role);
        break;
}
return 0;

static class SteamApi
{
    [DllImport("steam_api64", EntryPoint = "SteamAPI_Init", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] public static extern bool Init();
    [DllImport("steam_api64", EntryPoint = "SteamAPI_SteamUserStats_v012", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr UserStats();
    [DllImport("steam_api64", EntryPoint = "SteamAPI_ISteamUserStats_RequestCurrentStats", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] public static extern bool RequestCurrentStats(IntPtr stats);
    [DllImport("steam_api64", EntryPoint = "SteamAPI_ISteamUserStats_SetAchievement", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] public static extern bool SetAchievement(IntPtr stats, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("steam_api64", EntryPoint = "SteamAPI_ISteamUserStats_StoreStats", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] public static extern bool StoreStats(IntPtr stats);
    [DllImport("steam_api64", EntryPoint = "SteamAPI_RunCallbacks", CallingConvention = CallingConvention.Cdecl)] public static extern void RunCallbacks();
    [DllImport("steam_api64", EntryPoint = "SteamAPI_Shutdown", CallingConvention = CallingConvention.Cdecl)] public static extern void Shutdown();
}
