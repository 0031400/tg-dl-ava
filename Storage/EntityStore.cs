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

    public static void Put(ChatEntity entity)
    {
        var chatId = PeerRef.ToChatId(entity.Peer.Kind, entity.Peer.Id);
        lock (Gate)
        {
            _map[chatId.ToString()] = new EntityRecord
            {
                Kind = (int)entity.Peer.Kind,
                Id = entity.Peer.Id,
                AccessHash = entity.Peer.AccessHash,
                Title = entity.Title,
                Username = entity.Username,
            };
        }
    }

    public static void PutRange(IEnumerable<ChatEntity> entities)
    {
        lock (Gate)
        {
            foreach (var e in entities)
            {
                var chatId = PeerRef.ToChatId(e.Peer.Kind, e.Peer.Id);
                _map[chatId.ToString()] = new EntityRecord
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

    public static EntityRecord? Get(long chatId)
    {
        lock (Gate) return _map.TryGetValue(chatId.ToString(), out var r) ? r : null;
    }

    public static string? TitleOf(long chatId)
    {
        lock (Gate) return _map.TryGetValue(chatId.ToString(), out var r) ? r.Title : null;
    }

    /// <summary>
    /// 把 chat_id 解析成可以发请求的 PeerRef。缓存里没有 access_hash 就返回 null，
    /// 调用方需要先同步一次会话列表。
    /// </summary>
    public static PeerRef? Resolve(long chatId)
    {
        var (kind, id) = PeerRef.Classify(chatId);
        var rec = Get(chatId);
        if (kind == PeerKind.Chat)
            return new PeerRef(PeerKind.Chat, id, 0);
        if (rec == null || rec.AccessHash == 0)
            return null;
        return new PeerRef(kind, id, rec.AccessHash);
    }

    public static IReadOnlyList<EntityRecord> All()
    {
        lock (Gate) return _map.Values.ToList();
    }
}
