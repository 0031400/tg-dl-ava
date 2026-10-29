using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace tgdl.Mtproto;

public enum ProxyMode
{
    /// <summary>直连。</summary>
    None = 0,

    /// <summary>用手动填的地址。</summary>
    Manual = 1,

    /// <summary>跟随 Windows 系统代理设置。</summary>
    System = 2,
}

public enum ProxyKind
{
    Socks5 = 0,
    Http = 1,
    Vless = 2,
}

/// <summary>一条可用的代理配置（已经解析好的）。</summary>
public sealed record ProxyConfig(ProxyKind Kind, string Host, int Port, string? Username = null,
    string? Password = null, VlessConfig? Vless = null)
{
    public string Display => Kind == ProxyKind.Vless && Vless != null
        ? Vless.Display
        : $"{(Kind == ProxyKind.Socks5 ? "SOCKS5" : "HTTP")} {Host}:{Port}";
}

/// <summary>
/// 代理设置解析：手动配置直接读；系统代理从注册表读 Internet Settings。
/// </summary>
public static class ProxyResolver
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static ProxyConfig? Resolve(ProxyMode mode, ProxyKind kind, string host, int port, string? user,
        string? pass, string? vlessLink = null)
    {
        switch (mode)
        {
            case ProxyMode.None:
                return null;
            case ProxyMode.Manual:
                if (kind == ProxyKind.Vless)
                {
                    var vless = VlessConfig.Parse(vlessLink ?? "");
                    return new ProxyConfig(ProxyKind.Vless, vless.Server, vless.Port, null, null, vless);
                }
                if (string.IsNullOrWhiteSpace(host) || port <= 0) return null;
                return new ProxyConfig(kind, host.Trim(), port,
                    string.IsNullOrEmpty(user) ? null : user,
                    string.IsNullOrEmpty(pass) ? null : pass);
            case ProxyMode.System:
                return ResolveSystem();
            default:
                return null;
        }
    }

    /// <summary>读 Windows 的 Internet Settings（IE/WinINET 用的那一套）。</summary>
    public static ProxyConfig? ResolveSystem()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            if (key == null) return null;
            if (key.GetValue("ProxyEnable") is not int enabled || enabled == 0) return null;
            if (key.GetValue("ProxyServer") is not string server || string.IsNullOrWhiteSpace(server)) return null;

            // 可能是 "host:port"，也可能是 "http=h:p;https=h:p;socks=h:p"
            if (!server.Contains('='))
                return Parse(server, ProxyKind.Http);

            ProxyConfig? http = null, socks = null;
            foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var scheme = part.Substring(0, eq).Trim().ToLowerInvariant();
                var addr = part.Substring(eq + 1).Trim();
                if (scheme == "socks" || scheme == "socks5") socks = Parse(addr, ProxyKind.Socks5);
                else if (scheme == "http" || scheme == "https") http ??= Parse(addr, ProxyKind.Http);
            }
            return socks ?? http;
        }
        catch
        {
            return null;
        }
    }

    private static ProxyConfig? Parse(string addr, ProxyKind kind)
    {
        addr = addr.Trim();
        if (addr.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) addr = addr.Substring(7);
        if (addr.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
        {
            addr = addr.Substring(9);
            kind = ProxyKind.Socks5;
        }
        int colon = addr.LastIndexOf(':');
        if (colon <= 0) return null;
        if (!int.TryParse(addr.AsSpan(colon + 1), out var port) || port <= 0 || port > 65535) return null;
        var host = addr.Substring(0, colon).Trim('[', ']');
        return string.IsNullOrEmpty(host) ? null : new ProxyConfig(kind, host, port);
    }
}

/// <summary>
/// 在已经连上代理服务器的 TCP 流上，把隧道打通到目标 host:port。
/// 支持 SOCKS5（RFC 1928/1929）和 HTTP CONNECT。
/// </summary>
public static class ProxyTunnel
{
    public static async Task EstablishAsync(NetworkStream stream, string targetHost, int targetPort,
        ProxyConfig proxy, CancellationToken ct)
    {
        if (proxy.Kind == ProxyKind.Socks5)
            await Socks5Async(stream, targetHost, targetPort, proxy, ct).ConfigureAwait(false);
        else
            await HttpConnectAsync(stream, targetHost, targetPort, proxy, ct).ConfigureAwait(false);
    }

    private static async Task Socks5Async(NetworkStream stream, string host, int port, ProxyConfig proxy,
        CancellationToken ct)
    {
        bool needAuth = !string.IsNullOrEmpty(proxy.Username);
        // 打招呼：声明支持「无认证」和「用户名密码」
        var greeting = needAuth
            ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
            : new byte[] { 0x05, 0x01, 0x00 };
        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);
        var resp = await ReadExactlyAsync(stream, 2, ct).ConfigureAwait(false);
        if (resp[0] != 0x05) throw new MtpException("SOCKS5 代理返回了非 5 的版本号");

        if (resp[1] == 0x02)
        {
            if (!needAuth) throw new MtpException("SOCKS5 代理要求用户名密码，但设置里没填");
            var u = Encoding.UTF8.GetBytes(proxy.Username!);
            var p = Encoding.UTF8.GetBytes(proxy.Password ?? "");
            if (u.Length > 255 || p.Length > 255) throw new MtpException("SOCKS5 用户名/密码过长");
            var auth = new byte[3 + u.Length + p.Length];
            auth[0] = 0x01;
            auth[1] = (byte)u.Length;
            u.CopyTo(auth, 2);
            auth[2 + u.Length] = (byte)p.Length;
            p.CopyTo(auth, 3 + u.Length);
            await stream.WriteAsync(auth, ct).ConfigureAwait(false);
            var ar = await ReadExactlyAsync(stream, 2, ct).ConfigureAwait(false);
            if (ar[1] != 0x00) throw new MtpException("SOCKS5 认证失败");
        }
        else if (resp[1] != 0x00)
        {
            throw new MtpException($"SOCKS5 代理不接受我们的认证方式（method={resp[1]}）");
        }

        // CONNECT 请求：IP 直接用 ATYP=1，域名用 ATYP=3
        var req = new List<byte> { 0x05, 0x01, 0x00 };
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            req.Add(0x01);
            req.AddRange(ip.GetAddressBytes());
        }
        else
        {
            var hb = Encoding.UTF8.GetBytes(host);
            if (hb.Length > 255) throw new MtpException("SOCKS5 目标域名过长");
            req.Add(0x03);
            req.Add((byte)hb.Length);
            req.AddRange(hb);
        }
        req.Add((byte)(port >> 8));
        req.Add((byte)(port & 0xFF));
        await stream.WriteAsync(req.ToArray(), ct).ConfigureAwait(false);

        var head = await ReadExactlyAsync(stream, 4, ct).ConfigureAwait(false);
        if (head[0] != 0x05) throw new MtpException("SOCKS5 响应版本异常");
        if (head[1] != 0x00) throw new MtpException($"SOCKS5 连接失败：{Socks5Error(head[1])}");

        // 读掉 BND.ADDR + BND.PORT
        int addrLen = head[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => (await ReadExactlyAsync(stream, 1, ct).ConfigureAwait(false))[0],
            _ => throw new MtpException($"SOCKS5 响应地址类型异常：{head[3]}"),
        };
        await ReadExactlyAsync(stream, addrLen + 2, ct).ConfigureAwait(false);
    }

    private static string Socks5Error(byte code) => code switch
    {
        1 => "代理服务器内部错误",
        2 => "规则不允许",
        3 => "网络不可达",
        4 => "主机不可达",
        5 => "连接被拒绝",
        6 => "TTL 过期",
        7 => "不支持的命令",
        8 => "不支持的地址类型",
        _ => $"未知错误 {code}",
    };

    private static async Task HttpConnectAsync(NetworkStream stream, string host, int port, ProxyConfig proxy,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("CONNECT ").Append(host).Append(':').Append(port).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(host).Append(':').Append(port).Append("\r\n");
        if (!string.IsNullOrEmpty(proxy.Username))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{proxy.Username}:{proxy.Password}"));
            sb.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
        }
        sb.Append("Proxy-Connection: Keep-Alive\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);

        // 读到空行为止（最多 8KB，防止代理乱发东西）
        var buf = new byte[8192];
        int len = 0;
        while (len < buf.Length)
        {
            var one = await ReadExactlyAsync(stream, 1, ct).ConfigureAwait(false);
            buf[len++] = one[0];
            if (len >= 4 && buf[len - 4] == '\r' && buf[len - 3] == '\n' && buf[len - 2] == '\r' && buf[len - 1] == '\n')
                break;
        }
        var head = Encoding.ASCII.GetString(buf, 0, len);
        int sp = head.IndexOf(' ');
        if (sp < 0) throw new MtpException("HTTP 代理响应格式异常");
        var codeText = head.Substring(sp + 1, Math.Min(3, head.Length - sp - 1));
        if (codeText != "200")
            throw new MtpException($"HTTP 代理拒绝 CONNECT：{(head.Length > 40 ? head.Substring(0, 40).Replace("\r\n", " ") : head)}");
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(got, count - got), ct).ConfigureAwait(false);
            if (n <= 0) throw new MtpException("代理连接被关闭");
            got += n;
        }
        return buf;
    }
}
