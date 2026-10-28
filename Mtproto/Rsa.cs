using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Security.Cryptography;

namespace tgdl.Mtproto;

public sealed record ServerRsaKey(ulong Fingerprint, string ModulusHex, string ExponentHex, bool IsOld)
{
    private BigInteger? _n;
    private BigInteger? _e;

    public BigInteger Modulus => _n ??= HexToBigInteger(ModulusHex);
    public BigInteger Exponent => _e ??= HexToBigInteger(ExponentHex);

    public static BigInteger HexToBigInteger(string hex)
    {
        var bytes = Convert.FromHexString(hex.Length % 2 == 0 ? hex : "0" + hex);
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }

    /// <summary>Telegram 的 fingerprint：sha1(serialize_bytes(n) + serialize_bytes(e)) 的后 8 字节（小端）。</summary>
    public static ulong ComputeFingerprint(BigInteger n, BigInteger e)
    {
        var nBytes = n.ToByteArray(isUnsigned: true, isBigEndian: true);
        var eBytes = e.ToByteArray(isUnsigned: true, isBigEndian: true);
        var buf = new List<byte>(nBytes.Length + eBytes.Length + 16);
        buf.AddRange(SerializeBytes(nBytes));
        buf.AddRange(SerializeBytes(eBytes));
        var hash = SHA1.HashData(buf.ToArray());
        return BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(12, 8));
    }

    private static byte[] SerializeBytes(byte[] data)
    {
        int len = data.Length;
        byte[] prefix = len < 254
            ? new[] { (byte)len }
            : new[] { (byte)254, (byte)len, (byte)(len >> 8), (byte)(len >> 16) };
        // 与 TlReader/TlWriter 一致：短串把长度前缀也算进 4 字节对齐
        int pad = len < 254 ? ((4 - ((len + 1) & 3)) & 3) : ((4 - (len & 3)) & 3);
        var r = new byte[prefix.Length + len + pad];
        Buffer.BlockCopy(prefix, 0, r, 0, prefix.Length);
        Buffer.BlockCopy(data, 0, r, prefix.Length, len);
        return r;
    }
}

/// <summary>Telegram 服务端 RSA 公钥（用于 DH 握手的第一步加密）。</summary>
public static class TelegramRsa
{
    private static readonly List<ServerRsaKey> Keys = new();

    static TelegramRsa()
    {
        foreach (var k in RsaKeys.All)
        {
            var fp = ServerRsaKey.ComputeFingerprint(k.Modulus, k.Exponent);
            Keys.Add(k with { Fingerprint = fp });
        }
    }

    public static IReadOnlyList<ServerRsaKey> All => Keys;

    public static ServerRsaKey? Find(ulong fingerprint, bool allowOld)
    {
        foreach (var k in Keys)
        {
            if (k.Fingerprint == fingerprint && (allowOld || !k.IsOld)) return k;
        }
        return null;
    }

    /// <summary>
    /// 按 Telegram 的方式加密：明文 = sha1(data) + data + 随机填充到 255 字节，
    /// 然后做裸 RSA（不加 PKCS#1 填充），输出 256 字节。
    /// </summary>
    public static byte[] Encrypt(ServerRsaKey key, ReadOnlySpan<byte> data)
    {
        if (data.Length > 235) throw new ArgumentException("待加密数据太长");
        var toEncrypt = new byte[255];
        var hash = SHA1.HashData(data);
        hash.CopyTo(toEncrypt, 0);
        data.CopyTo(toEncrypt.AsSpan(20));
        int padFrom = 20 + data.Length;
        RandomNumberGenerator.Fill(toEncrypt.AsSpan(padFrom));

        var payload = new BigInteger(toEncrypt, isUnsigned: true, isBigEndian: true);
        var encrypted = BigInteger.ModPow(payload, key.Exponent, key.Modulus);
        var block = encrypted.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (block.Length > 256) throw new InvalidOperationException("RSA 输出超过 256 字节");
        if (block.Length == 256) return block;
        var padded = new byte[256];
        Buffer.BlockCopy(block, 0, padded, 256 - block.Length, block.Length);
        return padded;
    }
}
