using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using tgdl.Storage;

namespace tgdl.Mtproto;

/// <summary>
/// 连接池：按 (DC, auth key) 缓存长连接。
/// 下载时反复复用，省掉每次建连/DH 的开销（README 里「服务态」那部分）。
/// </summary>
public sealed class ConnectionPool : IAsyncDisposable
{
    private readonly Dictionary<string, MtpConnection> _conns = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MtpClientOptions _opt = new();
    private bool _disposed;

    /// <summary>进程内已知的服务器时间偏移（秒），由 DH / 收到的消息更新。</summary>
    public static long KnownTimeOffset;

    public static long HomeDc => AuthKeyStore.HomeDc ?? 0;

    public MtpClientOptions Options => _opt;

    public void ApplySettings(AppSettings settings)
    {
        _opt.ApiId = settings.ApiId;
        _opt.ApiHash = settings.ApiHash;
    }

    private static string KeyOf(int dcId, string keyHex) => dcId + ":" + keyHex;

    public int ConnectionCount
    {
        get { lock (_conns) return _conns.Count; }
    }

    public IReadOnlyList<string> ConnectionLabels
    {
        get { lock (_conns) return _conns.Keys.ToList(); }
    }

    /// <summary>拿一条指定 DC + 指定 key 的连接（没有就新建）。</summary>
    public async Task<MtpConnection> GetAsync(int dcId, string keyHex, CancellationToken ct)
    {
        var id = KeyOf(dcId, keyHex);
        lock (_conns)
        {
            if (_conns.TryGetValue(id, out var existing) && !existing.IsDead)
                return existing;
        }

        var key = AuthKey.FromHex(keyHex);
        var conn = await MtpConnection.ConnectWithKeyAsync(dcId, key, KnownTimeOffset, _opt, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_conns.TryGetValue(id, out var old) && !ReferenceEquals(old, conn))
            {
                if (!old.IsDead)
                {
                    // 别人先建好了，用别人的
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

    /// <summary>控制连接：home DC + 主 key。</summary>
    public Task<MtpConnection> GetControlAsync(CancellationToken ct)
    {
        var home = AuthKeyStore.HomeDc ?? throw new MtpException("还没有任何 auth key，先导入一个");
        var primary = AuthKeyStore.PrimaryKeyHex ?? throw new MtpException("home DC 上没有可用的 auth key");
        return GetAsync(home, primary, ct);
    }

    /// <summary>把一条已经握手/授权好的连接收进池子（派生 key 之后用）。</summary>
    public async Task AdoptAsync(int dcId, string keyHex, MtpConnection conn)
    {
        var id = KeyOf(dcId, keyHex);
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

    public async Task DropAsync(int dcId, string keyHex)
    {
        var id = KeyOf(dcId, keyHex);
        MtpConnection? conn = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_conns.Remove(id, out var c)) conn = c;
        }
        finally
        {
            _gate.Release();
        }
        if (conn != null) await conn.DisposeAsync().ConfigureAwait(false);
    }

    public async Task DropDcAsync(int dcId)
    {
        List<MtpConnection> list = new();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var key in _conns.Keys.Where(k => k.StartsWith(dcId + ":", StringComparison.Ordinal)).ToList())
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

    /// <summary>
    /// 为下载准备最多 want 条连接（每条一个独立 auth key）。
    /// 优先用缓存里的 key；不够就现场派生。
    /// </summary>
    public async Task<List<MtpConnection>> GetDownloadConnectionsAsync(int dcId, int want, IProgress<string>? progress,
        CancellationToken ct)
    {
        want = Math.Clamp(want, 1, 12);
        var keys = AuthKeyStore.KeysFor(dcId).ToList();

        string? deriveError = null;
        if (keys.Count < want)
        {
            try
            {
                var derived = await AuthKeyManager.DeriveAsync(this, dcId, want - keys.Count, progress, ct).ConfigureAwait(false);
                keys = AuthKeyStore.KeysFor(dcId).ToList();
                if (derived.Count > 0)
                    progress?.Report($"DC{dcId} 新派生 {derived.Count} 条 auth key");
            }
            catch (Exception ex)
            {
                deriveError = ex.Message;
                progress?.Report($"派生 auth key 失败（继续用现有的 {keys.Count} 条）：{ex.Message}");
            }
        }

        if (keys.Count == 0)
            throw new NeedAuthKeyException(dcId,
                $"DC{dcId} 上没有可用的 auth key" +
                (deriveError == null ? "" : $"，自动派生也失败了（{deriveError}）") +
                $"。请到「Auth Key」页导入一条 DC{dcId} 的 key。");

        var use = keys.Take(want).ToList();
        var tasks = use.Select(k => GetAsync(dcId, k, ct)).ToList();
        var result = new List<MtpConnection>();
        foreach (var t in tasks)
        {
            try { result.Add(await t.ConfigureAwait(false)); }
            catch (Exception ex) { progress?.Report($"连接失败：{ex.Message}"); }
        }
        if (result.Count == 0) throw new MtpException($"DC{dcId} 连接全部失败");
        return result;
    }

    /// <summary>关掉所有连接，但池子本身还能继续用（对应界面上的「停止」）。</summary>
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
