using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using tgdl.Storage;

namespace tgdl.Mtproto;

/// <summary>
/// auth key 管理：导入、派生（exportAuthorization + 新 DH + importAuthorization）、删除、清空。
///
/// 两个坑（Python 版实测出来的）：
/// 1. importAuthorization 不能并发，服务端只放行一条；
/// 2. DH 握手可以并发，而且占绝大部分时间。
/// 所以流程是「并行 DH + 串行导出/导入」。
///
/// 目标 DC == 主 DC 时不能从自己导出授权，这里会「借道」另一台已经有授权的 DC 去导出；
/// 服务端不接受就记住并回退（界面会提示只能用手上那几条 key）。
/// </summary>
public static class AuthKeyManager
{
    /// <summary>借道派生被服务端拒绝过，就不再反复浪费握手时间。</summary>
    private static bool _bridgeUnsupported;

    public static bool BridgeUnsupported => _bridgeUnsupported;

    public static void Import(string hex, int dcId, bool setHome)
    {
        var key = AuthKey.FromHex(hex);
        if (!AuthKeyStore.Add(dcId, key.Hex))
            throw new MtpException($"这条 key 已经在 DC{dcId} 里了");
        if (setHome || AuthKeyStore.HomeDc == null)
        {
            var ip = DcOptions.Addresses(dcId).FirstOrDefault();
            AuthKeyStore.SetHome(dcId, ip, DcOptions.Port);
        }
    }

    /// <summary>从主 key 派生 count 条目标 DC 的独立 auth key。</summary>
    public static async Task<List<string>> DeriveAsync(ConnectionPool pool, int targetDc, int count,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (count <= 0) return new List<string>();
        var home = AuthKeyStore.HomeDc
                   ?? throw new NeedAuthKeyException(null, "还没有主 auth key，无法派生。请先在「Auth Key」页导入一条。");

        MtpConnection control;
        if (home != targetDc)
        {
            control = await pool.GetControlAsync(ct).ConfigureAwait(false);
        }
        else
        {
            if (_bridgeUnsupported)
                throw new MtpException($"DC{targetDc} 是主 DC，之前尝试过借道其他 DC 导出也被拒绝，不再重试");
            control = await GetBridgeConnectionAsync(pool, targetDc, progress, ct).ConfigureAwait(false);
        }

        var opt = pool.Options;
        progress?.Report($"并行对 DC{targetDc} 做 {count} 次 DH 握手…");

        // 1) 并行握手
        var blankTasks = Enumerable.Range(0, count)
            .Select(_ => MtpConnection.HandshakeAsync(targetDc, opt, ct))
            .ToList();
        var blanks = new List<MtpConnection>();
        foreach (var t in blankTasks)
        {
            try { blanks.Add(await t.ConfigureAwait(false)); }
            catch (Exception ex) { progress?.Report($"握手失败：{ex.Message}"); }
        }
        if (blanks.Count == 0) throw new MtpException("所有 DH 握手都失败了（多半是被限流，过一会儿再试）");

        // 2) 串行导出 + 导入
        var created = new List<string>();
        int index = 0;
        foreach (var blank in blanks)
        {
            index++;
            try
            {
                var (id, bytes) = await MtpApi.ExportAuthorizationAsync(control, targetDc, ct).ConfigureAwait(false);
                await MtpApi.ImportAuthorizationAsync(blank, id, bytes, ct).ConfigureAwait(false);
                var hex = blank.Key.Hex;
                AuthKeyStore.Add(targetDc, hex);
                await pool.AdoptAsync(targetDc, hex, blank).ConfigureAwait(false);
                created.Add(hex);
                progress?.Report($"DC{targetDc} 第 {index}/{blanks.Count} 条 key 创建成功 {hex.Substring(0, 8)}");
            }
            catch (Exception ex)
            {
                if (home == targetDc && ex is RpcException)
                {
                    _bridgeUnsupported = true;
                    progress?.Report($"服务端拒绝从其他 DC 导出主 DC 的授权（{ex.Message}）——" +
                                     "只能用手上已有的 key，不再尝试借道");
                }
                progress?.Report($"DC{targetDc} 第 {index}/{blanks.Count} 条创建失败：{ex.Message}");
                await blank.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (created.Count == 0) throw new MtpException($"DC{targetDc} 一条 key 都没创建成功");
        return created;
    }

    /// <summary>找一台"不是目标 DC"的已授权 DC 作为导出的出发点。</summary>
    private static async Task<MtpConnection> GetBridgeConnectionAsync(ConnectionPool pool, int targetDc,
        IProgress<string>? progress, CancellationToken ct)
    {
        var bridgeDc = PickBridgeDc(targetDc);
        if (AuthKeyStore.CountFor(bridgeDc) == 0)
        {
            progress?.Report($"主 DC{targetDc} 不能从自己导出授权，先借道 DC{bridgeDc}：给它派生 1 条 key…");
            await DeriveAsync(pool, bridgeDc, 1, progress, ct).ConfigureAwait(false);
        }
        var keys = AuthKeyStore.KeysFor(bridgeDc);
        if (keys.Count == 0) throw new MtpException($"中转 DC{bridgeDc} 上没有可用 key");
        progress?.Report($"借道 DC{bridgeDc} 导出 DC{targetDc} 的授权…");
        return await pool.GetAsync(bridgeDc, keys[0], ct).ConfigureAwait(false);
    }

    private static int PickBridgeDc(int targetDc)
    {
        foreach (var dc in AuthKeyStore.DcIds)
        {
            if (dc != targetDc && DcOptions.IsKnown(dc)) return dc;
        }
        foreach (var dc in new[] { 4, 5, 2, 1, 3 })
        {
            if (dc != targetDc) return dc;
        }
        throw new MtpException("找不到可用的中转 DC");
    }
}
