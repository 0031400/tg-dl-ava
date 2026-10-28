using System;
using System.IO;
using System.Text;
using Avalonia;
using tgdl.Mtproto;

namespace tgdl;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--selftest" || args[0] == "--probe" || args[0] == "--netdebug" || args[0] == "--cryptotest" || args[0] == "--prototest" || args[0] == "--teststore" || args[0] == "--sessiontest"))
        {
            SelfTest.Run(args);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}

/// <summary>
/// 命令行自检（WinExe 没有 stdout，结果写到 data/selftest.log）：
///   tgdl.exe --selftest              连 DC 做一次 DH，再调 help.getConfig
///   tgdl.exe --probe &lt;hexkey&gt; &lt;dc&gt;   用给定 auth key 连上去，打印会话列表前几条
/// </summary>
internal static class SelfTest
{
    public static void Run(string[] args)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dir);
        var logPath = Path.Combine(dir, "selftest.log");
        using var log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(log);
        Console.SetError(log);
        MtpLog.Sink = m => log.WriteLine("  [mtp] " + m);
        MtpLog.Verbose = Environment.GetEnvironmentVariable("TGDL_VERBOSE") == "1";

        try
        {
            var task = args[0] switch
            {
                "--probe" => ProbeAsync(args, log),
                "--netdebug" => NetDebugAsync(log),
                "--cryptotest" => CryptoTestAsync(log),
                "--prototest" => ProtoTestAsync(log),
                "--teststore" => StoreTestAsync(log),
                "--sessiontest" => SessionTestAsync(log),
                _ => BasicAsync(log),
            };
            task.GetAwaiter().GetResult();
            log.WriteLine("== DONE ==");
        }
        catch (Exception ex)
        {
            log.WriteLine("!! 失败: " + ex);
            log.WriteLine("== FAILED ==");
        }
    }

    private static async System.Threading.Tasks.Task BasicAsync(TextWriter log)
    {
        log.WriteLine($"schema 构造函数数量: {TlSchema.All.Count}");
        var opt = new MtpClientOptions();

        foreach (var dc in new[] { 2, 4, 5 })
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            log.WriteLine($"--- 连 DC{dc} 做 DH ---");
            await using var conn = await MtpConnection.HandshakeAsync(dc, opt, default);
            sw.Stop();
            log.WriteLine($"DH 成功 {sw.ElapsedMilliseconds} ms  auth_key_id=0x{conn.Key.Id:x16}  时间偏移={conn.TimeOffsetSeconds}s  远端={conn.Remote}");

            sw.Restart();
            var cfg = await MtpApi.GetConfigAsync(conn, default);
            sw.Stop();
            log.WriteLine($"help.getConfig 成功 {sw.ElapsedMilliseconds} ms  -> {cfg.Name}");

            try
            {
                var d = await MtpApi.GetDialogsAsync(conn, 5, null, 0, 0, default);
                log.WriteLine($"getDialogs 竟然成功了：{d.Entities.Count} 个实体");
            }
            catch (RpcException rex)
            {
                log.WriteLine($"getDialogs 返回 rpc_error（说明请求序列化没问题）：{rex.Code} {rex.Text}");
            }
            catch (Exception ex)
            {
                log.WriteLine($"getDialogs 其它错误：{ex.GetType().Name}: {ex.Message}");
            }

            var dcOpts = cfg["dc_options"].AsVector();
            log.WriteLine($"  dc_options 数量 = {dcOpts.Length}");
            foreach (var v in dcOpts)
            {
                var n = v.AsNode();
                if (n == null) continue;
                log.WriteLine($"  DC{n["id"].AsInt()} {n["ip_address"].AsString()}:{n["port"].AsInt()} media={n["media_only"].AsBool()}");
            }
        }
    }

    private static System.Threading.Tasks.Task CryptoTestAsync(TextWriter log)
    {
        var key = new byte[32];
        for (int i = 0; i < 32; i++) key[i] = (byte)i;
        var iv = new byte[32];
        for (int i = 0; i < 32; i++) iv[i] = (byte)(32 + i);
        var data = new byte[48];
        for (int i = 0; i < 48; i++) data[i] = (byte)i;
        var enc = (byte[])data.Clone();
        MtpCrypto.AesIgeEncrypt(enc, key, iv);
        log.WriteLine("enc = " + Convert.ToHexString(enc).ToLowerInvariant());
        var dec = (byte[])enc.Clone();
        MtpCrypto.AesIgeDecrypt(dec, key, iv);
        log.WriteLine("dec = " + Convert.ToHexString(dec).ToLowerInvariant());

        var sn = new byte[16];
        var nn = new byte[32];
        for (int i = 0; i < 16; i++) sn[i] = (byte)i;
        for (int i = 0; i < 32; i++) nn[i] = (byte)(16 + i);
        var (dk, div) = DhHandshake.DeriveKeyIv(sn, nn);
        log.WriteLine("dkey = " + Convert.ToHexString(dk).ToLowerInvariant());
        log.WriteLine("div  = " + Convert.ToHexString(div).ToLowerInvariant());
        return System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// 用一个刚握手、未登录的 key 去调用各个方法：只要服务端回的是 401 AUTH_KEY_UNREGISTERED
    /// 这类 rpc_error，就说明我们的请求 TL 序列化是对的（否则会得到 -404 传输层错误）。
    /// </summary>
    private static async System.Threading.Tasks.Task ProtoTestAsync(TextWriter log)
    {
        var opt = new MtpClientOptions();
        await using var conn = await MtpConnection.HandshakeAsync(2, opt, default);
        log.WriteLine($"DC2 握手成功 key=0x{conn.Key.Id:x16}");

        async System.Threading.Tasks.Task Probe(string name, Func<System.Threading.Tasks.Task> action)
        {
            try
            {
                await action();
                log.WriteLine($"{name}: 意外成功");
            }
            catch (RpcException ex)
            {
                log.WriteLine($"{name}: rpc_error {ex.Code} {ex.Text}  -> 请求序列化 OK");
            }
            catch (Exception ex)
            {
                log.WriteLine($"{name}: 失败 {ex.GetType().Name}: {ex.Message}");
            }
        }

        await Probe("messages.getDialogs", () => MtpApi.GetDialogsAsync(conn, 5, null, 0, 0, default));
        await Probe("messages.getHistory(inputPeerSelf)", () =>
            MtpApi.GetMessageAsync(conn, new PeerRef(PeerKind.User, 1, 0), 1, default));
        await Probe("channels.getMessages", () =>
            MtpApi.GetMessageAsync(conn, new PeerRef(PeerKind.Channel, 1000000000001L, 1), 1, default));

        // upload.getFile：随便构造一个 location
        var w = new TlWriter();
        w.WriteUInt(TlIds.InputDocumentFileLocation).WriteLong(1).WriteLong(1).WriteByteString(new byte[8]).WriteString("");
        await Probe("upload.getFile", () => MtpApi.GetFileAsync(conn, w.ToArray(), 0, 4096, default));
    }

    /// <summary>验证「启动/停止」的状态机：没有 key 时必须给出可操作的提示。</summary>
    private static async System.Threading.Tasks.Task SessionTestAsync(TextWriter log)
    {
        var file = tgdl.Storage.AppPaths.AuthKeysFile;
        if (System.IO.File.Exists(file)) System.IO.File.Copy(file, file + ".bak", true);
        try
        {
            tgdl.Storage.AuthKeyStore.ClearAll();
            await using var engine = new tgdl.Download.DownloadEngine();
            log.WriteLine($"初始状态：{engine.Session} / {engine.SessionText}");
            try
            {
                await engine.StartSessionAsync(new Progress<string>(m => log.WriteLine("  " + m)), default);
                log.WriteLine($"启动结果：{engine.Session} / {engine.SessionText}");
            }
            catch (tgdl.Mtproto.NeedAuthKeyException nex)
            {
                log.WriteLine($"缺少 key 的提示（DC={nex.DcId?.ToString() ?? "null"}）：{nex.Message}");
                log.WriteLine($"状态：{engine.Session} / {engine.SessionText}");
            }
            await engine.StopSessionAsync();
            log.WriteLine($"停止后状态：{engine.Session} / {engine.SessionText}");
            log.WriteLine($"连接池连接数：{engine.Pool.ConnectionCount}");
        }
        finally
        {
            if (System.IO.File.Exists(file + ".bak"))
            {
                System.IO.File.Copy(file + ".bak", file, true);
                System.IO.File.Delete(file + ".bak");
            }
            else if (System.IO.File.Exists(file))
            {
                System.IO.File.Delete(file);
            }
            tgdl.Storage.AuthKeyStore.Reload();
        }
    }

    /// <summary>验证 authkeys.json 的读写（与 Python 版格式互通）。</summary>
    private static System.Threading.Tasks.Task StoreTestAsync(TextWriter log)
    {
        var file = tgdl.Storage.AppPaths.AuthKeysFile;
        if (System.IO.File.Exists(file)) System.IO.File.Copy(file, file + ".bak", true);
        try
        {
            tgdl.Storage.AuthKeyStore.ClearAll();
            var k1 = new byte[256];
            var k2 = new byte[256];
            System.Security.Cryptography.RandomNumberGenerator.Fill(k1);
            System.Security.Cryptography.RandomNumberGenerator.Fill(k2);
            tgdl.Mtproto.AuthKeyManager.Import(Convert.ToHexString(k1), 5, true);
            tgdl.Storage.AuthKeyStore.Add(4, Convert.ToHexString(k2).ToLowerInvariant());

            log.WriteLine("authkeys.json:");
            log.WriteLine(System.IO.File.ReadAllText(file));
            log.WriteLine($"解析：home_dc={tgdl.Storage.AuthKeyStore.HomeDc} home_ip={tgdl.Storage.AuthKeyStore.HomeIp} " +
                          $"总条数={tgdl.Storage.AuthKeyStore.TotalCount} DC4={tgdl.Storage.AuthKeyStore.CountFor(4)} " +
                          $"主key={tgdl.Storage.AuthKeyStore.PrimaryKeyHex?.Substring(0, 8)}");
            var key = tgdl.Mtproto.AuthKey.FromHex(tgdl.Storage.AuthKeyStore.PrimaryKeyHex!);
            log.WriteLine($"主 key 重新解析后 id=0x{key.Id:x16}");
        }
        finally
        {
            if (System.IO.File.Exists(file + ".bak"))
            {
                System.IO.File.Copy(file + ".bak", file, true);
                System.IO.File.Delete(file + ".bak");
            }
            else if (System.IO.File.Exists(file))
            {
                System.IO.File.Delete(file);
            }
            tgdl.Storage.AuthKeyStore.Reload();
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private static async System.Threading.Tasks.Task NetDebugAsync(TextWriter log)
    {
        var t = await MtpTransport.ConnectAsync("149.154.167.51", 443, TimeSpan.FromSeconds(10), default);
        log.WriteLine($"已连接 {t.Remote}");
        var nonce = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        var w = new TlWriter();
        w.WriteUInt(TlIds.ReqPqMulti).WriteRaw(nonce);
        var body = w.ToArray();
        log.WriteLine($"body({body.Length}) = {Convert.ToHexString(body)}");

        var frame = new byte[20 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(0), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(8), DateTimeOffset.UtcNow.ToUnixTimeSeconds() << 32);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(16), body.Length);
        body.CopyTo(frame, 20);
        log.WriteLine($"frame({frame.Length}) = {Convert.ToHexString(frame)}");
        try
        {
            var hs = await DhHandshake.RunAsync(t, default);
            log.WriteLine($"DH 成功 key_id=0x{hs.Key.Id:x16} offset={hs.TimeOffsetSeconds}");
        }
        catch (Exception ex)
        {
            log.WriteLine("DH 失败: " + ex.GetType().Name + " " + ex.Message);
        }
        await t.DisposeAsync();
    }

    private static async System.Threading.Tasks.Task ProbeAsync(string[] args, TextWriter log)
    {
        if (args.Length < 3)
        {
            log.WriteLine("用法: --probe <authkey-hex> <dc>");
            return;
        }
        var key = AuthKey.FromHex(args[1]);
        int dc = int.Parse(args[2]);
        var opt = new MtpClientOptions();

        await using var conn = await MtpConnection.ConnectWithKeyAsync(dc, key, 0, opt, default);
        log.WriteLine($"已连接 DC{dc} auth_key_id=0x{conn.Key.Id:x16}");

        var cfg = await MtpApi.GetConfigAsync(conn, default);
        log.WriteLine($"help.getConfig -> {cfg.Name}");

        var dialogs = await MtpApi.GetDialogsAsync(conn, 20, null, 0, 0, default);
        log.WriteLine($"getDialogs: 实体 {dialogs.Entities.Count} 个，总数 {dialogs.Total}");
        int shown = 0;
        foreach (var e in dialogs.Entities)
        {
            if (shown++ >= 20) break;
            log.WriteLine($"  {e.Peer.Kind,-8} id={e.Peer.Id,-16} hash={e.Peer.AccessHash,-20} {e.Title}");
        }
    }
}
