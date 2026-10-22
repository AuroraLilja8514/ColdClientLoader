using System.Security.Cryptography;
using System.Text.Json;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace ColdClientManager;

public sealed record SteamLogin(string AccountName, string RefreshToken);

public sealed class SteamSession : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly Task callbacks;
    readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<SteamUser.LoggedOnCallback> loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource playingSessionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<Exception> playingInterrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile SteamUser.PlayingSessionStateCallback? playingSession;
    volatile TaskCompletionSource? presenceConfirmed;
    volatile uint expectedAppId;
    volatile bool reportingPlaytime;
    public SteamClient Client { get; }
    public SteamStatsHandler Stats { get; } = new();
    public ulong SteamId => Client.SteamID!.ConvertToUInt64();
    public string AccountName { get; private set; } = "";
    public Task<Exception> PlayingInterrupted => playingInterrupted.Task;
    public static string LoginPath(string dataRoot) => Path.Combine(dataRoot, "steam", "login.json");

    SteamSession()
    {
        Client = new SteamClient(SteamConfiguration.Create(b => b.WithProtocolTypes(ProtocolTypes.WebSocket)));
        Client.AddHandler(Stats);
        var manager = new CallbackManager(Client);
        manager.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        manager.Subscribe<SteamClient.DisconnectedCallback>(_ => {
            connected.TrySetException(new IOException("Steam 连接已断开。"));
            loggedOn.TrySetException(new IOException("Steam 登录期间连接已断开。"));
            FailPlaying(new IOException("Steam 连接已断开，游玩状态和时长上报已中断。"));
        });
        manager.Subscribe<SteamUser.LoggedOnCallback>(result => loggedOn.TrySetResult(result));
        manager.Subscribe<SteamUser.LoggedOffCallback>(result => FailPlaying(new IOException($"Steam 会话已退出：{result.Result}。游玩状态和时长上报已中断。")));
        manager.Subscribe<SteamUser.PlayingSessionStateCallback>(result => {
            playingSession = result; playingSessionReady.TrySetResult();
            if (result.PlayingBlocked && reportingPlaytime)
                FailPlaying(new IOException($"Steam 账号正在其他客户端游玩 AppID {result.PlayingAppID}，本次时长上报已中断。"));
        });
        manager.Subscribe<SteamFriends.PersonaStateCallback>(result => {
            if (result.FriendID == Client.SteamID && result.StatusFlags.HasFlag(EClientPersonaStateFlag.Status) && result.GameAppID == expectedAppId)
                presenceConfirmed?.TrySetResult();
        });
        callbacks = Task.Run(() => {
            while (!stop.IsCancellationRequested) manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
        });
    }

    void FailPlaying(Exception error)
    {
        playingSessionReady.TrySetException(error);
        presenceConfirmed?.TrySetException(error);
        if (reportingPlaytime) playingInterrupted.TrySetResult(error);
    }

    public static uint ValidateProfile(GameProfile profile, ulong steamId)
    {
        if (!uint.TryParse(profile.AppId, out uint appId) || appId == 0) throw new InvalidDataException("请填写有效的 Steam AppID。");
        if (!ulong.TryParse(profile.SteamId, out ulong account) || account != steamId)
            throw new InvalidDataException("存档账号 ID 必须与当前登录的 Steam 账号一致。");
        return appId;
    }

    public async Task StartPlayingAsync(GameProfile profile, int processId)
    {
        uint appId = ValidateProfile(profile, SteamId);
        await playingSessionReady.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (playingSession!.PlayingBlocked)
            throw new InvalidOperationException($"Steam 账号正在其他客户端游玩 AppID {playingSession.PlayingAppID}。请先结束该游戏，或取消“上报 Steam 状态与时长”。");
        reportingPlaytime = true;
        Client.GetHandler<SteamFriends>()!.SetPersonaState(EPersonaState.Online);
        await SendPlayingAsync(appId, processId).ConfigureAwait(false);
    }

    async Task SendPlayingAsync(uint appId, int processId)
    {
        if (!Client.IsConnected) throw new IOException("Steam 已断开，无法确认游玩状态。");
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
        Client.GetHandler<SteamFriends>()!.RequestFriendInfo(Client.SteamID!, EClientPersonaStateFlag.Status | EClientPersonaStateFlag.GameExtraInfo);
        try { await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        finally { presenceConfirmed = null; }
    }

    public async Task StopPlayingAsync()
    {
        if (!reportingPlaytime) return;
        try { await SendPlayingAsync(0, 0).ConfigureAwait(false); }
        finally { reportingPlaytime = false; }
    }

    public static async Task<SteamSession> ConnectAsync(string dataRoot, AuthSessionDetails? authentication = null, CancellationToken cancellationToken = default)
    {
        var session = new SteamSession();
        try
        {
            session.Client.Connect();
            await session.connected.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            string path = LoginPath(dataRoot);
            SteamLogin login;
            if (File.Exists(path)) login = ReadLogin(dataRoot);
            else
            {
                if (authentication == null) throw new InvalidOperationException("请先使用“Steam 登录”登录账号。");
                authentication.IsPersistentSession = true;
                authentication.DeviceFriendlyName = "ColdClientManager";
                var auth = await session.Client.Authentication.BeginAuthSessionViaCredentialsAsync(authentication)
                    .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                var result = await auth.PollingWaitForResultAsync(cancellationToken).ConfigureAwait(false);
                login = new(result.AccountName, result.RefreshToken);
            }
            session.Client.GetHandler<SteamUser>()!.LogOn(new SteamUser.LogOnDetails {
                Username = login.AccountName, AccessToken = login.RefreshToken, ShouldRememberPassword = true,
                LoginID = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue)
            });
            var loggedOn = await session.loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (loggedOn.Result != EResult.OK)
                throw new IOException($"Steam 登录失败：{loggedOn.Result}。若登录已过期，请先退出保存的 Steam 账号，再重新登录。");
            session.AccountName = login.AccountName;
            SaveLogin(dataRoot, login);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static void SaveLogin(string dataRoot, SteamLogin login) => Storage.Write(LoginPath(dataRoot),
        ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(login), null, DataProtectionScope.CurrentUser));

    public static SteamLogin ReadLogin(string dataRoot) => JsonSerializer.Deserialize<SteamLogin>(
        ProtectedData.Unprotect(Storage.Read<byte[]>(LoginPath(dataRoot)), null, DataProtectionScope.CurrentUser))!;

    public async ValueTask DisposeAsync()
    {
        try { await StopPlayingAsync().ConfigureAwait(false); }
        finally
        {
            Client.Disconnect();
            stop.Cancel();
            await callbacks.ConfigureAwait(false);
            stop.Dispose();
        }
    }
}
