using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace tgdl.Mtproto;

/// <summary>连接参数（api_id 等）。</summary>
public sealed class MtpClientOptions
{
    public int ApiId { get; set; } = 2040;
    public string ApiHash { get; set; } = "b18441a1ff607e10a989891a5462e627";
    public string DeviceModel { get; set; } = "tgdl";
    public string SystemVersion { get; set; } = "Windows";
    public string AppVersion { get; set; } = "1.0";
    public string SystemLangCode { get; set; } = "zh-CN";
    public string LangPack { get; set; } = "";
    public string LangCode { get; set; } = "zh";
    public int Layer { get; set; } = 225;

    public MtpClientOptions Clone() => (MtpClientOptions)MemberwiseClone();
}

/// <summary>
/// 一条加密的 MTProto 连接：负责封包/解包、msg_id/seq_no、salt 维护、
/// 响应分发（rpc_result / new_session_created / bad_server_salt / 容器 / gzip）。
/// </summary>
public sealed class MtpConnection : IAsyncDisposable
{
    private readonly MtpTransport _t;
    private readonly AuthKey _key;
    private readonly MtpClientOptions _opt;
    private readonly ulong _sessionId;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<byte[]>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _idLock = new();
    private readonly List<long> _ackQueue = new();
    private readonly object _ackLock = new();

    private long _salt;
    private long _timeOffset;
    private long _lastMsgId;
    private int _seqNo;
    private volatile bool _initSent;
    private volatile bool _dead;
    private Task? _recv;
    private Exception? _fatal;

    private MtpConnection(MtpTransport transport, AuthKey key, int dcId, long timeOffset, MtpClientOptions opt)
    {
        _t = transport;
        _key = key;
        DcId = dcId;
        _timeOffset = timeOffset;
        _opt = opt;
        _sessionId = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
    }

    public AuthKey Key => _key;
    public int DcId { get; }
    public string Remote => _t.Remote;
    public long TimeOffsetSeconds => _timeOffset;
    public bool IsDead => _dead;

    /// <summary>连接是怎么死的（给上层日志用）。</summary>
    public Exception? FatalError => _fatal;
    public bool InitSent => _initSent;

    /// <summary>用现成的 auth key 连一条新连接（不做 DH）。</summary>
    public static async Task<MtpConnection> ConnectWithKeyAsync(int dcId, AuthKey key, long timeOffset,
        MtpClientOptions opt, CancellationToken ct)
    {
        var transport = await ConnectTransportAsync(dcId, ct).ConfigureAwait(false);
        var conn = new MtpConnection(transport, key, dcId, timeOffset, opt);
        conn.Start();
        return conn;
    }

    /// <summary>做一次全新 DH，拿到一条「已握手但未 initConnection」的连接。</summary>
    public static async Task<MtpConnection> HandshakeAsync(int dcId, MtpClientOptions opt, CancellationToken ct)
    {
        Exception? last = null;
        // 服务端偶尔会限流握手（传输层 -404），换个地址/等一会儿再试一轮
        for (int round = 0; round < 2; round++)
        {
            if (round > 0) await Task.Delay(1200, ct).ConfigureAwait(false);
            foreach (var ip in DcIpMemory.Candidates(dcId))
            {
                MtpTransport? transport = null;
                try
                {
                    transport = await MtpTransport.ConnectAsync(ip, DcOptions.Port, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                    var hs = await DhHandshake.RunAsync(transport, ct).ConfigureAwait(false);
                    DcIpMemory.Remember(dcId, transport.RemoteIp);
                    var conn = new MtpConnection(transport, hs.Key, dcId, hs.TimeOffsetSeconds, opt);
                    conn.Start();
                    return conn;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                    MtpLog.Write($"DC{dcId} 握手 {ip} 失败", ex);
                    if (transport != null) await transport.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        throw new MtpException($"DC{dcId} 握手失败：{last?.Message}", last ?? new Exception());
    }

    private static async Task<MtpTransport> ConnectTransportAsync(int dcId, CancellationToken ct)
    {
        Exception? last = null;
        // 上次连通的 IP 排最前，省得每次挨个试
        foreach (var ip in DcIpMemory.Candidates(dcId))
        {
            try
            {
                return await MtpTransport.ConnectAsync(ip, DcOptions.Port, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }
        throw new MtpException($"连接 DC{dcId} 失败：{last?.Message}", last ?? new Exception());
    }

    private void Start()
    {
        _recv = Task.Run(RecvLoopAsync);
    }

    // ------------------------------------------------------------------ 发送

    private long NextMsgId()
    {
        lock (_idLock)
        {
            long now = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _timeOffset) << 32;
            if (now <= _lastMsgId) now = _lastMsgId + 4;
            _lastMsgId = now;
            return now;
        }
    }

    private async Task<long> SendEncryptedAsync(byte[] body, bool contentRelated, Action<long>? beforeWrite, CancellationToken ct)
    {
        if (_dead) throw new MtpException("连接已断开");

        long msgId = NextMsgId();
        int seqNo = contentRelated ? Interlocked.Add(ref _seqNo, 2) - 1 : 0;
        beforeWrite?.Invoke(msgId);
        int padding = 16 - (body.Length & 15);
        if (padding < 12) padding += 16;

        var plain = new byte[32 + body.Length + padding];
        BinaryPrimitives.WriteInt64LittleEndian(plain.AsSpan(0), _salt);
        BinaryPrimitives.WriteUInt64LittleEndian(plain.AsSpan(8), _sessionId);
        BinaryPrimitives.WriteInt64LittleEndian(plain.AsSpan(16), msgId);
        BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(24), seqNo);
        BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(28), body.Length);
        Buffer.BlockCopy(body, 0, plain, 32, body.Length);
        RandomNumberGenerator.Fill(plain.AsSpan(32 + body.Length));

        var msgKey = MtpCrypto.ComputeMsgKey(_key.Key, plain, 0);
        Span<byte> aesKey = stackalloc byte[32];
        Span<byte> aesIv = stackalloc byte[32];
        MtpCrypto.DeriveAesKeyIv(_key.Key, msgKey, 0, aesKey, aesIv);
        MtpCrypto.AesIgeEncrypt(plain, aesKey, aesIv);

        var packet = new byte[8 + 16 + plain.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(0), _key.Id);
        msgKey.CopyTo(packet, 8);
        Buffer.BlockCopy(plain, 0, packet, 24, plain.Length);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _t.SendFrameAsync(packet, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
        return msgId;
    }

    private byte[] WrapInit(byte[] query)
    {
        var w = new TlWriter();
        w.WriteUInt(TlIds.InvokeWithLayer).WriteInt(_opt.Layer);
        w.WriteUInt(TlIds.InitConnection).WriteInt(0); // flags
        w.WriteInt(_opt.ApiId);
        w.WriteString(_opt.DeviceModel);
        w.WriteString(_opt.SystemVersion);
        w.WriteString(_opt.AppVersion);
        w.WriteString(_opt.SystemLangCode);
        w.WriteString(_opt.LangPack);
        w.WriteString(_opt.LangCode);
        w.WriteRaw(query);
        return w.ToArray();
    }

    /// <summary>发一个 RPC 并等结果（返回结果的 TL 字节，不含 rpc_result 头）。</summary>
    public async Task<byte[]> InvokeAsync(byte[] body, bool initConnection, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            bool wrap = initConnection && !_initSent;
            byte[] payload = wrap ? WrapInit(body) : body;

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            long msgId = 0;
            await SendEncryptedAsync(payload, contentRelated: true, id =>
            {
                msgId = id;
                _pending[id] = tcs;
            }, ct).ConfigureAwait(false);
            try
            {
                using var reg = ct.Register(static s => ((TaskCompletionSource<byte[]>)s!).TrySetCanceled(), tcs);
                var result = await tcs.Task.ConfigureAwait(false);
                _initSent = true;
                // 第一次真的收到响应才算"这个 IP 可用"
                DcIpMemory.Remember(DcId, _t.RemoteIp);
                return result;
            }
            catch (BadSaltException)
            {
                last = new MtpException("服务端 salt 已更新，重试");
            }
            catch (RetryException ex)
            {
                last = ex;
            }
            finally
            {
                _pending.TryRemove(msgId, out _);
            }
        }
        throw new MtpException($"RPC 重试多次仍失败：{last?.Message}", last ?? new Exception());
    }

    // ------------------------------------------------------------------ 接收

    private async Task RecvLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await _t.ReadFrameAsync(_cts.Token).ConfigureAwait(false);
                HandleFrame(frame);
                await FlushAcksAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _fatal = ex;
            _dead = true;
            FailAll(ex);
        }
    }

    private void HandleFrame(byte[] frame)
    {
        if (frame.Length == 4)
        {
            // 服务端在传输层直接拒绝：最常见的原因是这条 auth key 已经失效（被重置/删除）
            int code = BinaryPrimitives.ReadInt32LittleEndian(frame);
            MtpLog.Write($"服务端拒绝（传输层 {MtpTransportError.Describe(code)}）：auth_key_id=0x{_key.Id:x16}");
            throw new MtpException(code == -404
                ? $"服务端不认识这条 auth key（-404）：它可能已被重置或删除，请重新导入"
                : $"服务端拒绝了请求：{MtpTransportError.Describe(code)}");
        }
        if (frame.Length < 24 + 32)
        {
            MtpLog.Write($"收到过短的加密帧 {frame.Length}: {Convert.ToHexString(frame)}");
            throw new MtpException($"收到过短的加密帧 {frame.Length}");
        }
        ulong authKeyId = BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(0));
        if (authKeyId != _key.Id) throw new MtpException($"auth_key_id 不匹配（期望 {_key.Id:x16}，收到 {authKeyId:x16}）");

        var msgKey = new byte[16];
        Buffer.BlockCopy(frame, 8, msgKey, 0, 16);

        var cipher = new byte[frame.Length - 24];
        Buffer.BlockCopy(frame, 24, cipher, 0, cipher.Length);

        Span<byte> aesKey = stackalloc byte[32];
        Span<byte> aesIv = stackalloc byte[32];
        MtpCrypto.DeriveAesKeyIv(_key.Key, msgKey, 8, aesKey, aesIv);
        MtpCrypto.AesIgeDecrypt(cipher, aesKey, aesIv);

        var expect = MtpCrypto.ComputeMsgKey(_key.Key, cipher, 8);
        if (!expect.AsSpan().SequenceEqual(msgKey))
            throw new MtpException("msg_key 校验失败（数据被篡改或密钥不对）");

        // 头部里的 salt 不使用：salt 只在 new_session_created / bad_server_salt 时更新
        int seqNo = BinaryPrimitives.ReadInt32LittleEndian(cipher.AsSpan(24));        int len = BinaryPrimitives.ReadInt32LittleEndian(cipher.AsSpan(28));
        if (len < 4 || 32 + len > cipher.Length) throw new MtpException("消息长度异常");
        long msgId = BinaryPrimitives.ReadInt64LittleEndian(cipher.AsSpan(16));

        var body = new byte[len];
        Buffer.BlockCopy(cipher, 32, body, 0, len);

        HandleMessage(msgId, seqNo, body);
    }

    private void HandleMessage(long msgId, int seqNo, byte[] body)
    {
        if (seqNo > 0)
        {
            lock (_ackLock) _ackQueue.Add(msgId);
        }

        uint id = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0));
        if (MtpLog.Verbose) MtpLog.Write($"msg id=0x{id:x8} len={body.Length} seq={seqNo}");
        switch (id)
        {
            case TlIds.RpcResult:
            {
                long reqMsgId = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(4));
                var result = new byte[body.Length - 12];
                Buffer.BlockCopy(body, 12, result, 0, result.Length);
                result = MaybeGunzip(result);
                if (result.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(result) == TlIds.RpcError)
                {
                    var rr = new TlReader(result, 4);
                    int code = rr.ReadInt();
                    string text = rr.ReadString();
                    if (_pending.TryRemove(reqMsgId, out var tcs))
                        tcs.TrySetException(new RpcException(code, text));
                }
                else
                {
                    if (_pending.TryRemove(reqMsgId, out var tcs))
                        tcs.TrySetResult(result);
                }
                break;
            }
            case TlIds.NewSessionCreated:
                _salt = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(20));
                break;
            case TlIds.BadServerSalt:
            {
                long badMsgId = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(4));
                _salt = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(20));
                if (_pending.TryRemove(badMsgId, out var tcs))
                    tcs.TrySetException(new BadSaltException());
                break;
            }
            case TlIds.BadMsgNotification:
            {
                long badMsgId = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(4));
                int code = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(16));
                if (code == 16) _timeOffset += 1;      // msg_id 太低
                else if (code == 17) _timeOffset -= 1; // msg_id 太高
                if (_pending.TryRemove(badMsgId, out var tcs))
                    tcs.TrySetException(new RetryException($"bad_msg_notification code={code}"));
                break;
            }
            case TlIds.MsgsAck:
            case TlIds.Pong:
                break;
            case TlIds.MsgContainer:
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));
                int pos = 8;
                for (int i = 0; i < count; i++)
                {
                    long innerId = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(pos));
                    int innerSeq = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos + 8));
                    int innerLen = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos + 12));
                    pos += 16;
                    if (innerLen < 4 || pos + innerLen > body.Length) throw new MtpException("容器内消息长度异常");
                    var inner = new byte[innerLen];
                    Buffer.BlockCopy(body, pos, inner, 0, innerLen);
                    pos += innerLen;
                    HandleMessage(innerId, innerSeq, inner);
                }
                break;
            }
            case TlIds.GzipPacked:
                HandleMessage(msgId, seqNo, MaybeGunzip(body));
                break;
            default:
                // 其它（updates 之类）忽略
                break;
        }
    }

    /// <summary>rpc_result 里的结果对象也可能是 gzip_packed，这里统一剥掉。</summary>
    private static byte[] MaybeGunzip(byte[] body)
    {
        int guard = 0;
        while (body.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(body) == TlIds.GzipPacked && guard++ < 4)
        {
            var r = new TlReader(body, 4);
            var packed = r.ReadByteString();
            using var input = new MemoryStream(packed);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            var inner = output.ToArray();
            if (MtpLog.Verbose) MtpLog.Write($"gunzip {packed.Length} -> {inner.Length}");
            body = inner;
        }
        return body;
    }

    private async Task FlushAcksAsync()
    {
        long[] ids;
        lock (_ackLock)
        {
            if (_ackQueue.Count == 0) return;
            ids = _ackQueue.ToArray();
            _ackQueue.Clear();
        }
        try
        {
            var w = new TlWriter();
            w.WriteUInt(TlIds.MsgsAck).WriteVectorHeader(ids.Length);
            foreach (var id in ids) w.WriteLong(id);
            await SendEncryptedAsync(w.ToArray(), contentRelated: false, null, _cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // ack 失败无所谓，服务端会重发
        }
    }

    private void FailAll(Exception ex)
    {
        foreach (var kv in _pending)
        {
            kv.Value.TrySetException(new MtpException("连接中断：" + ex.Message, ex));
        }
        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _dead = true;
        _cts.Cancel();
        await _t.DisposeAsync().ConfigureAwait(false);
        if (_recv != null)
        {
            try { await _recv.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _cts.Dispose();
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
