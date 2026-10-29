using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace tgdl.Mtproto;

/// <summary>
/// 通用 TL 反序列化器：读 4 字节 constructor id，查 schema，按字段表展开。
/// 全部字段都会被解析（包括 flags 里的可选字段），这样上层只取自己关心的部分即可。
/// </summary>
public sealed class TlReader
{
    public const uint VectorCtorId = 0x1cb5c415;

    private readonly byte[] _buf;
    private int _pos;
    private int _depth;

    public TlReader(byte[] buffer, int offset = 0)
    {
        _buf = buffer;
        _pos = offset;
    }

    public int Position => _pos;
    public int Remaining => _buf.Length - _pos;

    public void Skip(int n) => _pos += n;

    public uint ReadUInt()
    {
        Need(4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(_pos, 4));
        _pos += 4;
        return v;
    }

    public int ReadInt() => unchecked((int)ReadUInt());

    public long ReadLong()
    {
        Need(8);
        long v = BinaryPrimitives.ReadInt64LittleEndian(_buf.AsSpan(_pos, 8));
        _pos += 8;
        return v;
    }

    public double ReadDouble()
    {
        Need(8);
        double v = BinaryPrimitives.ReadDoubleLittleEndian(_buf.AsSpan(_pos, 8));
        _pos += 8;
        return v;
    }

    public byte[] ReadRaw(int n)
    {
        Need(n);
        var r = new byte[n];
        Buffer.BlockCopy(_buf, _pos, r, 0, n);
        _pos += n;
        return r;
    }

    /// <summary>TL 的 string / bytes 同一种编码：1~4 字节长度 + 内容 + 4 字节对齐填充。</summary>
    public byte[] ReadByteString()
    {
        Need(1);
        int b0 = _buf[_pos++];
        int len;
        if (b0 < 254)
        {
            len = b0;
        }
        else if (b0 == 254)
        {
            Need(3);
            len = _buf[_pos] | (_buf[_pos + 1] << 8) | (_buf[_pos + 2] << 16);
            _pos += 3;
        }
        else
        {
            Need(4);
            len = _buf[_pos] | (_buf[_pos + 1] << 8) | (_buf[_pos + 2] << 16) | (_buf[_pos + 3] << 24);
            _pos += 4;
        }
        if (len < 0) throw new TlException("字符串长度为负");
        var data = ReadRaw(len);
        // 对齐规则：短串（1 字节长度前缀）按 (len+1) 补齐到 4 的倍数；长串按 len 补齐
        int pad = b0 < 254 ? ((4 - ((len + 1) & 3)) & 3) : ((4 - (len & 3)) & 3);
        Skip(pad);
        return data;
    }

    public string ReadString()
    {
        var bytes = ReadByteString();
        return bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    public byte[] ReadRest()
    {
        var r = new byte[Remaining];
        Buffer.BlockCopy(_buf, _pos, r, 0, r.Length);
        _pos = _buf.Length;
        return r;
    }

    /// <summary>读一个顶层 Vector（有些方法直接返回 Vector&lt;User&gt; 这类）。</summary>
    public TlValue[] ReadNodeVector()
    {
        uint id = ReadUInt();
        if (id != VectorCtorId) throw new TlException($"期望顶层 vector，收到 0x{id:x8}");
        int count = ReadInt();
        if (count < 0 || count > 100000) throw new TlException($"vector 长度异常 {count}");
        var items = new TlValue[count];
        for (int i = 0; i < count; i++) items[i] = TlValue.FromNode(ReadNode());
        return items;
    }

    /// <summary>读一个构造函数并展开成节点。</summary>
    public TlNode ReadNode()
    {
        if (++_depth > 64) throw new TlException("TL 嵌套过深，可能数据已错位");
        try
        {
            uint id = ReadUInt();
            var ctor = TlSchema.Find(id) ?? throw new TlException($"未知构造函数 0x{id:x8}（offset {_pos - 4}）");
            var values = new TlValue[ctor.FieldCount];
            var flags = new long[3];
            var fields = ctor.Fields;
            for (int i = 0; i < fields.Length; i++)
            {
                ref readonly var f = ref fields[i];
                if (f.IsFlagWord)
                {
                    long w = (uint)ReadInt();
                    if (f.Group < 3) flags[f.Group] = w;
                    continue;
                }
                if (f.Group != 0)
                {
                    long word = f.Group < 3 ? flags[f.Group] : 0;
                    if ((word & (1L << f.Bit)) == 0)
                    {
                        values[i] = TlValue.Null;
                        continue;
                    }
                }
                values[i] = ReadValue(f.Kind, f.IsVector);
            }
            return new TlNode(ctor, values);
        }
        finally
        {
            _depth--;
        }
    }

    private TlValue ReadValue(char kind, bool isVector)
    {
        if (isVector)
        {
            uint vecId = ReadUInt();
            if (vecId != VectorCtorId)
                throw new TlException($"期望 vector#1cb5c415，实际 0x{vecId:x8}");
            int count = ReadInt();
            if (count < 0 || count > 4_000_000) throw new TlException($"vector 长度异常 {count}");
            var items = new TlValue[count];
            for (int i = 0; i < count; i++)
                items[i] = ReadValue(kind, false);
            return TlValue.FromVector(items);
        }

        switch (kind)
        {
            case 'i': return TlValue.FromInt(ReadInt());
            case 'l': return TlValue.FromLong(ReadLong());
            case 'q': return TlValue.FromBytes(ReadRaw(16));
            case 'Q': return TlValue.FromBytes(ReadRaw(32));
            case 'd': return TlValue.FromDouble(ReadDouble());
            case 's': return TlValue.FromString(ReadString());
            case 'b': return TlValue.FromBytes(ReadByteString());
            case 'B': return TlValue.FromBool(ReadUInt() == 0x997275b5);
            case 'T': return TlValue.FromBool(true);
            case '#': return TlValue.FromNode(ReadNode());
            default: throw new TlException($"未知字段类型 '{kind}'");
        }
    }

    private void Need(int n)
    {
        if (_pos + n > _buf.Length)
            throw new TlException($"数据不够：需要 {n} 字节，剩余 {_buf.Length - _pos}");
    }
}

/// <summary>TL 序列化器，只用于构造请求（字段不多，手写顺序更直观）。</summary>
public sealed class TlWriter
{
    private byte[] _buf = new byte[256];
    private int _len;

    public int Length => _len;

    private void Reserve(int n)
    {
        if (_len + n <= _buf.Length) return;
        int cap = _buf.Length;
        while (cap < _len + n) cap *= 2;
        Array.Resize(ref _buf, cap);
    }

    public TlWriter WriteUInt(uint v)
    {
        Reserve(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_len, 4), v);
        _len += 4;
        return this;
    }

    public TlWriter WriteInt(int v) => WriteUInt(unchecked((uint)v));

    public TlWriter WriteLong(long v)
    {
        Reserve(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buf.AsSpan(_len, 8), v);
        _len += 8;
        return this;
    }

    public TlWriter WriteDouble(double v)
    {
        Reserve(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buf.AsSpan(_len, 8), v);
        _len += 8;
        return this;
    }

    public TlWriter WriteRaw(ReadOnlySpan<byte> data)
    {
        Reserve(data.Length);
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
        return this;
    }

    public TlWriter WriteByteString(ReadOnlySpan<byte> data)
    {
        int len = data.Length;
        Reserve(4 + len + 3);
        int pad;
        if (len < 254)
        {
            _buf[_len++] = (byte)len;
            pad = (4 - ((len + 1) & 3)) & 3;
        }
        else if (len <= 0xFFFFFF)
        {
            _buf[_len++] = 254;
            _buf[_len++] = (byte)len;
            _buf[_len++] = (byte)(len >> 8);
            _buf[_len++] = (byte)(len >> 16);
            pad = (4 - (len & 3)) & 3;
        }
        else
        {
            _buf[_len++] = 255;
            _buf[_len++] = (byte)len;
            _buf[_len++] = (byte)(len >> 8);
            _buf[_len++] = (byte)(len >> 16);
            _buf[_len++] = (byte)(len >> 24);
            pad = (4 - (len & 3)) & 3;
        }
        data.CopyTo(_buf.AsSpan(_len));
        _len += len;
        for (int i = 0; i < pad; i++) _buf[_len++] = 0;
        return this;
    }

    public TlWriter WriteString(string? s) => WriteByteString(s == null ? ReadOnlySpan<byte>.Empty : Encoding.UTF8.GetBytes(s));

    public TlWriter WriteBool(bool v) => WriteUInt(v ? 0x997275b5u : 0xbc799737u);

    public TlWriter WriteInt128(ReadOnlySpan<byte> v)
    {
        if (v.Length != 16) throw new ArgumentException("int128 必须是 16 字节");
        return WriteRaw(v);
    }

    public TlWriter WriteInt256(ReadOnlySpan<byte> v)
    {
        if (v.Length != 32) throw new ArgumentException("int256 必须是 32 字节");
        return WriteRaw(v);
    }

    /// <summary>Vector 头 + 数量；元素自己写。</summary>
    public TlWriter WriteVectorHeader(int count) => WriteUInt(TlReader.VectorCtorId).WriteInt(count);

    public byte[] ToArray()
    {
        var r = new byte[_len];
        Buffer.BlockCopy(_buf, 0, r, 0, _len);
        return r;
    }

    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, _len);
}
