using SteamKit2;
using SteamKit2.Internal;

namespace ColdClientManager;

// SteamKit supplies the wire messages, but does not expose these user-stats operations.
public sealed class SteamStatsHandler : ClientMsgHandler
{
    public sealed class Received : CallbackMsg
    {
        public CMsgClientGetUserStatsResponse Body { get; }
        public Received(IPacketMsg packet)
        {
            var message = new ClientMsgProtobuf<CMsgClientGetUserStatsResponse>(packet);
            JobID = message.TargetJobID; Body = message.Body;
        }
    }
    public sealed class Stored : CallbackMsg
    {
        public CMsgClientStoreUserStatsResponse Body { get; }
        public Stored(IPacketMsg packet)
        {
            var message = new ClientMsgProtobuf<CMsgClientStoreUserStatsResponse>(packet);
            JobID = message.TargetJobID; Body = message.Body;
        }
    }

    public AsyncJob<Received> Get(uint appId, ulong steamId)
    {
        var message = new ClientMsgProtobuf<CMsgClientGetUserStats>(EMsg.ClientGetUserStats) { SourceJobID = Client.GetNextJobID() };
        message.ProtoHeader.routing_appid = appId;
        message.Body.game_id = appId; message.Body.steam_id_for_user = steamId;
        var job = new AsyncJob<Received>(Client, message.SourceJobID);
        Client.Send(message); return job;
    }

    public AsyncJob<Stored> Store(uint appId, ulong steamId, uint crc, Dictionary<uint, uint> masks)
    {
        var message = new ClientMsgProtobuf<CMsgClientStoreUserStats2>(EMsg.ClientStoreUserStats2) { SourceJobID = Client.GetNextJobID() };
        message.ProtoHeader.routing_appid = appId;
        message.Body.game_id = appId; message.Body.settor_steam_id = message.Body.settee_steam_id = steamId;
        message.Body.crc_stats = crc; message.Body.explicit_reset = false;
        foreach (var mask in masks) message.Body.stats.Add(new() { stat_id = mask.Key, stat_value = mask.Value });
        var job = new AsyncJob<Stored>(Client, message.SourceJobID);
        Client.Send(message); return job;
    }

    public override void HandleMsg(IPacketMsg packet)
    {
        if (packet.MsgType == EMsg.ClientGetUserStatsResponse) Client.PostCallback(new Received(packet));
        else if (packet.MsgType == EMsg.ClientStoreUserStatsResponse) Client.PostCallback(new Stored(packet));
    }
}
