using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace tgdl.Mtproto;

/// <summary>
/// VLESS 客户端配置（从标准分享链接解析而来）。
/// 例：vless://uuid@host:443?encryption=none&amp;security=tls&amp;sni=host&amp;type=ws&amp;host=host&amp;path=%2Fxxx#tag
/// </summary>
public sealed record VlessConfig(
    string Uuid,
    string Server,
    int Port,
    bool Tls,
    string? Sni,
    string Transport,      // ws / tcp
    string? WsPath,
    string? WsHost,
    string Tag,
    bool AllowInsecure)
{
    /// <summary>uuid 的 16 字节形式（VLESS 请求头里要原样带上）。</summary>
    public byte[] UuidBytes { get; } = ParseUuid(Uuid);

    private static byte[] ParseUuid(string uuid)
    {
        var hex = uuid.Replace("-", "").Trim();
        if (hex.Length != 32)
            throw new MtpException($"VLESS uuid 格式不对：{uuid}（应为 8-4-4-4-12）");
        try { return Convert.FromHexString(hex); }
        catch { throw new MtpException($"VLESS uuid 含非法字符：{uuid}"); }
    }

    public string Display => string.IsNullOrEmpty(Tag)
        ? $"VLESS{(Tls ? "+TLS" : "")}+{Transport.ToUpperInvariant()} {Server}:{Port}"
        : $"{Tag}（VLESS{(Tls ? "+TLS" : "")}+{Transport.ToUpperInvariant()} {Server}:{Port}）";

    /// <summary>解析标准 vless:// 分享链接。</summary>
    public static VlessConfig Parse(string link)
    {
        var raw = (link ?? "").Trim();
        if (raw.Length == 0) throw new MtpException("VLESS 链接为空");
        if (!raw.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            throw new MtpException("VLESS 链接必须以 vless:// 开头");

        var body = raw.Substring(8);
        string tag = "";
        int hash = body.IndexOf('#');
        if (hash >= 0)
        {
            tag = Uri.UnescapeDataString(body.Substring(hash + 1)).Trim();
            body = body.Substring(0, hash);
        }

        string query = "";
        int q = body.IndexOf('?');
        if (q >= 0)
        {
            query = body.Substring(q + 1);
            body = body.Substring(0, q);
        }

        int at = body.LastIndexOf('@');
        if (at <= 0) throw new MtpException("VLESS 链接缺少 uuid@host:port");
        var uuid = body.Substring(0, at).Trim();
        var hostPort = body.Substring(at + 1);

        string server;
        int port;
        if (hostPort.StartsWith("[", StringComparison.Ordinal))   // IPv6
        {
            int close = hostPort.IndexOf(']');
            if (close < 0) throw new MtpException("VLESS 链接的 IPv6 地址格式不对");
            server = hostPort.Substring(1, close - 1);
            var rest = hostPort.Substring(close + 1);
            port = rest.StartsWith(":", StringComparison.Ordinal) && int.TryParse(rest.AsSpan(1), out var p) ? p : 443;
        }
        else
        {
            int colon = hostPort.LastIndexOf(':');
            if (colon <= 0) throw new MtpException("VLESS 链接缺少端口");
            server = hostPort.Substring(0, colon);
            if (!int.TryParse(hostPort.AsSpan(colon + 1), out port) || port <= 0 || port > 65535)
                throw new MtpException("VLESS 链接的端口不合法");
        }
        if (string.IsNullOrWhiteSpace(server)) throw new MtpException("VLESS 链接缺少服务器地址");

        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            args[part.Substring(0, eq)] = Uri.UnescapeDataString(part.Substring(eq + 1));
        }

        string Get(string key) => args.TryGetValue(key, out var v) ? v.Trim() : "";

        var transport = Get("type");
        if (transport.Length == 0) transport = "tcp";
        transport = transport.ToLowerInvariant();
        if (transport is not ("ws" or "tcp"))
            throw new MtpException($"暂不支持 VLESS 传输方式 type={transport}（只支持 ws / tcp）");

        var security = Get("security");
        bool tls = security.Equals("tls", StringComparison.OrdinalIgnoreCase) || Get("tls") == "tls";
        if (security.Equals("reality", StringComparison.OrdinalIgnoreCase))
            throw new MtpException("暂不支持 REALITY（需要伪造 TLS 指纹），请换 tls 或 ws+tls 的节点");

        // path 有时会带前导空格（复制粘贴导致），统一清掉
        var path = Get("path").Trim();
        if (path.Length > 0 && !path.StartsWith('/')) path = "/" + path;

        var sni = Get("sni");
        if (sni.Length == 0) sni = Get("peer");
        var wsHost = Get("host");
        var insecure = Get("allowInsecure") is "1" or "true";

        return new VlessConfig(
            uuid, server, port, tls,
            sni.Length > 0 ? sni : (tls ? server : null),
            transport,
            path.Length > 0 ? path : "/",
            wsHost.Length > 0 ? wsHost : null,
            tag,
            insecure);
    }
}

/// <summary>
/// 建立一条 VLESS 隧道：TCP → (TLS) → (WebSocket) → VLESS 头 → 返回可直接读写的 Stream。
/// </summary>
public static class VlessConnector
{
    private const byte CmdTcp = 0x01;

    public static async Task<Stream> ConnectAsync(VlessConfig cfg, string targetHost, int targetPort,
        TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        var token = linked.Token;

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            var endpoint = await ResolveAsync(cfg.Server, cfg.Port, token).ConfigureAwait(false);
            await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        Stream stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            if (cfg.Tls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, errors) =>
                {
                    if (cfg.AllowInsecure) return true;
                    return errors == SslPolicyErrors.None;
                });
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = cfg.Sni ?? cfg.Server,
                }, token).ConfigureAwait(false);
                stream = ssl;
            }

            if (cfg.Transport == "ws")
                stream = await WebSocketStream.ConnectAsync(stream, cfg.WsHost ?? cfg.Sni ?? cfg.Server,
                    cfg.WsPath ?? "/", token).ConfigureAwait(false);

            // VLESS 请求头：ver(1) + addonLen(1) + cmd(1) + port(2, BE) + atyp + addr
            var header = BuildRequestHeader(cfg, targetHost, targetPort);
            await stream.WriteAsync(header, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);

            // 响应头（ver + addonLen + addons）有些服务端要等目标回数据才发，
            // 所以**不在这里阻塞等**，包一层懒读：第一次 Read 时再消费。
            return new VlessHeaderStream(stream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// VLESS 请求头：
    ///   ver(1) + uuid(16) + addonLen(1) + addons + cmd(1) + port(2, BE) + atyp(1) + addr
    ///
    /// 两个坑（都被服务端日志精确验证过）：
    /// 1. **uuid 不能漏**：漏了之后服务端读 atyp 时会读到 payload 里的字节，
    ///    日志报 `unknown address family: 190`（0xBE = req_pq 首字节）。
    /// 2. **port 在 atyp 之前**：顺序反了会读到 IP 的第二字节，报 `unknown address family: 154`。
    /// </summary>
    private static byte[] BuildRequestHeader(VlessConfig cfg, string host, int port)
    {
        var buf = new List<byte>(64) { 0x00 };              // version
        buf.AddRange(cfg.UuidBytes);                        // uuid(16)
        buf.Add(0x00);                                      // addonLen
        buf.Add(CmdTcp);                                    // command
        buf.Add((byte)(port >> 8));                         // port(2, BE) 在地址之前
        buf.Add((byte)(port & 0xFF));

        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                buf.Add(0x01);
                buf.AddRange(ip.GetAddressBytes());
            }
            else
            {
                buf.Add(0x03);
                buf.AddRange(ip.GetAddressBytes());
            }
        }
        else
        {
            var hb = Encoding.UTF8.GetBytes(host);
            if (hb.Length > 255) throw new MtpException("VLESS 目标域名过长");
            buf.Add(0x02);
            buf.Add((byte)hb.Length);
            buf.AddRange(hb);
        }
        return buf.ToArray();
    }

    private static async Task<IPEndPoint> ResolveAsync(string host, int port, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return new IPEndPoint(ip, port);
        var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        foreach (var a in addrs)
        {
            if (a.AddressFamily == AddressFamily.InterNetwork) return new IPEndPoint(a, port);
        }
        if (addrs.Length > 0) return new IPEndPoint(addrs[0], port);
        throw new MtpException($"解析 VLESS 服务器地址失败：{host}");
    }
}

/// <summary>
/// 懒消费 VLESS 响应头（2 字节 + addons），避免服务端"等有数据才发头"时死等。
/// </summary>
public sealed class VlessHeaderStream : Stream
{
    private readonly Stream _inner;
    private bool _headerDone;

    public VlessHeaderStream(Stream inner) => _inner = inner;

    private async ValueTask EnsureHeaderAsync(CancellationToken ct)
    {
        if (_headerDone) return;
        var head = new byte[2];
        try
        {
            await _inner.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new MtpException("VLESS 服务器关闭了连接（uuid 不对，或这个节点连不到目标）");
        }
        if (head[0] != 0) throw new MtpException($"VLESS 响应版本异常：{head[0]}");
        if (head[1] > 0)
        {
            try { await _inner.ReadExactlyAsync(new byte[head[1]], ct).ConfigureAwait(false); }
            catch (EndOfStreamException) { throw new MtpException("VLESS 响应头被截断"); }
        }
        _headerDone = true;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        await EnsureHeaderAsync(ct).ConfigureAwait(false);
        return await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);

    public override void Write(byte[] buffer, int offset, int count)
        => _inner.Write(buffer, offset, count);

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 把底层连接包成 WebSocket（只做二进制帧），对上层来说就是一个普通 Stream。
/// </summary>
public sealed class WebSocketStream : Stream
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly Stream _inner;
    private byte[] _recvBuf = Array.Empty<byte>();
    private int _recvPos;
    private readonly byte[] _header = new byte[8];

    private WebSocketStream(Stream inner) => _inner = inner;

    public static async Task<WebSocketStream> ConnectAsync(Stream inner, string hostHeader, string path,
        CancellationToken ct)
    {
        var keyBytes = RandomNumberGenerator.GetBytes(16);
        var key = Convert.ToBase64String(keyBytes);
        var req = $"GET {path} HTTP/1.1\r\n" +
                  $"Host: {hostHeader}\r\n" +
                  "Upgrade: websocket\r\n" +
                  "Connection: Upgrade\r\n" +
                  $"Sec-WebSocket-Key: {key}\r\n" +
                  "Sec-WebSocket-Version: 13\r\n" +
                  "\r\n";
        await inner.WriteAsync(Encoding.ASCII.GetBytes(req), ct).ConfigureAwait(false);
        await inner.FlushAsync(ct).ConfigureAwait(false);

        // 读到空行
        var head = new List<byte>(1024);
        var one = new byte[1];
        while (head.Count < 16384)
        {
            await inner.ReadExactlyAsync(one, ct).ConfigureAwait(false);
            head.Add(one[0]);
            int n = head.Count;
            if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n')
                break;
        }
        var text = Encoding.ASCII.GetString(head.ToArray());
        int sp = text.IndexOf(' ');
        if (sp < 0 || !text.Substring(sp + 1).StartsWith("101", StringComparison.Ordinal))
        {
            var firstLine = text.Split('\n')[0].Trim();
            throw new MtpException($"WebSocket 握手失败：{firstLine}");
        }

        // 校验 Sec-WebSocket-Accept
        var expect = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WsGuid)));
        if (text.IndexOf(expect, StringComparison.OrdinalIgnoreCase) < 0)
            throw new MtpException("WebSocket 握手失败：Sec-WebSocket-Accept 不匹配");

        return new WebSocketStream(inner);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_recvPos >= _recvBuf.Length)
        {
            if (!await NextFrameAsync(ct).ConfigureAwait(false)) return 0;
        }
        int n = Math.Min(buffer.Length, _recvBuf.Length - _recvPos);
        _recvBuf.AsSpan(_recvPos, n).CopyTo(buffer.Span);
        _recvPos += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    private async Task<bool> NextFrameAsync(CancellationToken ct)
    {
        while (true)
        {
            await _inner.ReadExactlyAsync(_header.AsMemory(0, 2), ct).ConfigureAwait(false);
            int opcode = _header[0] & 0x0F;
            bool masked = (_header[1] & 0x80) != 0;
            long len = _header[1] & 0x7F;
            if (len == 126)
            {
                await _inner.ReadExactlyAsync(_header.AsMemory(0, 2), ct).ConfigureAwait(false);
                len = BinaryPrimitives.ReadUInt16BigEndian(_header);
            }
            else if (len == 127)
            {
                await _inner.ReadExactlyAsync(_header.AsMemory(0, 8), ct).ConfigureAwait(false);
                len = (long)BinaryPrimitives.ReadUInt64BigEndian(_header);
            }
            if (len < 0 || len > 64L * 1024 * 1024) throw new MtpException($"WebSocket 帧过大：{len}");

            var maskKey = new byte[4];
            if (masked) await _inner.ReadExactlyAsync(maskKey, ct).ConfigureAwait(false);

            if (opcode == 0x9)      // ping → 回 pong
            {
                var payload = new byte[len];
                await _inner.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
                await WriteFrameAsync(payload, 0xA, ct).ConfigureAwait(false);
                continue;
            }
            if (opcode == 0x8)      // close
            {
                return false;
            }

            var data = new byte[len];
            if (len > 0) await _inner.ReadExactlyAsync(data, ct).ConfigureAwait(false);
            if (masked)
            {
                for (long i = 0; i < len; i++) data[i] ^= maskKey[i & 3];
            }
            if (MtpLog.Verbose)
                MtpLog.Write($"[ws] <- frame op={opcode} len={len}");
            if (opcode == 0xA) continue;    // pong
            _recvBuf = data;
            _recvPos = 0;
            if (data.Length == 0) continue;
            return true;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await WriteFrameAsync(buffer, 0x2, ct).ConfigureAwait(false);
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    private async Task WriteFrameAsync(ReadOnlyMemory<byte> payload, int opcode, CancellationToken ct)
    {
        int len = payload.Length;
        // 帧头长度随 payload 长度变化（1/2/8 字节扩展长度 + 4 字节掩码），
        // 必须按实际长度分配缓冲区 —— 之前按最坏情况 14+len 分配，
        // 短帧会多写出 6~8 个零字节，服务端把那些 0 当成下一个帧头，直接协议错乱。
        int headerLen = len < 126 ? 2 : (len <= 0xFFFF ? 4 : 10);
        var frame = new byte[headerLen + 4 + len];
        int pos = 0;
        frame[pos++] = (byte)(0x80 | opcode);          // FIN + opcode
        if (len < 126)
        {
            frame[pos++] = (byte)(0x80 | len);         // 客户端必须掩码
        }
        else if (len <= 0xFFFF)
        {
            frame[pos++] = 0x80 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(pos), (ushort)len);
            pos += 2;
        }
        else
        {
            frame[pos++] = 0x80 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(pos), (ulong)len);
            pos += 8;
        }
        var mask = RandomNumberGenerator.GetBytes(4);
        mask.CopyTo(frame, pos);
        pos += 4;
        payload.Span.CopyTo(frame.AsSpan(pos));
        for (int i = 0; i < len; i++) frame[pos + i] ^= mask[i & 3];

        if (MtpLog.Verbose)
            MtpLog.Write($"[ws] -> frame op={opcode} len={len} head={Convert.ToHexString(frame.AsSpan(0, Math.Min(frame.Length, 20)))}");
        await _inner.WriteAsync(frame, ct).ConfigureAwait(false);
        await _inner.FlushAsync(ct).ConfigureAwait(false);
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
