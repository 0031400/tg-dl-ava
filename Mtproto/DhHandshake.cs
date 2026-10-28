using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace tgdl.Mtproto;

public sealed record HandshakeResult(AuthKey Key, long TimeOffsetSeconds, long ServerTime);

/// <summary>
/// 手写 MTProto 的 DH 握手（auth key 生成）：
/// req_pq_multi → resPQ → 分解 pq → req_DH_params → server_DH_params_ok →
/// set_client_DH_params → dh_gen_ok。
/// 全程用未加密消息（auth_key_id = 0）。
/// </summary>
public static class DhHandshake
{
    public static async Task<HandshakeResult> RunAsync(MtpTransport transport, CancellationToken ct)
    {
        var gen = new MsgIdGen();

        // ---------- 1) req_pq_multi ----------
        var nonce = RandomBytes(16);
        var w = new TlWriter();
        w.WriteUInt(TlIds.ReqPqMulti).WriteRaw(nonce);
        await SendPlainAsync(transport, w.ToArray(), gen, ct).ConfigureAwait(false);

        var resPq = await ReadPlainAsync(transport, ct).ConfigureAwait(false);
        var r = new TlReader(resPq);
        uint id = r.ReadUInt();
        if (id != TlIds.ResPQ) throw new MtpException($"期望 resPQ，收到 0x{id:x8}");
        var rNonce = r.ReadRaw(16);
        if (!rNonce.AsSpan().SequenceEqual(nonce)) throw new MtpException("resPQ nonce 不匹配");
        var serverNonce = r.ReadRaw(16);
        var pqBytes = r.ReadByteString();
        uint vecId = r.ReadUInt();
        if (vecId != TlReader.VectorCtorId) throw new MtpException("resPQ 指纹列表格式异常");
        int fpCount = r.ReadInt();
        var fingerprints = new ulong[fpCount];
        for (int i = 0; i < fpCount; i++) fingerprints[i] = (ulong)r.ReadLong();

        ServerRsaKey? rsaKey = null;
        ulong usedFp = 0;
        foreach (var fp in fingerprints)
        {
            var k = TelegramRsa.Find(fp, allowOld: true);
            if (k == null) continue;
            rsaKey = k;
            usedFp = fp;
            break;
        }
        if (rsaKey == null)
            throw new MtpException("服务端公钥指纹没有一个能对上（公钥列表可能过期）");

        // ---------- 2) 分解 pq ----------
        var pq = new BigInteger(pqBytes, isUnsigned: true, isBigEndian: true);
        var (p, q) = Factorize(pq);
        if (p <= 1 || q <= 1 || p * q != pq)
            throw new MtpException($"pq 分解结果不对（p={p}, q={q}）");
        var pBytes = p.ToByteArray(isUnsigned: true, isBigEndian: true);
        var qBytes = q.ToByteArray(isUnsigned: true, isBigEndian: true);

        var newNonce = RandomBytes(32);

        // p_q_inner_data#83c95aec pq:string p:string q:string nonce:int128 server_nonce:int128 new_nonce:int256
        var inner = new TlWriter();
        inner.WriteUInt(TlIds.PQInnerData)
             .WriteByteString(pqBytes)
             .WriteByteString(pBytes)
             .WriteByteString(qBytes)
             .WriteRaw(nonce)
             .WriteRaw(serverNonce)
             .WriteRaw(newNonce);
        var encryptedData = TelegramRsa.Encrypt(rsaKey, inner.Span);

        // req_DH_params#d712e4be nonce:int128 server_nonce:int128 p:string q:string
        //                     public_key_fingerprint:long encrypted_data:string
        var req = new TlWriter();
        req.WriteUInt(TlIds.ReqDHParams)
           .WriteRaw(nonce)
           .WriteRaw(serverNonce)
           .WriteByteString(pBytes)
           .WriteByteString(qBytes)
           .WriteLong(unchecked((long)usedFp))
           .WriteByteString(encryptedData);
        await SendPlainAsync(transport, req.ToArray(), gen, ct).ConfigureAwait(false);

        var dhResp = await ReadPlainAsync(transport, ct).ConfigureAwait(false);
        var r2 = new TlReader(dhResp);
        uint id2 = r2.ReadUInt();
        if (id2 == TlIds.ServerDHParamsFail)
            throw new MtpException("服务端拒绝 DH 参数（server_DH_params_fail）");
        if (id2 != TlIds.ServerDHParamsOk)
            throw new MtpException($"期望 server_DH_params_ok，收到 0x{id2:x8}");
        var n2 = r2.ReadRaw(16);
        var sn2 = r2.ReadRaw(16);
        if (!n2.AsSpan().SequenceEqual(nonce) || !sn2.AsSpan().SequenceEqual(serverNonce))
            throw new MtpException("server_DH_params_ok 的 nonce 不匹配");
        var encryptedAnswer = r2.ReadByteString();

        // ---------- 3) 解密 server_DH_inner_data ----------
        var (tmpKey, tmpIv) = DeriveKeyIv(serverNonce, newNonce);
        MtpCrypto.AesIgeDecrypt(encryptedAnswer, tmpKey, tmpIv);
        var answer = new byte[encryptedAnswer.Length - 20];
        Buffer.BlockCopy(encryptedAnswer, 20, answer, 0, answer.Length);

        var r3 = new TlReader(answer);
        uint id3 = r3.ReadUInt();
        if (id3 != TlIds.ServerDHInnerData)
            throw new MtpException($"期望 server_DH_inner_data，收到 0x{id3:x8}");
        var n3 = r3.ReadRaw(16);
        var sn3 = r3.ReadRaw(16);
        if (!n3.AsSpan().SequenceEqual(nonce) || !sn3.AsSpan().SequenceEqual(serverNonce))
            throw new MtpException("server_DH_inner_data 的 nonce 不匹配");
        int g = r3.ReadInt();
        var dhPrimeBytes = r3.ReadByteString();
        var gABytes = r3.ReadByteString();
        int serverTime = r3.ReadInt();

        var dhPrime = new BigInteger(dhPrimeBytes, isUnsigned: true, isBigEndian: true);
        var gA = new BigInteger(gABytes, isUnsigned: true, isBigEndian: true);

        // ---------- 4) 生成自己的 b，算出 auth key ----------
        var b = new BigInteger(RandomBytes(256), isUnsigned: true, isBigEndian: true);
        var gB = BigInteger.ModPow(g, b, dhPrime);
        var gAb = BigInteger.ModPow(gA, b, dhPrime);

        var gBBytes = gB.ToByteArray(isUnsigned: true, isBigEndian: true);
        var gAbBytes = gAb.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (gAbBytes.Length < 256)
        {
            var padded = new byte[256];
            Buffer.BlockCopy(gAbBytes, 0, padded, 256 - gAbBytes.Length, gAbBytes.Length);
            gAbBytes = padded;
        }
        else if (gAbBytes.Length > 256)
        {
            gAbBytes = gAbBytes.AsSpan(gAbBytes.Length - 256).ToArray();
        }

        var authKey = new AuthKey(gAbBytes);

        // client_DH_inner_data#6643b654 nonce:int128 server_nonce:int128 retry_id:long g_b:bytes
        var cdw = new TlWriter();
        cdw.WriteUInt(TlIds.ClientDHInnerData)
           .WriteRaw(nonce)
           .WriteRaw(serverNonce)
           .WriteLong(0)
           .WriteByteString(gBBytes);
        var cdBytes = cdw.ToArray();
        var cdHash = MtpCrypto.Sha1(cdBytes);
        int cdPad = (16 - ((20 + cdBytes.Length) & 15)) & 15;
        var cdHashed = new byte[20 + cdBytes.Length + cdPad];
        cdHash.CopyTo(cdHashed, 0);
        cdBytes.CopyTo(cdHashed, 20);
        if (cdPad > 0) RandomNumberGenerator.Fill(cdHashed.AsSpan(20 + cdBytes.Length));
        MtpCrypto.AesIgeEncrypt(cdHashed, tmpKey, tmpIv);

        // set_client_DH_params#f5045f1f nonce:int128 server_nonce:int128 encrypted_data:string
        var scw = new TlWriter();
        scw.WriteUInt(TlIds.SetClientDHParams)
           .WriteRaw(nonce)
           .WriteRaw(serverNonce)
           .WriteByteString(cdHashed);
        await SendPlainAsync(transport, scw.ToArray(), gen, ct).ConfigureAwait(false);

        var genResp = await ReadPlainAsync(transport, ct).ConfigureAwait(false);
        var r4 = new TlReader(genResp);
        uint id4 = r4.ReadUInt();
        if (id4 != TlIds.DhGenOk && id4 != TlIds.DhGenRetry && id4 != TlIds.DhGenFail)
            throw new MtpException($"期望 dh_gen_*，收到 0x{id4:x8}");
        var n4 = r4.ReadRaw(16);
        var sn4 = r4.ReadRaw(16);
        if (!n4.AsSpan().SequenceEqual(nonce) || !sn4.AsSpan().SequenceEqual(serverNonce))
            throw new MtpException("dh_gen_* 的 nonce 不匹配");
        if (id4 == TlIds.DhGenFail) throw new MtpException("服务端返回 dh_gen_fail（DH 失败）");
        if (id4 == TlIds.DhGenRetry) throw new MtpException("服务端返回 dh_gen_retry（需要重试）");

        var hash1 = r4.ReadRaw(16);
        var expect = NewNonceHash(authKey, newNonce, 1);
        if (!hash1.AsSpan().SequenceEqual(expect))
            throw new MtpException("new_nonce_hash1 校验失败（auth key 不一致）");

        long offset = serverTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new HandshakeResult(authKey, offset, serverTime);
    }

    /// <summary>tmp_aes_key / tmp_aes_iv，见 core.telegram.org/mtproto/auth_key。</summary>
    public static (byte[] Key, byte[] Iv) DeriveKeyIv(ReadOnlySpan<byte> serverNonce, ReadOnlySpan<byte> newNonce)
    {
        var ns = new byte[48];
        newNonce.CopyTo(ns.AsSpan(0));           // new_nonce + server_nonce
        serverNonce.CopyTo(ns.AsSpan(32));
        var hash1 = MtpCrypto.Sha1(ns);          // sha1(new_nonce + server_nonce)

        var sn = new byte[48];
        serverNonce.CopyTo(sn.AsSpan(0));        // server_nonce + new_nonce
        newNonce.CopyTo(sn.AsSpan(16));
        var hash2 = MtpCrypto.Sha1(sn);          // sha1(server_nonce + new_nonce)

        var nn = new byte[64];
        newNonce.CopyTo(nn.AsSpan(0));           // new_nonce + new_nonce
        newNonce.CopyTo(nn.AsSpan(32));
        var hash3 = MtpCrypto.Sha1(nn);          // sha1(new_nonce + new_nonce)

        var key = new byte[32];
        hash1.CopyTo(key, 0);
        Buffer.BlockCopy(hash2, 0, key, 20, 12);

        var iv = new byte[32];
        Buffer.BlockCopy(hash2, 12, iv, 0, 8);
        Buffer.BlockCopy(hash3, 0, iv, 8, 20);
        Buffer.BlockCopy(newNonce.ToArray(), 0, iv, 28, 4);
        return (key, iv);
    }

    private static byte[] NewNonceHash(AuthKey key, byte[] newNonce, byte number)
    {
        var sha1 = MtpCrypto.Sha1(key.Key);
        var data = new byte[32 + 1 + 8];
        Buffer.BlockCopy(newNonce, 0, data, 0, 32);
        data[32] = number;
        Buffer.BlockCopy(sha1, 0, data, 33, 8);
        var digest = MtpCrypto.Sha1(data);
        var result = new byte[16];
        Buffer.BlockCopy(digest, 4, result, 0, 16);
        return result;
    }

    private sealed class MsgIdGen
    {
        private long _last;

        public long Next()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() << 32;
            if (now <= _last) now = _last + 4;
            _last = now;
            return now;
        }
    }

    private static async Task SendPlainAsync(MtpTransport t, byte[] body, MsgIdGen gen, CancellationToken ct)
    {
        long msgId = gen.Next();
        var frame = new byte[8 + 8 + 4 + body.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(0), 0);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(8), msgId);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(16), body.Length);
        body.CopyTo(frame, 20);
        await t.SendFrameAsync(frame, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadPlainAsync(MtpTransport t, CancellationToken ct)
    {
        var frame = await t.ReadFrameAsync(ct).ConfigureAwait(false);
        if (frame.Length == 4)
        {
            int code = BinaryPrimitives.ReadInt32LittleEndian(frame);
            throw new MtpException($"服务端在传输层拒绝了本次请求（code {code}），" +
                                   "通常是握手被限流，稍后重试即可");
        }
        if (frame.Length < 20) throw new MtpException($"握手响应太短（{frame.Length} 字节）");
        ulong authKeyId = BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(0));
        if (authKeyId != 0) throw new MtpException("握手阶段收到了加密消息");
        int len = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(16));
        if (len < 4 || 20 + len > frame.Length) throw new MtpException("握手响应长度异常");
        var body = new byte[len];
        Buffer.BlockCopy(frame, 20, body, 0, len);
        return body;
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    /// <summary>把 pq 拆成两个素数（Pollard rho，pq 只有 ~63 bit）。</summary>
    public static (BigInteger P, BigInteger Q) Factorize(BigInteger pq)
    {
        if (pq < 4) throw new MtpException("pq 太小");
        for (int d = 2; d < 200000; d++)
        {
            if (pq % d == 0) return (d, pq / d);
        }
        var factor = PollardRho(pq);
        return (factor, pq / factor);
    }

    private static BigInteger PollardRho(BigInteger n)
    {
        if (n % 2 == 0) return 2;
        if (n % 3 == 0) return 3;
        var rnd = new Random();
        for (int attempt = 0; attempt < 200; attempt++)
        {
            var c = new BigInteger(rnd.Next(1, 1000));
            var x = new BigInteger(rnd.Next(2, 1000));
            var y = x;
            var d = BigInteger.One;
            long iter = 0;
            while (d.IsOne && iter++ < 5_000_000)
            {
                x = (x * x + c) % n;
                y = (y * y + c) % n;
                y = (y * y + c) % n;
                d = BigInteger.GreatestCommonDivisor(BigInteger.Abs(x - y), n);
            }
            if (!d.IsOne && d != n) return d;
        }
        throw new MtpException("无法分解 pq");
    }
}
