using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using SteamKit2;
using SteamKit2.Internal;

namespace ColdClientManager;

public sealed record SteamAchievement(string Name, uint Block, int Bit, JsonObject Configuration);
public sealed record SteamAchievementSchema(List<SteamAchievement> Achievements, JsonArray Stats);
public sealed record AchievementSyncResult(int Uploaded, int Unlocked, int Total);
public sealed record AchievementAccount(uint AppId, ulong SteamId);

public static class AchievementSync
{
    public static string LoaderDirectory(GameProfile profile, string dataRoot) => Path.Combine(Storage.ProfileFolder(dataRoot, profile), "loader");
    public static string OutputFile(GameProfile profile, string dataRoot) => Path.Combine(LoaderDirectory(profile, dataRoot), "emu-data", uint.Parse(profile.AppId, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), "achievements.json");
    public static string AccountFile(GameProfile profile, string dataRoot) => Path.Combine(Path.GetDirectoryName(OutputFile(profile, dataRoot))!, "achievement-account.json");

    public static uint ValidateProfile(GameProfile profile, string dataRoot, ulong steamId)
    {
        if (!profile.UseColdClient) throw new InvalidOperationException("成就同步目前支持由本应用配置的 ColdClientLoader 游戏。");
        uint appId = SteamSession.ValidateProfile(profile, steamId);
        string accountFile = AccountFile(profile, dataRoot);
        if (File.Exists(accountFile) && Storage.Read<AchievementAccount>(accountFile) != new AchievementAccount(appId, steamId))
            throw new InvalidDataException("此游戏配置的本地成就已绑定其他 Steam 账号。请为另一账号新建游戏配置，避免混合同一份成就记录。");
        return appId;
    }

    public static async Task<CMsgClientGetUserStatsResponse> ReadRemoteAsync(SteamSession session, uint appId)
    {
        var remote = (await session.Stats.Get(appId, session.SteamId).ToTask().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false)).Body;
        if ((EResult)remote.eresult != EResult.OK) throw new IOException($"读取 Steam 成就失败：{(EResult)remote.eresult}。");
        return remote;
    }

    public static SteamAchievementSchema ReadSchema(CMsgClientGetUserStatsResponse remote)
    {
        if (remote.schema is not { Length: > 0 }) throw new InvalidDataException("Steam 没有返回游戏的成就定义。");
        var root = new KeyValue();
        using var input = new MemoryStream(remote.schema);
        if (!root.TryReadAsBinary(input)) throw new InvalidDataException("无法解析 Steam 成就定义。");
        var achievements = new List<SteamAchievement>();
        var stats = new JsonArray();
        foreach (var stat in root["stats"].Children)
        {
            uint id = uint.Parse(stat.Name!, CultureInfo.InvariantCulture);
            int type = stat["type"].Value switch {
                "1" or "INT" => 1, "2" or "FLOAT" => 2, "3" or "AVGRATE" => 3, "4" or "ACHIEVEMENTS" => 4,
                var value => throw new InvalidDataException($"不支持的 Steam 统计类型：{value}。")
            };
            if (type == 4)
            {
                foreach (var bit in stat["bits"].Children)
                {
                    int index = int.Parse(bit.Name!, CultureInfo.InvariantCulture);
                    if (index is < 0 or > 31) throw new InvalidDataException("Steam 成就位索引超出范围。");
                    string name = bit["name"].Value ?? throw new InvalidDataException("Steam 成就缺少名称。");
                    var display = bit["display"];
                    var configuration = new JsonObject {
                        ["name"] = name, ["displayName"] = ToJson(display["name"]),
                        ["description"] = ToJson(display["desc"]), ["hidden"] = display["hidden"].AsInteger()
                    };
                    if (bit["progress"].Children.Count > 0) configuration["progress"] = ToJson(bit["progress"], true);
                    achievements.Add(new(name, id, index, configuration));
                }
            }
            else
            {
                var definition = new JsonObject { ["name"] = stat["name"].Value, ["type"] = type switch {
                    1 => "int", 2 => "float", 3 => "avgrate", _ => throw new InvalidDataException($"不支持的 Steam 统计类型：{type}。")
                } };
                uint? value = remote.stats.FirstOrDefault(s => s.stat_id == id)?.stat_value;
                definition["default"] = type == 1
                    ? JsonValue.Create(value.HasValue ? unchecked((int)value.Value) : stat["default"].AsInteger())
                    : JsonValue.Create(value.HasValue ? BitConverter.UInt32BitsToSingle(value.Value) : stat["default"].AsFloat());
                stats.Add(definition);
            }
        }
        return new(achievements, stats);
    }

    static JsonNode? ToJson(KeyValue value, bool numbers = false)
    {
        if (value.Children.Count == 0)
        {
            if (numbers && double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return JsonValue.Create(number);
            return JsonValue.Create(value.Value);
        }
        var result = new JsonObject();
        foreach (var child in value.Children) result[child.Name!] = ToJson(child, numbers);
        return result;
    }

    public static JsonObject ReadLocal(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("本地 achievements.json 必须是成就记录对象。");
    }

    public static uint RemoteMask(CMsgClientGetUserStatsResponse remote, uint blockId)
    {
        // Steam can retain an unlock timestamp after ClearAchievement; the stats bit is authoritative.
        return remote.stats.FirstOrDefault(s => s.stat_id == blockId)?.stat_value ?? 0;
    }

    public static Dictionary<uint, uint> BuildUpload(SteamAchievementSchema schema, CMsgClientGetUserStatsResponse remote, JsonObject local)
    {
        var definitions = schema.Achievements.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var masks = schema.Achievements.Select(a => a.Block).Distinct().ToDictionary(id => id, id => RemoteMask(remote, id));
        foreach (var entry in local)
        {
            if (entry.Value is not JsonObject state || state["earned"] is not JsonValue earned || !earned.TryGetValue<bool>(out bool unlocked))
                throw new InvalidDataException($"本地成就记录无效：{entry.Key}。");
            if (!unlocked) continue;
            if (!definitions.TryGetValue(entry.Key, out var definition)) throw new InvalidDataException($"Steam 成就定义中找不到本地已解锁项：{entry.Key}。");
            masks[definition.Block] |= 1u << definition.Bit;
        }
        return masks.Where(m => m.Value != RemoteMask(remote, m.Key)).ToDictionary();
    }

    public static void MergeLocal(SteamAchievementSchema schema, CMsgClientGetUserStatsResponse remote, JsonObject local)
    {
        foreach (var definition in schema.Achievements)
        {
            var state = local[definition.Name] as JsonObject;
            if (state == null) { state = new JsonObject { ["earned"] = false, ["earned_time"] = 0 }; local[definition.Name] = state; }
            if ((RemoteMask(remote, definition.Block) & (1u << definition.Bit)) == 0 || state["earned"]!.GetValue<bool>()) continue;
            state["earned"] = true;
            var block = remote.achievement_blocks.FirstOrDefault(b => b.achievement_id == definition.Block);
            state["earned_time"] = block != null && definition.Bit < block.unlock_time.Count ? block.unlock_time[definition.Bit] : 0;
        }
    }

    public static async Task<AchievementSyncResult> SynchronizeAsync(GameProfile profile, string dataRoot, SteamSession session)
    {
        uint appId = ValidateProfile(profile, dataRoot, session.SteamId);
        var remote = await ReadRemoteAsync(session, appId).ConfigureAwait(false);
        var schema = ReadSchema(remote);
        string output = OutputFile(profile, dataRoot);
        var local = ReadLocal(output);
        var upload = BuildUpload(schema, remote, local);
        Storage.Write(AccountFile(profile, dataRoot), new AchievementAccount(appId, session.SteamId));
        int uploaded = upload.Sum(m => BitOperations.PopCount(m.Value & ~RemoteMask(remote, m.Key)));
        if (upload.Count > 0)
        {
            var stored = (await session.Stats.Store(appId, session.SteamId, remote.crc_stats, upload).ToTask()
                .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false)).Body;
            if ((EResult)stored.eresult != EResult.OK || stored.stats_out_of_date || stored.stats_failed_validation.Count != 0)
                throw new IOException($"Steam 未确认全部成就：{(EResult)stored.eresult}；数据过期：{stored.stats_out_of_date}；校验失败：{stored.stats_failed_validation.Count}。本地记录已保留，请稍后再次同步。");
            remote = await ReadRemoteAsync(session, appId).ConfigureAwait(false);
            if (upload.Any(m => (RemoteMask(remote, m.Key) & m.Value) != m.Value))
                throw new IOException("重新读取 Steam 后仍有成就未解锁。本地记录已保留，请稍后再次同步。");
        }
        MergeLocal(schema, remote, local);
        string settings = Path.Combine(LoaderDirectory(profile, dataRoot), "steam_settings");
        Storage.Write(Path.Combine(settings, "achievements.json"), schema.Achievements.Select(a => a.Configuration).ToArray());
        Storage.Write(Path.Combine(settings, "stats.json"), schema.Stats);
        Storage.Write(output, local);
        return new(uploaded, schema.Achievements.Count(a => (RemoteMask(remote, a.Block) & (1u << a.Bit)) != 0), schema.Achievements.Count);
    }
}
