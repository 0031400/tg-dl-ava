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

    // ---- 代理 ----
    public ProxyMode ProxyMode { get; set; } = ProxyMode.None;
    public ProxyKind ProxyKind { get; set; } = ProxyKind.Socks5;
    public string ProxyHost { get; set; } = "127.0.0.1";
    public int ProxyPort { get; set; } = 1080;
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";
    public string ProxyVlessLink { get; set; } = "";

    /// <summary>按当前设置解析出实际要用的代理（系统代理每次现读注册表）。</summary>
    public ProxyConfig? ResolveProxy() =>
        ProxyResolver.Resolve(ProxyMode, ProxyKind, ProxyHost, ProxyPort, ProxyUsername, ProxyPassword, ProxyVlessLink);

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
    /// <summary>单个 RPC 的等待上限（秒）。</summary>
    private const int RpcTimeoutSeconds = 60;

    private int _seqNo;
    private int _badMsgCount;
    private readonly string _sid;
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
        _sid = (_sessionId & 0xFF).ToString("x2");
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
        var transport = await ConnectTransportAsync(dcId, opt, ct).ConfigureAwait(false);
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
                    transport = await MtpTransport.ConnectAsync(ip, DcOptions.Port, TimeSpan.FromSeconds(15),
                        opt.ResolveProxy(), ct).ConfigureAwait(false);
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

    private static async Task<MtpTransport> ConnectTransportAsync(int dcId, MtpClientOptions opt, CancellationToken ct)
    {
        Exception? last = null;
        var proxy = opt.ResolveProxy();
        // 上次连通的 IP 排最前，省得每次挨个试
        foreach (var ip in DcIpMemory.Candidates(dcId))
        {
            try
            {
                return await MtpTransport.ConnectAsync(ip, DcOptions.Port, TimeSpan.FromSeconds(15), proxy, ct).ConfigureAwait(false);
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

    /// <summary>分配一个 content-related 的 seq_no（1, 3, 5…）。一条逻辑请求只分配一次。</summary>
    private int AllocSeqNo() => Interlocked.Add(ref _seqNo, 2) - 1;

    /// <summary>
    /// 非内容消息（msgs_ack 等）的 seq_no = 2 × 已发内容消息数，**不是 0**。
    /// 发 0 的话服务端会判 "seq_no too low"(32) 把 ack 全拒了，然后不停重发未确认的消息，
    /// 最后直接把连接踢掉（表现为 Unable to read beyond the end of the stream）。
    /// </summary>
    private int CurrentNonContentSeqNo() => Volatile.Read(ref _seqNo);

    /// <summary>
    /// 发送一条加密消息。
    ///
    /// **msg_id 和 seq_no 都必须在写锁内按"实际发送顺序"分配**：并发调用时如果先在锁外分配、
    /// 再抢锁发送，顺序就会乱，服务端会判 msg_id/seq_no too low。
    ///
    /// 重试时也重新分配 seq_no（传 null），不要复用：被拒的消息不计入服务端计数，
    /// 而并发期间别的请求已经分配了更大的号，复用旧号会被判 "seq_no too low"。
    /// </summary>
    private async Task<(long MsgId, int SeqNo)> SendEncryptedAsync(byte[] body, bool contentRelated,
        int? fixedSeqNo, Action<long>? beforeWrite, CancellationToken ct)
    {
        if (_dead) throw new MtpException("连接已断开");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long msgId = NextMsgId();
            // 内容消息：2k+1 且 k++；非内容消息（ack）：2k（不是 0！）
            int seqNo = fixedSeqNo ?? (contentRelated ? AllocSeqNo() : CurrentNonContentSeqNo());
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

            if (MtpLog.Verbose)
                MtpLog.Write($"[{_sid}] -> msg {msgId:x16} seq={seqNo} {(contentRelated ? "内容" : "ack ")} len={body.Length}");
            await _t.SendFrameAsync(packet, ct).ConfigureAwait(false);
            return (msgId, seqNo);
        }
        finally
        {
            _writeLock.Release();
        }
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
            // 每次尝试都重新分配 seq_no：被拒的消息不计入服务端计数，
            // 而期间别的请求可能已经分配了更大的号，复用旧的会被判 "seq_no too low"
            var sent = await SendEncryptedAsync(payload, contentRelated: true, null, id =>
            {
                _pending[id] = tcs;
            }, ct).ConfigureAwait(false);
            long msgId = sent.MsgId;
            try
            {
                // 加个上限：代理坏掉时不能无限等（否则"启动连接"会一直转圈）
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(RpcTimeoutSeconds));
                using var reg = timeoutCts.Token.Register(static s => ((TaskCompletionSource<byte[]>)s!).TrySetCanceled(), tcs);
                byte[] result;
                try
                {
                    result = await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new MtpException($"请求超时（{RpcTimeoutSeconds} 秒没有响应）—— 检查代理是否能连通");
                }
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
                // 16=msg_id 太低 17=太高 19=msg_id 不合法 20=太旧 32=seq_no 太低 33=太高 48=salt 不对
                if (code == 16) _timeOffset += 1;
                else if (code == 17) _timeOffset -= 1;
                int n = Interlocked.Increment(ref _badMsgCount);
                if (n <= 3)
                    MtpLog.Write($"[{_sid}] <- bad_msg code={code} badMsgId={badMsgId:x16} " +
                                 $"pending={_pending.ContainsKey(badMsgId)} localSeq={_seqNo}");
                if (code is 32 or 33)
                    MtpLog.Write($"seq_no 不同步（code {code}）：本地 {_seqNo}，重试会复用同一 seq_no");
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
            await SendEncryptedAsync(w.ToArray(), contentRelated: false, null, null, _cts.Token).ConfigureAwait(false);
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
