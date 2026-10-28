using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using HwAes = System.Runtime.Intrinsics.X86.Aes;
using HwSse2 = System.Runtime.Intrinsics.X86.Sse2;

namespace tgdl.Mtproto;

/// <summary>MTProto 用到的密码学原语：AES-IGE、msg_key 派生、SHA1/SHA256。</summary>
public static class MtpCrypto
{
    public static byte[] Sha1(ReadOnlySpan<byte> data) => SHA1.HashData(data);

    public static byte[] Sha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    /// <summary>
    /// MTProto 的 msg_key：msg_key = SHA256(auth_key[88+x .. +32] + plaintext)[8..24]
    /// x=0 客户端→服务端，x=8 服务端→客户端。
    /// </summary>
    public static byte[] ComputeMsgKey(ReadOnlySpan<byte> authKey, ReadOnlySpan<byte> plaintext, int x)
    {
        var buf = new byte[32 + plaintext.Length];
        authKey.Slice(88 + x, 32).CopyTo(buf);
        plaintext.CopyTo(buf.AsSpan(32));
        var digest = SHA256.HashData(buf);
        var key = new byte[16];
        Buffer.BlockCopy(digest, 8, key, 0, 16);
        return key;
    }

    /// <summary>由 msg_key 与 auth_key 派生 AES key / iv。</summary>
    public static void DeriveAesKeyIv(ReadOnlySpan<byte> authKey, ReadOnlySpan<byte> msgKey, int x, Span<byte> key, Span<byte> iv)
    {
        var tmpA = new byte[16 + 36];
        msgKey.CopyTo(tmpA);
        authKey.Slice(x, 36).CopyTo(tmpA.AsSpan(16));
        var sha256a = SHA256.HashData(tmpA);

        var tmpB = new byte[36 + 16];
        authKey.Slice(40 + x, 36).CopyTo(tmpB);
        msgKey.CopyTo(tmpB.AsSpan(36));
        var sha256b = SHA256.HashData(tmpB);

        sha256a.AsSpan(0, 8).CopyTo(key);
        sha256b.AsSpan(8, 16).CopyTo(key.Slice(8));
        sha256a.AsSpan(24, 8).CopyTo(key.Slice(24));

        sha256b.AsSpan(0, 8).CopyTo(iv);
        sha256a.AsSpan(8, 16).CopyTo(iv.Slice(8));
        sha256b.AsSpan(24, 8).CopyTo(iv.Slice(24));
    }

    /// <summary>CPU 有 AES-NI 时走内联指令（见 AesNiIge），否则退回托管实现。</summary>
    private static readonly bool UseAesNi = HwAes.IsSupported && HwSse2.IsSupported;

    /// <summary>AES-256-IGE 加密（原地）。IGE 是串行链式，无法并行，只能一块一块来。</summary>
    public static void AesIgeEncrypt(byte[] data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (data.Length % 16 != 0) throw new ArgumentException("IGE 数据必须是 16 的倍数");
        if (UseAesNi && key.Length == 32 && iv.Length == 32)
        {
            AesNiIge.Encrypt(data, key, iv);
            return;
        }
        AesIgeEncryptManaged(data, key, iv);
    }

    /// <summary>纯托管实现（Aes.Create + 每块一次 TransformBlock）。没有 AES-NI 时用，也作为基准对照。</summary>
    internal static void AesIgeEncryptManaged(byte[] data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        using var enc = aes.CreateEncryptor();

        var cPrev = new byte[16];
        var pPrev = new byte[16];
        var pCur = new byte[16];
        var inBuf = new byte[16];
        var outBuf = new byte[16];
        iv[..16].CopyTo(cPrev);
        iv.Slice(16, 16).CopyTo(pPrev);

        for (int off = 0; off < data.Length; off += 16)
        {
            Buffer.BlockCopy(data, off, pCur, 0, 16);
            for (int i = 0; i < 16; i++) inBuf[i] = (byte)(pCur[i] ^ cPrev[i]);
            enc.TransformBlock(inBuf, 0, 16, outBuf, 0);
            for (int i = 0; i < 16; i++) data[off + i] = (byte)(outBuf[i] ^ pPrev[i]);
            // c_prev 是最终的密文，p_prev 是本轮明文
            Buffer.BlockCopy(data, off, cPrev, 0, 16);
            Buffer.BlockCopy(pCur, 0, pPrev, 0, 16);
        }
    }

    /// <summary>AES-256-IGE 解密（原地）。</summary>
    public static void AesIgeDecrypt(byte[] data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (data.Length % 16 != 0) throw new ArgumentException("IGE 数据必须是 16 的倍数");
        if (UseAesNi && key.Length == 32 && iv.Length == 32)
        {
            AesNiIge.Decrypt(data, key, iv);
            return;
        }
        AesIgeDecryptManaged(data, key, iv);
    }

    /// <summary>纯托管实现（Aes.Create + 每块一次 TransformBlock）。</summary>
    internal static void AesIgeDecryptManaged(byte[] data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        using var dec = aes.CreateDecryptor();

        var cPrev = new byte[16];
        var pPrev = new byte[16];
        var cCur = new byte[16];
        var inBuf = new byte[16];
        var outBuf = new byte[16];
        iv[..16].CopyTo(cPrev);
        iv.Slice(16, 16).CopyTo(pPrev);

        for (int off = 0; off < data.Length; off += 16)
        {
            Buffer.BlockCopy(data, off, cCur, 0, 16);
            for (int i = 0; i < 16; i++) inBuf[i] = (byte)(cCur[i] ^ pPrev[i]);
            dec.TransformBlock(inBuf, 0, 16, outBuf, 0);
            for (int i = 0; i < 16; i++) data[off + i] = (byte)(outBuf[i] ^ cPrev[i]);
            // c_prev 是本轮密文，p_prev 是本轮明文
            Buffer.BlockCopy(cCur, 0, cPrev, 0, 16);
            Buffer.BlockCopy(data, off, pPrev, 0, 16);
        }
    }
}

/// <summary>一条 256 字节的 MTProto auth key。</summary>
public sealed class AuthKey
{
    public AuthKey(byte[] key)
    {
        if (key.Length != 256) throw new ArgumentException($"auth key 必须是 256 字节，实际 {key.Length}");
        Key = key;
        var hash = MtpCrypto.Sha1(key);
        Id = BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(12, 8));
    }

    public byte[] Key { get; }
    public ulong Id { get; }
    public string Hex => Convert.ToHexString(Key).ToLowerInvariant();
    public string ShortHex => Hex.Substring(0, 8);

    public static AuthKey FromHex(string hex)
    {
        var clean = hex.Trim().Replace(" ", "").Replace("\r", "").Replace("\n", "");
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(2);
        if (clean.Length != 512) throw new FormatException($"auth key 应该是 512 个 hex 字符（256 字节），实际 {clean.Length}");
        return new AuthKey(Convert.FromHexString(clean));
    }
}
