using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace tgdl.Storage;

/// <summary>
/// 一个账号 = 一条登录会话。它自己有主 DC + 主 key，并且在别的 DC 上最多各留一条派生 key。
/// （取消"多 auth key 下载"之后，一个账号对一个 DC 只需要一条 key。）
/// </summary>
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Username { get; set; } = "";

    /// <summary>账号所在的 DC（登录时定的，改不了）。</summary>
    public int HomeDc { get; set; }

    /// <summary>主 DC 上的 auth key。</summary>
    public string PrimaryKey { get; set; } = "";

    /// <summary>其他 DC 上的 key：dc -> hex，每个 DC 最多一条。</summary>
    public Dictionary<string, string> Keys { get; set; } = new();

    public long AddedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long LastOkAt { get; set; }

    [JsonIgnore]
    public string ShortKey => PrimaryKey.Length >= 8 ? PrimaryKey.Substring(0, 8) : PrimaryKey;

    [JsonIgnore]
    public string Title => string.IsNullOrWhiteSpace(DisplayName)
        ? (UserId > 0 ? $"账号 {UserId}" : $"账号 {ShortKey}")
        : DisplayName;

    public string? KeyFor(int dcId)
    {
        if (dcId == HomeDc) return string.IsNullOrEmpty(PrimaryKey) ? null : PrimaryKey;
        return Keys.TryGetValue(dcId.ToString(), out var hex) && !string.IsNullOrEmpty(hex) ? hex : null;
    }

    public void SetKeyFor(int dcId, string hex)
    {
        if (dcId == HomeDc) PrimaryKey = hex;
        else Keys[dcId.ToString()] = hex;
    }

    [JsonIgnore]
    public IReadOnlyList<int> DcIds
    {
        get
        {
            var list = new List<int> { HomeDc };
            foreach (var k in Keys.Keys)
            {
                if (int.TryParse(k, out var dc) && dc != HomeDc) list.Add(dc);
            }
            list.Sort();
            return list;
        }
    }

    public Account Clone() => new()
    {
        Id = Id,
        UserId = UserId,
        DisplayName = DisplayName,
        Username = Username,
        HomeDc = HomeDc,
        PrimaryKey = PrimaryKey,
        Keys = new Dictionary<string, string>(Keys),
        AddedAt = AddedAt,
        LastOkAt = LastOkAt,
    };
}

public sealed class AccountFile
{
    public int Version { get; set; } = 2;
    public List<Account> Accounts { get; set; } = new();
}

/// <summary>data/accounts.json 的读写（含从老 authkeys.json 的一次性迁移）。</summary>
public static class AccountStore
{
    private static readonly object Gate = new();
    private static List<Account> _items = new();

    static AccountStore()
    {
        Reload();
    }

    public static void Reload()
    {
        lock (Gate)
        {
            var file = JsonIo.Read(AppPaths.AccountsFile, AppJson.Default.AccountFile);
            if (file?.Accounts is { Count: > 0 })
            {
                _items = file.Accounts;
                foreach (var a in _items) a.Keys ??= new Dictionary<string, string>();
                return;
            }
            _items = new List<Account>();
            MigrateLegacyLocked();
        }
    }

    /// <summary>老版本是"按 DC 分组的 key 池"，没有账号概念：把 home 那条当账号 1，其他 DC 各取一条。</summary>
    private static void MigrateLegacyLocked()
    {
        try
        {
            if (!File.Exists(AppPaths.LegacyAuthKeysFile)) return;
            var legacy = JsonIo.Read(AppPaths.LegacyAuthKeysFile, AppJson.Default.AuthKeyFile);
            if (legacy?.HomeDc is not int home) return;
            if (!legacy.Keys.TryGetValue(home.ToString(), out var homeKeys) || homeKeys.Count == 0) return;

            var acc = new Account
            {
                HomeDc = home,
                PrimaryKey = homeKeys[0],
                DisplayName = $"账号（DC{home}，待验证）",
            };
            foreach (var kv in legacy.Keys)
            {
                if (!int.TryParse(kv.Key, out var dc) || dc == home || kv.Value.Count == 0) continue;
                acc.Keys[dc.ToString()] = kv.Value[0];
            }
            _items = new List<Account> { acc };
            SaveLocked();
            File.Move(AppPaths.LegacyAuthKeysFile, AppPaths.LegacyAuthKeysFile + ".migrated", true);
        }
        catch
        {
            // 迁移失败就当没有老数据
        }
    }

    private static void SaveLocked()
    {
        JsonIo.Write(AppPaths.AccountsFile, new AccountFile { Accounts = _items }, AppJson.Default.AccountFile);
    }

    public static void Save()
    {
        lock (Gate) SaveLocked();
    }

    public static IReadOnlyList<Account> All()
    {
        lock (Gate) return _items.Select(a => a.Clone()).ToArray();
    }

    public static int Count
    {
        get { lock (Gate) return _items.Count; }
    }

    public static Account? Find(string id)
    {
        lock (Gate) return _items.FirstOrDefault(a => a.Id == id)?.Clone();
    }

    public static Account? FindByUserId(long userId)
    {
        if (userId == 0) return null;
        lock (Gate) return _items.FirstOrDefault(a => a.UserId == userId)?.Clone();
    }

    /// <summary>插入或按 Id 覆盖。</summary>
    public static void Upsert(Account account)
    {
        lock (Gate)
        {
            int idx = _items.FindIndex(a => a.Id == account.Id);
            if (idx >= 0) _items[idx] = account.Clone();
            else _items.Add(account.Clone());
            SaveLocked();
        }
    }

    public static void Update(string id, Action<Account> mutate)
    {
        lock (Gate)
        {
            var acc = _items.FirstOrDefault(a => a.Id == id);
            if (acc == null) return;
            mutate(acc);
            SaveLocked();
        }
    }

    public static bool Remove(string id)
    {
        lock (Gate)
        {
            int n = _items.RemoveAll(a => a.Id == id);
            if (n > 0) SaveLocked();
            return n > 0;
        }
    }

    public static void ClearAll()
    {
        lock (Gate)
        {
            _items.Clear();
            SaveLocked();
        }
    }

    /// <summary>按设置挑出要用的账号：不够就有几个用几个。</summary>
    public static IReadOnlyList<Account> SelectForUse(int want)
    {
        lock (Gate)
        {
            int n = Math.Clamp(want, 1, 20);
            return _items.Take(n).Select(a => a.Clone()).ToArray();
        }
    }
}
