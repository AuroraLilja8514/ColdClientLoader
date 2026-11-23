using SteamKit2;
using SteamKit2.Internal;

namespace ColdClientManager;

internal sealed class SteamNetworkException(string message) : IOException(message);
internal sealed class SteamTemporaryResultException(EResult result, string message) : IOException(message)
{
    public EResult Result => result;
}

// Callback promises, SteamKit jobs and presence acknowledgements belong to one connection.
internal sealed class SteamConnection : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly Task callbacks;
    readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<SteamUser.LoggedOnCallback> loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource playingSessionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<Exception> playingInterrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<Exception> lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile SteamUser.PlayingSessionStateCallback? playingSession;
    volatile TaskCompletionSource? presenceConfirmed;
    volatile uint expectedAppId;
    volatile bool reportingPlaytime;
    public SteamClient Client { get; }
    public SteamStatsHandler Stats { get; } = new();
    public ulong SteamId { get; private set; }
    public bool IsOnline => !lost.Task.IsCompleted && Client.IsConnected && Client.SteamID != null;
    public Task<Exception> Lost => lost.Task;
    public Task<Exception> PlayingInterrupted => playingInterrupted.Task;

    public SteamConnection(SteamConfiguration configuration)
    {
        Client = new SteamClient(configuration);
        Client.AddHandler(Stats);
        var manager = new CallbackManager(Client);
        manager.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        manager.Subscribe<SteamClient.DisconnectedCallback>(_ => ConnectionLost(new SteamNetworkException("Steam 连接已断开。")));
        manager.Subscribe<SteamUser.LoggedOnCallback>(result => loggedOn.TrySetResult(result));
        manager.Subscribe<SteamUser.LoggedOffCallback>(result => ConnectionLost(result.Result == EResult.OK
            ? new SteamNetworkException("Steam 已关闭当前连接。") : Failure(result.Result, $"Steam 会话已退出：{result.Result}。")));
        manager.Subscribe<SteamUser.PlayingSessionStateCallback>(result => {
            playingSession = result; playingSessionReady.TrySetResult();
            if (result.PlayingBlocked && reportingPlaytime)
            {
                var error = new IOException($"Steam 账号正在其他客户端游玩 AppID {result.PlayingAppID}，本次时长上报已中断。");
                presenceConfirmed?.TrySetException(error);
                playingInterrupted.TrySetResult(error);
                reportingPlaytime = false;
            }
        });
        manager.Subscribe<SteamFriends.PersonaStateCallback>(result => {
            if (result.FriendID == Client.SteamID && result.StatusFlags.HasFlag(EClientPersonaStateFlag.Status) && result.GameAppID == expectedAppId)
                presenceConfirmed?.TrySetResult();
        });
        callbacks = Task.Run(() => {
            while (!stop.IsCancellationRequested) manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
        });
    }

    public static IOException Failure(EResult result, string message) => result is EResult.NoConnection or EResult.ConnectFailed
        or EResult.RemoteDisconnect or EResult.Timeout or EResult.ServiceUnavailable or EResult.TryAnotherCM or EResult.Busy
        or EResult.RateLimitExceeded or EResult.IOFailure ? new SteamTemporaryResultException(result, message) : new IOException(message);

    public void ConnectionLost(Exception reason)
    {
        if (lost.TrySetResult(reason) && reportingPlaytime) playingInterrupted.TrySetResult(reason);
    }

    async Task WaitForReplyAsync(Task reply, CancellationToken token)
    {
        await Task.WhenAny(reply, Lost).WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        if (!reply.IsCompleted) throw await Lost.ConfigureAwait(false);
        await reply.ConfigureAwait(false);
    }

    public async Task<T> WaitForReplyAsync<T>(Task<T> reply, CancellationToken token = default)
    {
        await WaitForReplyAsync((Task)reply, token).ConfigureAwait(false);
        return await reply.ConfigureAwait(false);
    }

    public async Task ConnectAsync(CancellationToken token)
    {
        Client.Connect();
        await WaitForReplyAsync(connected.Task, token).ConfigureAwait(false);
    }

    public async Task LogOnAsync(SteamUser.LogOnDetails details, CancellationToken token)
    {
        Client.GetHandler<SteamUser>()!.LogOn(details);
        var response = await WaitForReplyAsync(loggedOn.Task, token).ConfigureAwait(false);
        if (response.Result != EResult.OK)
            throw Failure(response.Result, $"Steam 登录失败：{response.Result}。若登录已过期，请先退出保存的 Steam 账号，再重新登录。");
        SteamId = Client.SteamID?.ConvertToUInt64() ?? throw new SteamNetworkException("Steam 在登录确认后断开了连接。");
    }

    public async Task<KeyValue> GetAppInfoAsync(uint appId)
    {
        var apps = Client.GetHandler<SteamApps>()!;
        var tokens = await WaitForReplyAsync(apps.PICSGetAccessTokens(appId, null).ToTask()).ConfigureAwait(false);
        tokens.AppTokens.TryGetValue(appId, out ulong token);
        var product = await WaitForReplyAsync(apps.PICSGetProductInfo(new SteamApps.PICSRequest(appId, token), null).ToTask()).ConfigureAwait(false);
        var app = product.Results?.SelectMany(r => r.Apps.Values).SingleOrDefault(a => a.ID == appId);
        return app?.KeyValues ?? throw new IOException("Steam 未返回该游戏的信息，请检查 AppID 和账号的游戏许可。");
    }

    public async Task StartPlayingAsync(uint appId, int processId, CancellationToken token)
    {
        await WaitForReplyAsync(playingSessionReady.Task, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (playingSession!.PlayingBlocked)
            throw new InvalidOperationException($"Steam 账号正在其他客户端游玩 AppID {playingSession.PlayingAppID}。请先结束该游戏，或取消“上报 Steam 状态与时长”。");
        reportingPlaytime = true;
        Client.GetHandler<SteamFriends>()!.SetPersonaState(EPersonaState.Online);
        await SendPlayingAsync(appId, processId, token).ConfigureAwait(false);
    }

    async Task SendPlayingAsync(uint appId, int processId, CancellationToken token)
    {
        if (!IsOnline) throw new SteamNetworkException("Steam 已断开，无法确认游玩状态。");
        if (playingSession!.PlayingBlocked) throw new IOException("Steam 游玩会话已被其他客户端占用。");
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        expectedAppId = appId; presenceConfirmed = confirmed;
        var message = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedWithDataBlob);
        message.Body.client_os_type = (uint)EOSType.WinUnknown;
        if (appId != 0)
        {
            var game = new CMsgClientGamesPlayed.GamePlayed { game_id = appId };
            game.process_id_list.Add(new() { process_id = (uint)processId });
            message.Body.games_played.Add(game);
        }
        Client.Send(message);
        Client.GetHandler<SteamFriends>()!.RequestFriendInfo(new SteamID(SteamId), EClientPersonaStateFlag.Status | EClientPersonaStateFlag.GameExtraInfo);
        try { await WaitForReplyAsync(confirmed.Task, token).ConfigureAwait(false); }
        finally { presenceConfirmed = null; }
    }

    public async Task<bool> StopPlayingAsync()
    {
        if (!reportingPlaytime || !IsOnline) return false;
        try { await SendPlayingAsync(0, 0, CancellationToken.None).ConfigureAwait(false); return true; }
        finally { reportingPlaytime = false; }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        Client.Disconnect();
        await callbacks.ConfigureAwait(false);
        stop.Dispose();
    }
}
