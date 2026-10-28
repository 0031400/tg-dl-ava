using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using HwAes = System.Runtime.Intrinsics.X86.Aes;

namespace tgdl.Mtproto;

/// <summary>
/// 用 AES-NI 指令做 AES-256-IGE。
///
/// IGE 是串行链式（后一块依赖前一块的密文），没法像 CTR/ECB 那样并行铺开，
/// 但单块走硬件指令只要几纳秒；而 Aes.Create + TransformBlock 每 16 字节
/// 都要过一次托管↔原生边界，实测差一个数量级。
/// 轮密钥扩展仍用软件算（每次分片只做一次，摊薄后可以忽略）。
/// </summary>
internal static class AesNiIge
{
    private const int Rounds = 14;
    private const int RkLen = 16 * (Rounds + 1);

    private static readonly byte[] SBox = BuildSBox();

    public static unsafe void Encrypt(byte[] data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        Span<byte> rk = stackalloc byte[RkLen];
        ExpandKey(key, rk);
        var ivBuf = iv.ToArray();

        fixed (byte* p = data)
        fixed (byte* pk = rk)
        fixed (byte* piv = ivBuf)
        {
            var cprev = Sse2.LoadVector128(piv);
            var pprev = Sse2.LoadVector128(piv + 16);
            for (int off = 0; off < data.Length; off += 16)
            {
                var pcur = Sse2.LoadVector128(p + off);
                var x = Sse2.Xor(pcur, cprev);
                x = Sse2.Xor(x, Sse2.LoadVector128(pk));
                for (int r = 1; r < Rounds; r++)
                    x = HwAes.Encrypt(x, Sse2.LoadVector128(pk + 16 * r));
                x = HwAes.EncryptLast(x, Sse2.LoadVector128(pk + 16 * Rounds));
                var c = Sse2.Xor(x, pprev);
                Sse2.Store(p + off, c);
                cprev = c;
                pprev = pcur;
            }
        }
    }

    public static unsafe void Decrypt(byte[] data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        Span<byte> rk = stackalloc byte[RkLen];
        ExpandKey(key, rk);
        var ivBuf = iv.ToArray();

        fixed (byte* p = data)
        fixed (byte* pk = rk)
        fixed (byte* piv = ivBuf)
        {
            var cprev = Sse2.LoadVector128(piv);
            var pprev = Sse2.LoadVector128(piv + 16);
            for (int off = 0; off < data.Length; off += 16)
            {
                var ccur = Sse2.LoadVector128(p + off);
                var x = Sse2.Xor(ccur, pprev);
                x = Sse2.Xor(x, Sse2.LoadVector128(pk + 16 * Rounds));
                for (int r = Rounds - 1; r >= 1; r--)
                    x = HwAes.Decrypt(x, HwAes.InverseMixColumns(Sse2.LoadVector128(pk + 16 * r)));
                x = HwAes.DecryptLast(x, Sse2.LoadVector128(pk));
                var plain = Sse2.Xor(x, cprev);
                Sse2.Store(p + off, plain);
                cprev = ccur;
                pprev = plain;
            }
        }
    }

    // ---------------------------------------------------------------- 轮密钥

    private static void ExpandKey(ReadOnlySpan<byte> key, Span<byte> rk)
    {
        key.Slice(0, 32).CopyTo(rk);
        byte rcon = 1;
        for (int i = 8; i < 60; i++)
        {
            byte t0 = rk[4 * (i - 1)];
            byte t1 = rk[4 * (i - 1) + 1];
            byte t2 = rk[4 * (i - 1) + 2];
            byte t3 = rk[4 * (i - 1) + 3];
            if (i % 8 == 0)
            {
                byte tmp = t0;
                t0 = (byte)(SBox[t1] ^ rcon);
                t1 = SBox[t2];
                t2 = SBox[t3];
                t3 = SBox[tmp];
                rcon = XTime(rcon);
            }
            else if (i % 8 == 4)
            {
                t0 = SBox[t0];
                t1 = SBox[t1];
                t2 = SBox[t2];
                t3 = SBox[t3];
            }
            rk[4 * i] = (byte)(rk[4 * (i - 8)] ^ t0);
            rk[4 * i + 1] = (byte)(rk[4 * (i - 8) + 1] ^ t1);
            rk[4 * i + 2] = (byte)(rk[4 * (i - 8) + 2] ^ t2);
            rk[4 * i + 3] = (byte)(rk[4 * (i - 8) + 3] ^ t3);
        }
    }

    private static byte XTime(byte a) => (byte)((a << 1) ^ ((a >> 7) * 0x1b));

    /// <summary>S-box 用 GF(2^8) 求逆 + 仿射变换现算，避免手抄 256 字节表出错。</summary>
    private static byte[] BuildSBox()
    {
        var box = new byte[256];
        for (int a = 0; a < 256; a++)
        {
            byte inv = 0;
            if (a != 0)
            {
                for (int x = 1; x < 256; x++)
                {
                    if (GMul((byte)a, (byte)x) == 1)
                    {
                        inv = (byte)x;
                        break;
                    }
                }
            }
            box[a] = (byte)(inv ^ RotL(inv, 1) ^ RotL(inv, 2) ^ RotL(inv, 3) ^ RotL(inv, 4) ^ 0x63);
        }
        return box;
    }

    private static byte GMul(byte a, byte b)
    {
        byte p = 0;
        for (int i = 0; i < 8; i++)
        {
            if ((b & 1) != 0) p ^= a;
            byte hi = (byte)(a & 0x80);
            a = (byte)(a << 1);
            if (hi != 0) a ^= 0x1b;
            b = (byte)(b >> 1);
        }
        return p;
    }

    private static byte RotL(byte x, int n) => (byte)((x << n) | (x >> (8 - n)));
}
