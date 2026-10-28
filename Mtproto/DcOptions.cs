using System;
using System.Collections.Generic;

namespace tgdl.Mtproto;

/// <summary>MTProto 层异常。</summary>
public class MtpException : Exception
{
    public MtpException(string message) : base(message) { }
    public MtpException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>服务端返回的 rpc_error。</summary>
public sealed class RpcException : MtpException
{
    public RpcException(int code, string text) : base($"{code}: {text}")
    {
        Code = code;
        Text = text;
    }

    public int Code { get; }
    public string Text { get; }

    /// <summary>FLOOD_WAIT_X：需要等 X 秒。</summary>
    public bool IsFloodWait => Code == 420 || Text.StartsWith("FLOOD_WAIT", StringComparison.Ordinal);

    public int FloodSeconds
    {
        get
        {
            const string prefix = "FLOOD_WAIT_";
            if (!Text.StartsWith(prefix, StringComparison.Ordinal)) return 1;
            var tail = Text.AsSpan(prefix.Length);
            int n = 0;
            foreach (var ch in tail)
            {
                if (ch < '0' || ch > '9') break;
                n = n * 10 + (ch - '0');
                if (n > 86400) break;
            }
            return n <= 0 ? 1 : n;
        }
    }

    /// <summary>FILE_MIGRATE_X / PHONE_MIGRATE_X / NETWORK_MIGRATE_X：换 DC。</summary>
    public int? MigrateDc
    {
        get
        {
            if (!Text.StartsWith("FILE_MIGRATE_", StringComparison.Ordinal) &&
                !Text.StartsWith("NETWORK_MIGRATE_", StringComparison.Ordinal) &&
                !Text.StartsWith("PHONE_MIGRATE_", StringComparison.Ordinal))
                return null;
            int idx = Text.LastIndexOf('_');
            return int.TryParse(Text.AsSpan(idx + 1), out var dc) ? dc : null;
        }
    }
}

/// <summary>把服务端在传输层返回的 4 字节错误码翻译成人话。</summary>
public static class MtpTransportError
{
    public static string Describe(int code) => code switch
    {
        -404 => "-404 auth key 不存在 / 请求不被接受（握手阶段常见于被限流）",
        -429 => "-429 请求太频繁，被限流",
        -444 => "-444 DC 地址无效",
        -5 => "-5 服务端主动关闭了连接",
        _ => $"{code}（未知的传输层错误码）",
    };
}

/// <summary>缺少可用的 auth key：界面据此提示用户去导入。</summary>
public sealed class NeedAuthKeyException : MtpException
{
    public NeedAuthKeyException(int? dcId, string message) : base(message) => DcId = dcId;

    public int? DcId { get; }
}

/// <summary>salt 不对，需要换 salt 重发同一条消息。</summary>
internal sealed class BadSaltException : Exception
{
    public BadSaltException() : base("bad_server_salt") { }
}

/// <summary>服务端要求重试（bad_msg_notification）。</summary>
internal sealed class RetryException : Exception
{
    public RetryException(string why) : base(why) { }
}

/// <summary>DC 地址表。Telegram 的正式 DC，每个 DC 给几个备用 IP。</summary>
public static class DcOptions
{
    private static readonly Dictionary<int, string[]> Ips = new()
    {
        // 取自 help.getConfig 的 dc_options（生产 DC）
        [1] = new[] { "149.154.175.51", "149.154.175.50" },
        [2] = new[] { "149.154.167.41", "149.154.167.51" },
        [3] = new[] { "149.154.175.100" },
        [4] = new[] { "149.154.167.91", "149.154.167.92" },
        [5] = new[] { "91.108.56.196", "91.108.56.130", "149.154.171.5" },
    };

    public const int Port = 443;

    public static IReadOnlyList<string> Addresses(int dcId)
    {
        return Ips.TryGetValue(dcId, out var list) ? list : Array.Empty<string>();
    }

    public static bool IsKnown(int dcId) => Ips.ContainsKey(dcId);
}
