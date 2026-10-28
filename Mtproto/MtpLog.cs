using System;

namespace tgdl.Mtproto;

/// <summary>极简日志出口：GUI 的日志面板和自检都往这里挂 Sink。</summary>
public static class MtpLog
{
    public static Action<string>? Sink;
    public static bool Verbose;

    public static void Write(string message)
    {
        try { Sink?.Invoke(message); } catch { /* 日志本身不能抛 */ }
    }

    public static void Write(string context, Exception ex)
    {
        Write($"{context}: {ex.GetType().Name}: {ex.Message}");
    }
}
