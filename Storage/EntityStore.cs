using System;
using System.Collections.Generic;
using System.Linq;
using tgdl.Mtproto;

namespace tgdl.Storage;

/// <summary>
/// chat_id → (kind, access_hash) 的本地缓存。
/// 频道/超级群必须有 access_hash 才能构造 inputPeer，而 access_hash 只能从
/// getDialogs 里拿到，所以这里把同步过的会话存下来。
/// </summary>
public static class EntityStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, EntityRecord> _map = new();

    static EntityStore()
    {
        Reload();
    }

    public static void Reload()
    {
        lock (Gate)
        {
            _map = JsonIo.Read(AppPaths.EntitiesFile, AppJson.Default.DictionaryStringEntityRecord)
                   ?? new Dictionary<string, EntityRecord>();
        }
    }

    public static int Count
    {
        get { lock (Gate) return _map.Count; }
    }

    private static void Save()
    {
        Dictionary<string, EntityRecord> copy;
        lock (Gate) copy = new Dictionary<string, EntityRecord>(_map);
        JsonIo.Write(AppPaths.EntitiesFile, copy, AppJson.Default.DictionaryStringEntityRecord);
    }

    /// <summary>access_hash 是"账号 × 会话"的，不同账号不能混用，所以 key 里带上账号。</summary>
    private static string KeyOf(string accountId, long chatId) => accountId + "|" + chatId;

    public static void Put(string accountId, ChatEntity entity)
    {
        var chatId = PeerRef.ToChatId(entity.Peer.Kind, entity.Peer.Id);
        lock (Gate)
        {
            _map[KeyOf(accountId, chatId)] = new EntityRecord
            {
                Kind = (int)entity.Peer.Kind,
                Id = entity.Peer.Id,
                AccessHash = entity.Peer.AccessHash,
                Title = entity.Title,
                Username = entity.Username,
            };
        }
    }

    public static void PutRange(string accountId, IEnumerable<ChatEntity> entities)
    {
        lock (Gate)
        {
            foreach (var e in entities)
            {
                var chatId = PeerRef.ToChatId(e.Peer.Kind, e.Peer.Id);
                _map[KeyOf(accountId, chatId)] = new EntityRecord
                {
                    Kind = (int)e.Peer.Kind,
                    Id = e.Peer.Id,
                    AccessHash = e.Peer.AccessHash,
                    Title = e.Title,
                    Username = e.Username,
                };
            }
        }
        Save();
    }

    public static void SaveAll() => Save();

    public static EntityRecord? Get(string accountId, long chatId)
    {
        lock (Gate) return _map.TryGetValue(KeyOf(accountId, chatId), out var r) ? r : null;
    }

    /// <summary>
    /// 把 chat_id 解析成可以发请求的 PeerRef。缓存里没有 access_hash 就返回 null，
    /// 调用方需要先同步一次会话列表。
    /// </summary>
    public static PeerRef? Resolve(string accountId, long chatId)
    {
        var (kind, id) = PeerRef.Classify(chatId);
        var rec = Get(accountId, chatId);
        if (kind == PeerKind.Chat)
            return new PeerRef(PeerKind.Chat, id, 0);
        if (rec == null || rec.AccessHash == 0)
            return null;
        return new PeerRef(kind, id, rec.AccessHash);
    }

    /// <summary>从任意账号的记录里找这个会话的 username（用来给别的账号反查 access_hash）。</summary>
    public static string? FindUsername(long chatId)
    {
        lock (Gate)
        {
            foreach (var rec in _map.Values)
            {
                if (string.IsNullOrWhiteSpace(rec.Username)) continue;
                if (PeerRef.ToChatId((PeerKind)rec.Kind, rec.Id) == chatId) return rec.Username;
            }
            return null;
        }
    }

    public static IReadOnlyList<EntityRecord> All()
    {
        lock (Gate) return _map.Values.ToList();
    }
}
