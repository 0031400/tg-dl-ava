using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using tgdl.Storage;

namespace tgdl.Mtproto;

/// <summary>
/// 账号 / auth key 管理。
///
/// 导入流程：粘贴 hex + 选 DC → **立刻连上去验证**（`users.getUsers` 拿自己的身份）→
/// 验证通过才落盘，并把 key 挂到对应账号名下。
///
/// 一个账号对一个 DC 只保留一条 key：主 DC 是登录时那条，别的 DC 用 export/import 派生一条就够
/// （已经取消"多 auth key 下载"，并发靠"多个账号"而不是"多条 key"）。
/// </summary>
public static class AuthKeyManager
{
    /// <summary>导入一条 key：连接验证 → 识别账号 → 落盘。</summary>
    public static async Task<Account> ImportAndVerifyAsync(string hex, int dcId, MtpClientOptions opt,
        ConnectionPool? pool, IProgress<string>? progress, CancellationToken ct)
    {
        var key = AuthKey.FromHex(hex);
        progress?.Report($"正在连接 DC{dcId} 验证这条 key…");

        var conn = await MtpConnection.ConnectWithKeyAsync(dcId, key, ConnectionPool.KnownTimeOffset, opt, ct)
            .ConfigureAwait(false);
        MtpApi.SelfInfo self;
        try
        {
            self = await MtpApi.GetSelfAsync(conn, ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Code == 401)
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw new NeedAuthKeyException(dcId,
                $"这条 key 无效或已失效（{ex.Text}）：DC{dcId} 不认识它，或者它已经被服务端重置");
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var existing = AccountStore.FindByUserId(self.UserId);
        Account account;
        if (existing != null)
        {
            account = existing;
            account.DisplayName = self.Display;
            account.Username = self.Username ?? "";
            account.LastOkAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (account.HomeDc == dcId) account.PrimaryKey = key.Hex;
            else account.Keys[dcId.ToString()] = key.Hex;
            AccountStore.Upsert(account);
            progress?.Report($"这是已有账号「{account.Title}」，已把 DC{dcId} 的 key 挂到它名下");
        }
        else
        {
            account = new Account
            {
                UserId = self.UserId,
                DisplayName = self.Display,
                Username = self.Username ?? "",
                HomeDc = dcId,
                PrimaryKey = key.Hex,
                LastOkAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            AccountStore.Upsert(account);
            progress?.Report($"新账号「{account.Title}」（user_id {self.UserId}，主 DC{dcId}）");
        }

        if (pool != null) await pool.AdoptAsync(account, dcId, conn).ConfigureAwait(false);
        else await conn.DisposeAsync().ConfigureAwait(false);
        return account;
    }

    /// <summary>重新验证一个账号（连接 + 更新身份/时间）。</summary>
    public static async Task VerifyAsync(ConnectionPool pool, Account account, IProgress<string>? progress,
        CancellationToken ct)
    {
        var conn = await pool.GetAsync(account, account.HomeDc, ct).ConfigureAwait(false);
        var self = await MtpApi.GetSelfAsync(conn, ct).ConfigureAwait(false);
        AccountStore.Update(account.Id, a =>
        {
            a.UserId = self.UserId;
            a.DisplayName = self.Display;
            a.Username = self.Username ?? "";
            a.LastOkAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        });
        progress?.Report($"账号「{self.Display}」验证通过（主 DC{account.HomeDc}）");
    }

    /// <summary>
    /// 确保某个账号在目标 DC 上有 key：没有就用**该账号自己**的主连接导出授权、握手、导入一条。
    /// 一个 DC 只留一条。
    /// </summary>
    public static async Task<string> EnsureDcKeyAsync(ConnectionPool pool, Account account, int dcId,
        IProgress<string>? progress, CancellationToken ct)
    {
        var existing = account.KeyFor(dcId);
        if (existing != null) return existing;

        if (dcId == account.HomeDc)
            throw new NeedAuthKeyException(dcId, $"账号「{account.Title}」在它的主 DC 上没有 key");

        progress?.Report($"账号「{account.Title}」在 DC{dcId} 还没有 key，正在派生…");

        // 导出必须走这个账号自己的主连接
        var control = await pool.GetAsync(account, account.HomeDc, ct).ConfigureAwait(false);
        var (authId, bytes) = await MtpApi.ExportAuthorizationAsync(control, dcId, ct).ConfigureAwait(false);

        var blank = await MtpConnection.HandshakeAsync(dcId, pool.Options, ct).ConfigureAwait(false);
        try
        {
            await MtpApi.ImportAuthorizationAsync(blank, authId, bytes, ct).ConfigureAwait(false);
        }
        catch
        {
            await blank.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var hex = blank.Key.Hex;
        account.SetKeyFor(dcId, hex);
        AccountStore.Update(account.Id, a => a.SetKeyFor(dcId, hex));
        await pool.AdoptAsync(account, dcId, blank).ConfigureAwait(false);
        progress?.Report($"账号「{account.Title}」的 DC{dcId} key 已就绪（{hex.Substring(0, 8)}…）");
        return hex;
    }

    public static void Remove(string accountId) => AccountStore.Remove(accountId);

    public static void ClearAll() => AccountStore.ClearAll();
}
