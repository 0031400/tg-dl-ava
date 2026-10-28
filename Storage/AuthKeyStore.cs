using System;
using System.Collections.Generic;
using System.Linq;

namespace tgdl.Storage;

/// <summary>
/// 所有 auth key 的唯一存放处（data/authkeys.json），格式与 Python 版 tg-dl 完全一致，
/// 两边的文件可以互相拷贝。里面是裸的 auth key，等价于密码。
/// </summary>
public static class AuthKeyStore
{
    private const int MaxPerDc = 12;
    private static readonly object Gate = new();
    private static AuthKeyFile _file = new();

    static AuthKeyStore()
    {
        Reload();
    }

    public static void Reload()
    {
        lock (Gate)
        {
            _file = JsonIo.Read(AppPaths.AuthKeysFile, AppJson.Default.AuthKeyFile) ?? new AuthKeyFile();
            if (_file.Keys == null) _file.Keys = new Dictionary<string, List<string>>();
        }
    }

    private static void Save()
    {
        JsonIo.Write(AppPaths.AuthKeysFile, _file, AppJson.Default.AuthKeyFile);
    }

    public static int? HomeDc
    {
        get { lock (Gate) return _file.HomeDc; }
    }

    public static string? HomeIp
    {
        get { lock (Gate) return _file.HomeIp; }
    }

    public static int HomePort
    {
        get { lock (Gate) return _file.HomePort ?? 443; }
    }

    public static IReadOnlyList<string> KeysFor(int dcId)
    {
        lock (Gate)
        {
            return _file.Keys.TryGetValue(dcId.ToString(), out var list)
                ? list.ToArray()
                : Array.Empty<string>();
        }
    }

    public static int CountFor(int dcId)
    {
        lock (Gate)
        {
            return _file.Keys.TryGetValue(dcId.ToString(), out var list) ? list.Count : 0;
        }
    }

    public static int TotalCount
    {
        get { lock (Gate) return _file.Keys.Values.Sum(v => v.Count); }
    }

    public static IReadOnlyList<int> DcIds
    {
        get
        {
            lock (Gate)
            {
                return _file.Keys.Where(kv => kv.Value.Count > 0)
                    .Select(kv => int.TryParse(kv.Key, out var dc) ? dc : 0)
                    .Where(dc => dc > 0)
                    .OrderBy(dc => dc)
                    .ToArray();
            }
        }
    }

    /// <summary>主 key：home DC 的第一条。</summary>
    public static string? PrimaryKeyHex
    {
        get
        {
            lock (Gate)
            {
                if (_file.HomeDc is not int home) return null;
                if (!_file.Keys.TryGetValue(home.ToString(), out var list) || list.Count == 0) return null;
                return list[0];
            }
        }
    }

    public static void SetHome(int dcId, string? ip, int? port)
    {
        lock (Gate)
        {
            _file.HomeDc = dcId;
            if (!string.IsNullOrEmpty(ip)) _file.HomeIp = ip;
            if (port is > 0) _file.HomePort = port;
            Save();
        }
    }

    public static bool Add(int dcId, string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim().ToLowerInvariant();
        if (hex.Length != 512) return false;
        lock (Gate)
        {
            var key = dcId.ToString();
            if (!_file.Keys.TryGetValue(key, out var list))
            {
                list = new List<string>();
                _file.Keys[key] = list;
            }
            if (list.Contains(hex)) return false;
            list.Add(hex);
            if (list.Count > MaxPerDc) list.RemoveRange(0, list.Count - MaxPerDc);
            Save();
            return true;
        }
    }

    public static bool Remove(int dcId, string hex)
    {
        lock (Gate)
        {
            if (!_file.Keys.TryGetValue(dcId.ToString(), out var list)) return false;
            var removed = list.Remove(hex);
            if (removed) Save();
            return removed;
        }
    }

    public static void ClearDc(int dcId)
    {
        lock (Gate)
        {
            _file.Keys.Remove(dcId.ToString());
            Save();
        }
    }

    /// <summary>清空所有 key（等价于退出登录）。</summary>
    public static void ClearAll()
    {
        lock (Gate)
        {
            _file.Keys.Clear();
            _file.HomeDc = null;
            _file.HomeIp = null;
            _file.HomePort = null;
            Save();
        }
    }
}
