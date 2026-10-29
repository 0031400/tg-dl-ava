using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using tgdl.Storage;

namespace tgdl.Mtproto;

/// <summary>
/// 连接池：按 (账号, DC) 缓存长连接，一个账号在一个 DC 上只占一条 TCP。
///
/// 关键点：**派生 key 必须挂在自己账号名下**。拿 A 账号导出的 key 去顶 B 账号的连接，
/// 看起来是 N 个账号并行，实际只有一个账号在干活（限流额度也只有一份）。
/// </summary>
public sealed class ConnectionPool : IAsyncDisposable
{
    private readonly Dictionary<string, MtpConnection> _conns = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MtpClientOptions _opt = new();
    private bool _disposed;

    public static long KnownTimeOffset;

    public MtpClientOptions Options => _opt;

    public void ApplySettings(AppSettings settings)
    {
        _opt.ApiId = settings.ApiId;
        _opt.ApiHash = settings.ApiHash;
        _opt.ProxyMode = settings.ProxyMode;
        _opt.ProxyKind = settings.ProxyKind;
        _opt.ProxyHost = settings.ProxyHost;
        _opt.ProxyPort = settings.ProxyPort;
        _opt.ProxyUsername = settings.ProxyUsername;
        _opt.ProxyPassword = settings.ProxyPassword;
        _opt.ProxyVlessLink = settings.ProxyVlessLink;
    }

    private static string KeyOf(string accountId, int dcId) => accountId + ":" + dcId;

    public int ConnectionCount
    {
        get { lock (_conns) return _conns.Count; }
    }

    /// <summary>拿一条"某账号在某 DC"的连接（没有就新建）。</summary>
    public async Task<MtpConnection> GetAsync(Account account, int dcId, CancellationToken ct)
    {
        var id = KeyOf(account.Id, dcId);
        lock (_conns)
        {
            if (_conns.TryGetValue(id, out var existing) && !existing.IsDead)
                return existing;
        }

        var hex = account.KeyFor(dcId)
                  ?? throw new NeedAuthKeyException(dcId, $"账号「{account.Title}」在 DC{dcId} 上没有 key");
        var key = AuthKey.FromHex(hex);
        var conn = await MtpConnection.ConnectWithKeyAsync(dcId, key, KnownTimeOffset, _opt, ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_conns.TryGetValue(id, out var old) && !ReferenceEquals(old, conn))
            {
                if (!old.IsDead)
                {
                    await conn.DisposeAsync().ConfigureAwait(false);
                    return old;
                }
                await old.DisposeAsync().ConfigureAwait(false);
            }
            _conns[id] = conn;
            return conn;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>把一条已握手/已授权的连接收进池子。</summary>
    public async Task AdoptAsync(Account account, int dcId, MtpConnection conn)
    {
        var id = KeyOf(account.Id, dcId);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_conns.TryGetValue(id, out var old) && !ReferenceEquals(old, conn))
            {
                if (!old.IsDead)
                {
                    await conn.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                await old.DisposeAsync().ConfigureAwait(false);
            }
            _conns[id] = conn;
        }
        finally
        {
            _gate.Release();
        }
    }

    public sealed record AccountConnection(Account Account, MtpConnection Conn);

    /// <summary>为下载准备连接：每个账号一条。死掉的连接会被自动换成新的。</summary>
    public async Task<List<AccountConnection>> GetDownloadConnectionsAsync(IReadOnlyList<Account> accounts, int dcId,
        IProgress<string>? progress, CancellationToken ct)
    {
        var result = new List<AccountConnection>();
        var tasks = new List<(Account Account, Task<MtpConnection> Task)>();
        foreach (var acc in accounts)
        {
            if (acc.KeyFor(dcId) == null) continue;
            tasks.Add((acc, GetAsync(acc, dcId, ct)));
        }
        foreach (var (acc, task) in tasks)
        {
            try { result.Add(new AccountConnection(acc, await task.ConfigureAwait(false))); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { progress?.Report($"账号「{acc.Title}」连接失败：{ex.Message}"); }
        }
        if (result.Count == 0)
            throw new NeedAuthKeyException(dcId, $"没有任何账号能在 DC{dcId} 上建立连接");
        return result;
    }

    public async Task DropAccountAsync(string accountId)
    {
        List<MtpConnection> list = new();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var key in _conns.Keys.Where(k => k.StartsWith(accountId + ":", StringComparison.Ordinal)).ToList())
            {
                if (_conns.Remove(key, out var c)) list.Add(c);
            }
        }
        finally
        {
            _gate.Release();
        }
        foreach (var c in list) await c.DisposeAsync().ConfigureAwait(false);
    }

    public async Task CloseAllAsync()
    {
        List<MtpConnection> list;
        lock (_conns)
        {
            list = _conns.Values.ToList();
            _conns.Clear();
        }
        foreach (var c in list)
        {
            try { await c.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CloseAllAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
