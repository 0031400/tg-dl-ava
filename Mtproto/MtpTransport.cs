using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace tgdl.Mtproto;

/// <summary>
/// TCP 传输层，用 Telegram 的 intermediate 协议：
/// 连接后先发 0xEEEEEEEE，之后每个包是「4 字节小端长度 + 负载」。
/// </summary>
public sealed class MtpTransport : IAsyncDisposable
{
    private const uint IntermediateTag = 0xEEEEEEEE;
    private const int MaxFrame = 64 * 1024 * 1024;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private int _disposed;

    private MtpTransport(Socket socket, NetworkStream stream)
    {
        _socket = socket;
        _stream = stream;
    }

    public string Remote { get; private set; } = "";

    public static async Task<MtpTransport> ConnectAsync(string ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(ip), port), linked.Token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        var stream = new NetworkStream(socket, ownsSocket: true);
        var t = new MtpTransport(socket, stream) { Remote = $"{ip}:{port}" };
        var tag = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tag, IntermediateTag);
        await stream.WriteAsync(tag, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        return t;
    }

    public async ValueTask SendFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var buf = new byte[4 + payload.Length];
        if (MtpLog.Verbose) MtpLog.Write($"-> frame len={payload.Length}");
        BinaryPrimitives.WriteInt32LittleEndian(buf, payload.Length);
        payload.CopyTo(buf.AsMemory(4));
        await _stream.WriteAsync(buf, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await _stream.ReadExactlyAsync(lenBuf, ct).ConfigureAwait(false);
        int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (MtpLog.Verbose) MtpLog.Write($"<- frame len={len}");
        if (len <= 0 || len > MaxFrame)
            throw new MtpException($"收到异常的帧长度 {len}（连接可能已损坏）");
        var buf = new byte[len];
        await _stream.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        if (MtpLog.Verbose) MtpLog.Write($"<- payload {Convert.ToHexString(buf.AsSpan(0, Math.Min(len, 48)))}");
        return buf;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        try { _stream.Dispose(); } catch { /* ignore */ }
        try { _socket.Dispose(); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }
}
