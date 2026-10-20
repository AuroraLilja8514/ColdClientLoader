using System.Diagnostics;

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
