using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace tgdl.Mtproto;

/// <summary>TL 字段类型。线上编码见 <see cref="TlReader"/>。</summary>
public enum TlKind : byte
{
    Null,
    Int,        // i
    Long,       // l
    Int128,     // q
    Int256,     // Q
    Double,     // d
    String,     // s
    Bytes,      // b
    Bool,       // B
    True,       // T  (flags 位标记，线上不占字节)
    Node,       // #  构造函数，先读 4 字节 id 再展开
    Vector,     // *   前面带 vector 头 + 数量
}

/// <summary>一个 TL 值。用结构体避免海量小对象。</summary>
public readonly struct TlValue
{
    public readonly TlKind Kind;
    public readonly long Num;
    public readonly double Dbl;
    public readonly object? Obj;

    private TlValue(TlKind kind, long num, double dbl, object? obj)
    {
        Kind = kind;
        Num = num;
        Dbl = dbl;
        Obj = obj;
    }

    public static readonly TlValue Null = new(TlKind.Null, 0, 0, null);
    public static TlValue FromInt(long v) => new(TlKind.Int, v, 0, null);
    public static TlValue FromLong(long v) => new(TlKind.Long, v, 0, null);
    public static TlValue FromDouble(double v) => new(TlKind.Double, 0, v, null);
    public static TlValue FromString(string v) => new(TlKind.String, 0, 0, v);
    public static TlValue FromBytes(byte[] v) => new(TlKind.Bytes, 0, 0, v);
    public static TlValue FromBool(bool v) => new(TlKind.Bool, v ? 1 : 0, 0, null);
    public static TlValue FromNode(TlNode v) => new(TlKind.Node, 0, 0, v);
    public static TlValue FromVector(TlValue[] v) => new(TlKind.Vector, 0, 0, v);

    public bool IsNull => Kind == TlKind.Null;

    /// <summary>整数取值。缺失的可选字段（Null）按 0 处理。</summary>
    public long AsLong() => Kind switch
    {
        TlKind.Null => 0,
        TlKind.Int or TlKind.Long or TlKind.Bool => Num,
        TlKind.Double => (long)Dbl,
        _ => throw new TlException($"期望整数，实际是 {Kind}"),
    };

    public int AsInt() => (int)AsLong();

    public bool AsBool() => AsLong() != 0;

    public double AsDouble() => Kind switch
    {
        TlKind.Null => 0,
        TlKind.Double => Dbl,
        TlKind.Int or TlKind.Long => Num,
        _ => throw new TlException($"期望浮点，实际是 {Kind}"),
    };

    public string AsString() => Obj as string ?? (Kind == TlKind.Null ? "" : throw new TlException($"期望字符串，实际是 {Kind}"));

    public byte[] AsBytes() => Obj as byte[] ?? (Kind == TlKind.Null ? Array.Empty<byte>() : throw new TlException($"期望字节串，实际是 {Kind}"));

    public TlNode? AsNode() => Obj as TlNode;

    public TlValue[] AsVector() => Obj as TlValue[] ?? Array.Empty<TlValue>();

    public override string ToString() => Kind switch
    {
        TlKind.Null => "null",
        TlKind.String => Obj as string ?? "",
        TlKind.Bytes => $"<{((byte[]?)Obj)?.Length ?? 0} bytes>",
        TlKind.Node => Obj is TlNode n ? n.Name : "node",
        TlKind.Vector => $"<vector {((TlValue[]?)Obj)?.Length ?? 0}>",
        TlKind.Bool => Num != 0 ? "true" : "false",
        _ => Num.ToString(CultureInfo.InvariantCulture),
    };
}

public sealed class TlException : Exception
{
    public TlException(string message) : base(message) { }
}

/// <summary>一个构造函数在线上展开后得到的东西。</summary>
public sealed class TlNode
{
    private readonly TlValue[] _values;
    private Dictionary<string, int>? _index;

    internal TlNode(TlConstructor ctor, TlValue[] values)
    {
        Ctor = ctor;
        _values = values;
    }

    public TlConstructor Ctor { get; }
    public uint Id => Ctor.Id;
    public string Name => Ctor.Name;
    public int Count => _values.Length;

    public int IndexOf(string field)
    {
        if (_index == null)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < Ctor.Fields.Length; i++)
                map[Ctor.Fields[i].Name] = i;
            _index = map;
        }
        return _index.TryGetValue(field, out var idx) ? idx : -1;
    }

    public TlValue this[string field]
    {
        get
        {
            int i = IndexOf(field);
            return i < 0 ? TlValue.Null : _values[i];
        }
    }

    public bool Has(string field)
    {
        int i = IndexOf(field);
        return i >= 0 && !_values[i].IsNull;
    }

    public override string ToString() => $"{Name}#{Id:x8}";
}

public struct TlField
{
    public string Name;
    public char Kind;      // i l q Q d s b B T # (* 由 IsVector 表示)
    public bool IsVector;
    public bool IsFlagWord;
    public byte Group;     // 0=总是存在, 1=flags, 2=flags2
    public byte Bit;
}

public sealed class TlConstructor
{
    public TlConstructor(uint id, string name, TlField[] fields)
    {
        Id = id;
        Name = name;
        Fields = fields;
        FieldCount = fields.Length;
    }

    public uint Id { get; }
    public string Name { get; }
    public TlField[] Fields { get; }
    public int FieldCount { get; }

    public override string ToString() => $"{Name}#{Id:x8}";
}

/// <summary>
/// 从 tools/gen_tl_schema.py 生成的 TlSchema.txt 加载「构造函数 → 字段表」。
/// 只要拿到 id 就能把整棵响应树展开，不用为每个类型手写解析代码。
/// </summary>
public static class TlSchema
{
    private static readonly Dictionary<uint, TlConstructor> ById = new();
    private static TlConstructor[] _all = Array.Empty<TlConstructor>();
    private static readonly object Gate = new();
    private static bool _loaded;

    public static IReadOnlyList<TlConstructor> All
    {
        get { EnsureLoaded(); return _all; }
    }

    public static TlConstructor? Find(uint id)
    {
        EnsureLoaded();
        return ById.TryGetValue(id, out var c) ? c : null;
    }

    public static TlConstructor Require(uint id)
    {
        return Find(id) ?? throw new TlException($"schema 里没有构造函数 0x{id:x8}");
    }

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            var asm = typeof(TlSchema).Assembly;
            string? name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("TlSchema.txt", StringComparison.OrdinalIgnoreCase));
            if (name == null) throw new TlException("找不到内嵌资源 TlSchema.txt");
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            Load(reader.ReadToEnd());
            _loaded = true;
        }
    }

    /// <summary>解析 schema 文本。每行：idhex|name|field field ...</summary>
    public static void Load(string text)
    {
        var list = new List<TlConstructor>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int p1 = line.IndexOf('|');
            int p2 = line.IndexOf('|', p1 + 1);
            if (p1 < 0 || p2 < 0) continue;
            uint id = uint.Parse(line.AsSpan(0, p1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string ctorName = line.Substring(p1 + 1, p2 - p1 - 1);
            var fieldText = line.Substring(p2 + 1).Trim();
            var parts = fieldText.Length == 0
                ? Array.Empty<string>()
                : fieldText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var fields = new TlField[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                fields[i] = ParseField(parts[i]);
            var ctor = new TlConstructor(id, ctorName, fields);
            list.Add(ctor);
            ById[id] = ctor;
        }
        _all = list.ToArray();
    }

    private static TlField ParseField(string token)
    {
        // 形如 "1.5?*#:media" 或 "!1:flags" 或 "l:id"
        int colon = token.LastIndexOf(':');
        string spec = colon >= 0 ? token.Substring(0, colon) : token;
        string name = colon >= 0 ? token.Substring(colon + 1) : spec;

        var f = new TlField { Name = name, Group = 0, Bit = 0 };

        if (spec.Length > 0 && spec[0] == '!')
        {
            f.IsFlagWord = true;
            f.Group = byte.Parse(spec.AsSpan(1), CultureInfo.InvariantCulture);
            f.Kind = '#';
            return f;
        }

        int q = spec.IndexOf('?');
        if (q > 0)
        {
            var flag = spec.Substring(0, q);
            spec = spec.Substring(q + 1);
            int dot = flag.IndexOf('.');
            if (dot > 0)
            {
                f.Group = byte.Parse(flag.AsSpan(0, dot), CultureInfo.InvariantCulture);
                f.Bit = byte.Parse(flag.AsSpan(dot + 1), CultureInfo.InvariantCulture);
            }
        }

        if (spec.Length > 0 && spec[0] == '*')
        {
            f.IsVector = true;
            spec = spec.Substring(1);
        }

        f.Kind = spec.Length > 0 ? spec[0] : '#';
        return f;
    }
}
