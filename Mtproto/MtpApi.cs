using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace tgdl.Mtproto;

public enum PeerKind
{
    User,
    Chat,
    Channel,
}

/// <summary>一个会话/用户的定位信息（channel 必须带 access_hash）。</summary>
public readonly record struct PeerRef(PeerKind Kind, long Id, long AccessHash)
{
    /// <summary>把输入里的 chat_id 转成 (kind, 裸 id)。</summary>
    public static (PeerKind Kind, long Id) Classify(long chatId)
    {
        if (chatId > 0) return (PeerKind.User, chatId);
        // -100xxxxxxxxxx → 频道/超级群
        if (chatId <= -1000000000000L) return (PeerKind.Channel, -chatId - 1000000000000L);
        return (PeerKind.Chat, -chatId);
    }

    public static long ToChatId(PeerKind kind, long id) => kind switch
    {
        PeerKind.User => id,
        PeerKind.Channel => -1000000000000L - id,
        _ => -id,
    };
}

/// <summary>消息里可下载的媒体定位信息。</summary>
public sealed record MediaLocation(byte[] Location, long Size, string FileName, string? MimeType, int DcId, bool IsPhoto)
{
    public string LocationBase64 => Convert.ToBase64String(Location);
}

/// <summary>一个会话条目（getDialogs 的结果，用于缓存 access_hash）。</summary>
public sealed record ChatEntity(PeerRef Peer, string Title, string? Username);

public static class MtpApi
{
    // ---------------------------------------------------------------- 基础 RPC

    public static async Task<TlNode> GetConfigAsync(MtpConnection conn, CancellationToken ct)
    {
        var w = new TlWriter();
        w.WriteUInt(TlIds.HelpGetConfig);
        var res = await conn.InvokeAsync(w.ToArray(), true, ct).ConfigureAwait(false);
        return new TlReader(res).ReadNode();
    }

    /// <summary>从当前 DC 导出一条目标 DC 的授权（目标 DC 不能是本 DC）。</summary>
    public static async Task<(long Id, byte[] Bytes)> ExportAuthorizationAsync(MtpConnection conn, int dcId, CancellationToken ct)
    {
        var w = new TlWriter();
        w.WriteUInt(TlIds.AuthExportAuthorization).WriteInt(dcId);
        var res = await conn.InvokeAsync(w.ToArray(), true, ct).ConfigureAwait(false);
        var node = new TlReader(res).ReadNode();
        if (node.Id != TlIds.AuthExportedAuthorization)
            throw new MtpException($"期望 auth.exportedAuthorization，收到 {node}");
        return (node["id"].AsLong(), node["bytes"].AsBytes());
    }

    /// <summary>在一条刚握手完成（还没发过任何消息）的连接上导入授权。</summary>
    public static async Task ImportAuthorizationAsync(MtpConnection blank, long id, byte[] bytes, CancellationToken ct)
    {
        var w = new TlWriter();
        w.WriteUInt(TlIds.AuthImportAuthorization).WriteLong(id).WriteByteString(bytes);
        var res = await blank.InvokeAsync(w.ToArray(), true, ct).ConfigureAwait(false);
        var node = new TlReader(res).ReadNode();
        if (node.Id != TlIds.AuthAuthorization)
            throw new MtpException($"auth.importAuthorization 失败：{node}");
    }

    // ---------------------------------------------------------------- 会话列表

    public sealed record DialogsResult(List<ChatEntity> Entities, PeerRef? LastPeer, int LastDate, int LastMsgId, int Total);

    public static async Task<DialogsResult> GetDialogsAsync(MtpConnection conn, int limit, PeerRef? offsetPeer,
        int offsetDate, int offsetId, CancellationToken ct)
    {
        var w = new TlWriter();
        w.WriteUInt(TlIds.MessagesGetDialogs).WriteInt(0);
        w.WriteInt(offsetDate).WriteInt(offsetId);
        WriteInputPeer(w, offsetPeer);
        w.WriteInt(limit).WriteLong(0);

        var res = await conn.InvokeAsync(w.ToArray(), true, ct).ConfigureAwait(false);
        var node = new TlReader(res).ReadNode();

        var entities = new List<ChatEntity>();
        if (node.Id == TlIds.MessagesDialogsNotModified)
            return new DialogsResult(entities, null, 0, 0, 0);

        foreach (var chatVal in node["chats"].AsVector())
        {
            var chat = chatVal.AsNode();
            if (chat == null) continue;
            var e = ParseChat(chat);
            if (e != null) entities.Add(e);
        }
        foreach (var userVal in node["users"].AsVector())
        {
            var user = userVal.AsNode();
            if (user == null) continue;
            var e = ParseUser(user);
            if (e != null) entities.Add(e);
        }

        // 分页游标：取最后一个 dialog 的 peer + 它置顶消息的日期
        PeerRef? lastPeer = null;
        int lastDate = 0, lastMsgId = 0;
        var dialogs = node["dialogs"].AsVector();
        if (dialogs.Length > 0)
        {
            var last = dialogs[dialogs.Length - 1].AsNode();
            if (last != null)
            {
                var peer = ParsePeer(last["peer"].AsNode());
                if (peer != null)
                {
                    lastPeer = peer;
                    lastMsgId = last["top_message"].AsInt();
                    foreach (var msgVal in node["messages"].AsVector())
                    {
                        var m = msgVal.AsNode();
                        if (m != null && m["id"].AsInt() == lastMsgId)
                        {
                            lastDate = m["date"].AsInt();
                            break;
                        }
                    }
                }
            }
        }

        int total = node.Has("count") ? node["count"].AsInt() : entities.Count;
        return new DialogsResult(entities, lastPeer, lastDate, lastMsgId, total);
    }

    private static ChatEntity? ParseChat(TlNode chat)
    {
        if (chat.Id == TlIds.Channel)
        {
            long id = chat["id"].AsLong();
            long accessHash = chat.Has("access_hash") ? chat["access_hash"].AsLong() : 0;
            string title = chat.Has("title") ? chat["title"].AsString() : "";
            string? username = chat.Has("username") ? chat["username"].AsString() : null;
            return new ChatEntity(new PeerRef(PeerKind.Channel, id, accessHash), title, username);
        }
        if (chat.Id == TlIds.Chat || chat.Id == TlIds.ChatForbidden)
        {
            long id = chat["id"].AsLong();
            string title = chat.Has("title") ? chat["title"].AsString() : "";
            return new ChatEntity(new PeerRef(PeerKind.Chat, id, 0), title, null);
        }
        return null;
    }

    private static ChatEntity? ParseUser(TlNode user)
    {
        if (user.Id != TlIds.User) return null;
        long id = user["id"].AsLong();
        long accessHash = user.Has("access_hash") ? user["access_hash"].AsLong() : 0;
        string first = user.Has("first_name") ? user["first_name"].AsString() : "";
        string last = user.Has("last_name") ? user["last_name"].AsString() : "";
        string? username = user.Has("username") ? user["username"].AsString() : null;
        var title = (first + " " + last).Trim();
        if (title.Length == 0) title = username ?? id.ToString();
        return new ChatEntity(new PeerRef(PeerKind.User, id, accessHash), title, username);
    }

    private static PeerRef? ParsePeer(TlNode? peer)
    {
        if (peer == null) return null;
        return peer.Id switch
        {
            TlIds.PeerUser => new PeerRef(PeerKind.User, peer["user_id"].AsLong(), 0),
            TlIds.PeerChat => new PeerRef(PeerKind.Chat, peer["chat_id"].AsLong(), 0),
            TlIds.PeerChannel => new PeerRef(PeerKind.Channel, peer["channel_id"].AsLong(), 0),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- 取消息

    public static async Task<TlNode> GetMessageAsync(MtpConnection conn, PeerRef peer, int messageId, CancellationToken ct)
    {
        byte[] query;
        if (peer.Kind == PeerKind.Channel)
        {
            var w = new TlWriter();
            w.WriteUInt(TlIds.ChannelsGetMessages);
            w.WriteUInt(TlIds.InputChannel).WriteLong(peer.Id).WriteLong(peer.AccessHash);
            w.WriteVectorHeader(1);
            w.WriteUInt(TlIds.InputMessageID).WriteInt(messageId);
            query = w.ToArray();
        }
        else
        {
            var w = new TlWriter();
            w.WriteUInt(TlIds.MessagesGetHistory);
            WriteInputPeer(w, peer);
            w.WriteInt(messageId + 1);   // offset_id：从这个 id 往下取
            w.WriteInt(0);               // offset_date
            w.WriteInt(0);               // add_offset
            w.WriteInt(1);               // limit
            w.WriteInt(0);               // max_id
            w.WriteInt(0);               // min_id
            w.WriteLong(0);              // hash
            query = w.ToArray();
        }

        var res = await conn.InvokeAsync(query, true, ct).ConfigureAwait(false);
        var node = new TlReader(res).ReadNode();
        foreach (var msgVal in node["messages"].AsVector())
        {
            var m = msgVal.AsNode();
            if (m == null) continue;
            if (m.Id != TlIds.Message && m.Id != TlIds.MessageService) continue;
            if (m["id"].AsInt() != messageId) continue;
            return m;
        }
        throw new MtpException($"找不到消息 {messageId}（可能已删除，或不在这个会话里）");
    }

    /// <summary>从消息里取可下载媒体的定位信息。</summary>
    public static MediaLocation ExtractMedia(TlNode message)
    {
        if (!message.Has("media"))
            throw new MtpException("这条消息里没有可下载的媒体");
        var media = message["media"].AsNode() ?? throw new MtpException("媒体字段解析失败");

        if (media.Id == TlIds.MessageMediaDocument)
        {
            if (!media.Has("document"))
                throw new MtpException("这条消息里没有可下载的文档（可能是空的媒体）");
            var doc = media["document"].AsNode()!;
            if (doc.Id != TlIds.Document)
                throw new MtpException("文档类型不支持");

            long id = doc["id"].AsLong();
            long accessHash = doc["access_hash"].AsLong();
            var fileRef = doc["file_reference"].AsBytes();
            long size = doc["size"].AsLong();
            int dcId = doc["dc_id"].AsInt();
            string? mime = doc.Has("mime_type") ? doc["mime_type"].AsString() : null;

            string name = $"doc_{id}";
            foreach (var attrVal in doc["attributes"].AsVector())
            {
                var attr = attrVal.AsNode();
                if (attr == null) continue;
                if (attr.Id == TlIds.DocumentAttributeFilename)
                {
                    var fn = attr["file_name"].AsString();
                    if (fn.Length > 0) name = fn;
                    break;
                }
            }

            var w = new TlWriter();
            w.WriteUInt(TlIds.InputDocumentFileLocation)
             .WriteLong(id).WriteLong(accessHash)
             .WriteByteString(fileRef).WriteString("");
            return new MediaLocation(w.ToArray(), size, name, mime, dcId, false);
        }

        if (media.Id == TlIds.MessageMediaPhoto)
        {
            if (!media.Has("photo"))
                throw new MtpException("这条消息里的照片已过期");
            var photo = media["photo"].AsNode()!;
            if (photo.Id != TlIds.Photo)
                throw new MtpException("照片类型不支持");

            long id = photo["id"].AsLong();
            long accessHash = photo["access_hash"].AsLong();
            var fileRef = photo["file_reference"].AsBytes();
            int dcId = photo["dc_id"].AsInt();

            string bestType = "";
            long bestSize = 0;
            foreach (var sizeVal in photo["sizes"].AsVector())
            {
                var sz = sizeVal.AsNode();
                if (sz == null) continue;
                long size;
                if (sz.Id == TlIds.PhotoSize) size = sz["size"].AsInt();
                else if (sz.Id == TlIds.PhotoSizeProgressive)
                {
                    size = 0;
                    foreach (var v in sz["sizes"].AsVector()) size = Math.Max(size, v.AsLong());
                }
                else continue;
                if (size >= bestSize)
                {
                    bestSize = size;
                    bestType = sz["type"].AsString();
                }
            }

            var w = new TlWriter();
            w.WriteUInt(TlIds.InputPhotoFileLocation)
             .WriteLong(id).WriteLong(accessHash)
             .WriteByteString(fileRef).WriteString(bestType);
            return new MediaLocation(w.ToArray(), bestSize, $"photo_{id}.jpg", "image/jpeg", dcId, true);
        }

        throw new MtpException($"这条消息的媒体类型不支持下载（{media.Name}）");
    }

    // ---------------------------------------------------------------- 下载分片

    public static async Task<byte[]> GetFileAsync(MtpConnection conn, byte[] location, long offset, int limit, CancellationToken ct)
    {
        var w = new TlWriter();
        w.WriteUInt(TlIds.UploadGetFile).WriteInt(0); // flags: 不走 CDN
        w.WriteRaw(location);
        w.WriteLong(offset).WriteInt(limit);
        var res = await conn.InvokeAsync(w.ToArray(), true, ct).ConfigureAwait(false);
        var node = new TlReader(res).ReadNode();
        if (node.Id == TlIds.UploadFile)
            return node["bytes"].AsBytes();
        if (node.Id == TlIds.UploadFileCdnRedirect)
            throw new MtpException("服务端要求走 CDN 下载，当前版本不支持");
        throw new MtpException($"upload.getFile 返回了意外类型 {node}");
    }

    // ---------------------------------------------------------------- 工具

    public static void WriteInputPeer(TlWriter w, PeerRef? peer)
    {
        if (peer == null)
        {
            w.WriteUInt(TlIds.InputPeerEmpty);
            return;
        }
        var p = peer.Value;
        switch (p.Kind)
        {
            case PeerKind.User:
                w.WriteUInt(TlIds.InputPeerUser).WriteLong(p.Id).WriteLong(p.AccessHash);
                break;
            case PeerKind.Chat:
                w.WriteUInt(TlIds.InputPeerChat).WriteLong(p.Id);
                break;
            default:
                w.WriteUInt(TlIds.InputPeerChannel).WriteLong(p.Id).WriteLong(p.AccessHash);
                break;
        }
    }
}
