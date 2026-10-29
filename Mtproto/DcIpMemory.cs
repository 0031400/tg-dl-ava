using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using tgdl.Storage;

namespace tgdl.Mtproto;

/// <summary>
/// 记住每个 DC 上次连通的 IP（data/dc-ip.json）。
/// 下次先试这个，失败再退回内置地址表 —— 免得每次都要一个个 IP 试过去。
/// </summary>
public static class DcIpMemory
{
    private static readonly object Gate = new();
    private static Dictionary<string, string> _map = new();

    static DcIpMemory()
    {
        Reload();
    }

    public static void Reload()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(AppPaths.DcIpFile))
                {
                    _map = new Dictionary<string, string>();
                    return;
                }
                var text = File.ReadAllText(AppPaths.DcIpFile);
                _map = JsonSerializer.Deserialize(text, AppJson.Default.DictionaryStringString)
                       ?? new Dictionary<string, string>();
            }
            catch
            {
                _map = new Dictionary<string, string>();
            }
        }
    }

    public static string? Get(int dcId)
    {
        lock (Gate) return _map.TryGetValue(dcId.ToString(), out var ip) ? ip : null;
    }

    /// <summary>记下"这台 DC 上次是用哪个 IP 连通的"。</summary>
    public static void Remember(int dcId, string ip)
    {
        if (string.IsNullOrEmpty(ip)) return;
        lock (Gate)
        {
            if (_map.TryGetValue(dcId.ToString(), out var old) && old == ip) return;
            _map[dcId.ToString()] = ip;
            try
            {
                var text = JsonSerializer.Serialize(_map, AppJson.Default.DictionaryStringString);
                AppPaths.WriteAllTextAtomic(AppPaths.DcIpFile, text);
            }
            catch
            {
                // 记不住就算了
            }
        }
    }

    /// <summary>候选 IP：上次成功的排最前，然后才是内置表。</summary>
    public static IReadOnlyList<string> Candidates(int dcId)
    {
        var list = new List<string>(4);
        var remembered = Get(dcId);
        if (!string.IsNullOrEmpty(remembered)) list.Add(remembered);
        foreach (var ip in DcOptions.Addresses(dcId))
        {
            if (!list.Contains(ip)) list.Add(ip);
        }
        return list;
    }

    public static IReadOnlyDictionary<int, string> Snapshot()
    {
        lock (Gate)
        {
            var result = new Dictionary<int, string>();
            foreach (var kv in _map)
            {
                if (int.TryParse(kv.Key, out var dc)) result[dc] = kv.Value;
            }
            return result;
        }
    }
}
