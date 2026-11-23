using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using SteamKit2;
using SteamKit2.Authentication;

namespace ColdClientManager;

public sealed record SteamLogin(string AccountName, string RefreshToken);

public sealed class SteamSession : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly object gate = new();
    readonly SteamUser.LogOnDetails login;
    readonly Task recovery;
    volatile SteamConnection current;
    TaskCompletionSource<SteamConnection> next = new(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationTokenSource? playingStop;
    Task? playing;
    SteamConnection? playingOn;
    TaskCompletionSource<Exception> playingInterrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SteamClient Client => current.Client;
    public SteamStatsHandler Stats => current.Stats;
    public ulong SteamId { get; }
    public string AccountName => login.Username!;
    public Task<Exception> PlayingInterrupted => playingInterrupted.Task;
    public IProgress<string>? ConnectionProgress { get; set; }

    SteamSession(SteamConnection connection, SteamUser.LogOnDetails login)
    {
        current = connection;
        SteamId = connection.SteamId;
        this.login = login;
        recovery = RecoverAsync(connection, stop.Token);
    }

    public static string LoginPath(string dataRoot) => Path.Combine(dataRoot, "steam", "login.json");

    public static void SaveLogin(string dataRoot, SteamLogin login) => Storage.Write(LoginPath(dataRoot),
        ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(login), null, DataProtectionScope.CurrentUser));

    public static SteamLogin ReadLogin(string dataRoot) => JsonSerializer.Deserialize<SteamLogin>(
        ProtectedData.Unprotect(Storage.Read<byte[]>(LoginPath(dataRoot)), null, DataProtectionScope.CurrentUser))!;

    public static async Task<SteamSession> ConnectAsync(string dataRoot, AuthSessionDetails? authentication = null, CancellationToken cancellationToken = default)
    {
        var connection = new SteamConnection(SteamConfiguration.Create(b => b.WithProtocolTypes(ProtocolTypes.WebSocket)));
        try
        {
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
            SteamLogin saved;
            bool reuseLogin = File.Exists(LoginPath(dataRoot));
            if (reuseLogin) saved = ReadLogin(dataRoot);
            else
            {
                if (authentication == null) throw new InvalidOperationException("请先使用“Steam 登录”登录账号。");
                authentication.IsPersistentSession = true;
                authentication.DeviceFriendlyName = "ColdClientManager";
                var auth = await connection.Client.Authentication.BeginAuthSessionViaCredentialsAsync(authentication)
                    .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                var result = await auth.PollingWaitForResultAsync(cancellationToken).ConfigureAwait(false);
                saved = new(result.AccountName, result.RefreshToken);
            }
            var details = new SteamUser.LogOnDetails { Username = saved.AccountName, AccessToken = saved.RefreshToken,
                ShouldRememberPassword = true, LoginID = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue) };
            await connection.LogOnAsync(details, cancellationToken).ConfigureAwait(false);
            if (!reuseLogin) SaveLogin(dataRoot, saved);
            return new SteamSession(connection, details);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    async Task RecoverAsync(SteamConnection first, CancellationToken token)
    {
        var configuration = first.Client.Configuration;
        SteamConnection? owned = first;
        try
        {
            while (true)
            {
                var reason = await owned.Lost.WaitAsync(token).ConfigureAwait(false);
                await owned.DisposeAsync().ConfigureAwait(false);
                owned = null;
                if (reason is not (SteamNetworkException or SteamTemporaryResultException)) throw reason;
                ConnectionProgress?.Report("Steam 连接已中断，正在自动重连。");
                for (int seconds = 1; owned == null; seconds = Math.Min(seconds * 2, 30))
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
                    var attempt = new SteamConnection(configuration);
                    try
                    {
                        await attempt.ConnectAsync(token).ConfigureAwait(false);
                        await attempt.LogOnAsync(login, token).ConfigureAwait(false);
                        if (attempt.SteamId != SteamId) throw new InvalidDataException("重新登录的 Steam 账号与本次会话不一致。");
                        owned = attempt;
                    }
                    catch (Exception ex) when (ex is SteamNetworkException or SteamTemporaryResultException or TimeoutException)
                    {
                        await attempt.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        await attempt.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }
                lock (gate)
                {
                    current = owned;
                    next.TrySetResult(owned);
                    next = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                ConnectionProgress?.Report("Steam 已重新登录。");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { next.TrySetCanceled(token); }
        catch (Exception ex)
        {
            next.TrySetException(ex);
            ConnectionProgress?.Report("Steam 无法恢复会话：" + ex.Message);
        }
        finally { if (owned != null) await owned.DisposeAsync().ConfigureAwait(false); }
    }

    static bool RetryRequest(Exception error, SteamConnection connection) => error is SteamNetworkException or TimeoutException or TaskCanceledException
        || error is AsyncJobFailedException && !connection.IsOnline
        || error is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded };

    // Retry a whole operation on one authenticated connection. Callers that write first read the server's current state.
    internal async Task<T> RunConnectedAsync<T>(Func<SteamConnection, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        var token = cancel.Token;
        int serviceDelay = 1;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task<SteamConnection> ready;
            lock (gate) ready = current.IsOnline ? Task.FromResult(current) : next.Task;
            var connection = await ready.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            try { return await operation(connection); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || stop.IsCancellationRequested) { throw; }
            catch (SteamTemporaryResultException ex)
            {
                // Steam answered this request. A new login would restart the session even though the transport works.
                if (!connection.IsOnline) continue;
                if (serviceDelay == 1) ConnectionProgress?.Report($"Steam 请求暂未完成（{ex.Result}），将自动重试。");
                await Task.WhenAny(connection.Lost, Task.Delay(TimeSpan.FromSeconds(serviceDelay), token));
                serviceDelay = Math.Min(serviceDelay * 2, 30);
            }
            catch (Exception ex) when (RetryRequest(ex, connection))
            {
                token.ThrowIfCancellationRequested();
                if (connection.IsOnline) connection.ConnectionLost(new SteamNetworkException("Steam 请求的连接已中断。"));
            }
        }
    }

    public static uint ValidateProfile(GameProfile profile, ulong steamId)
    {
        if (!uint.TryParse(profile.AppId, out uint appId) || appId == 0) throw new InvalidDataException("请填写有效的 Steam AppID。");
        if (!ulong.TryParse(profile.SteamId, out ulong account) || account != steamId)
            throw new InvalidDataException("存档账号 ID 必须与当前登录的 Steam 账号一致。");
        return appId;
    }

    public Task<KeyValue> GetAppInfoAsync(uint appId, CancellationToken token = default) =>
        RunConnectedAsync(c => c.GetAppInfoAsync(appId), token);

    public Task StartPlayingAsync(GameProfile profile, int processId, CancellationToken cancellationToken = default)
    {
        uint appId = ValidateProfile(profile, SteamId);
        if (playing != null) throw new InvalidOperationException("已有游戏正在上报游玩状态。");
        playingStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playingInterrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        playing = KeepPlayingAsync(appId, processId, started, playingStop.Token);
        return started.Task;
    }

    async Task KeepPlayingAsync(uint appId, int processId, TaskCompletionSource started, CancellationToken token)
    {
        try
        {
            await RunConnectedAsync<bool>(async connection => {
                playingOn = connection;
                await connection.StartPlayingAsync(appId, processId, token).ConfigureAwait(false);
                if (!started.TrySetResult()) ConnectionProgress?.Report("Steam 已确认恢复正在游玩，继续上报时长。");
                throw await connection.PlayingInterrupted.WaitAsync(token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { started.TrySetCanceled(token); }
        catch (Exception ex) { started.TrySetException(ex); playingInterrupted.TrySetResult(ex); }
    }

    public async Task<bool> StopPlayingAsync()
    {
        if (playing == null) return false;
        playingStop!.Cancel();
        await playing.ConfigureAwait(false);
        playing = null;
        playingStop.Dispose();
        var connection = playingOn;
        playingOn = null;
        if (connection == null) return false;
        try { return await connection.StopPlayingAsync().ConfigureAwait(false); }
        catch (Exception ex) when (RetryRequest(ex, connection))
        {
            if (connection.IsOnline) connection.ConnectionLost(new SteamNetworkException("Steam 游玩状态连接已中断。"));
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopPlayingAsync().ConfigureAwait(false); }
        finally
        {
            stop.Cancel();
            await recovery.ConfigureAwait(false);
            stop.Dispose();
        }
    }
}
