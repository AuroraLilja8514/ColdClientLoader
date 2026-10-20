using System.Text.Json;

namespace ColdClientManager;

public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新游戏";
    public string Executable { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool UseColdClient { get; set; }
    public string LoaderSource { get; set; } = "";
    public string AppId { get; set; } = "";
    public string SteamId { get; set; } = "";
    public List<SyncRule> Rules { get; set; } = [];
    public override string ToString() => Name;
}

public sealed class SyncRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string LocalPath { get; set; } = "";
    public string SharedPath { get; set; } = "";
    public bool SingleFile { get; set; }
    public string Patterns { get; set; } = "*";
}

public static class Storage
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ColdClientManager");
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)!;
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, true);
    }
    public static string ProfileFolder(string dataRoot, GameProfile profile) => Path.Combine(dataRoot, "games", profile.Id);
}
