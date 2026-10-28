using System;

namespace tgdl.Models;

/// <summary>解析 chat_id:message_id 输入。</summary>
public static class TargetParser
{
    // TDLib 把服务端 message_id 存在高 20 位：MessageId(server_id) == server_id << 20
    private const int TdlibShift = 20;
    private const long TdlibMask = (1L << TdlibShift) - 1;

    public static bool TryParse(string? text, out long chatId, out int messageId, out string error)
    {
        chatId = 0;
        messageId = 0;
        error = "";
        var raw = (text ?? "").Trim();
        if (raw.Length == 0)
        {
            error = "请输入 chat_id:message_id";
            return false;
        }
        int idx = raw.IndexOf(':');
        if (idx < 0)
        {
            error = "格式应为 chat_id:message_id，例如 -1001234567890:42";
            return false;
        }
        if (!long.TryParse(raw.AsSpan(0, idx).Trim(), out chatId))
        {
            error = "chat_id 必须是整数（频道/超级群形如 -1001234567890）";
            return false;
        }
        if (!long.TryParse(raw.AsSpan(idx + 1).Trim(), out long mid) || mid <= 0 || mid > int.MaxValue)
        {
            error = "message_id 必须是正整数";
            return false;
        }

        // TDLib 风格的 id（低位 20 bit 全 0）自动还原成 MTProto 的原始 id
        if (mid >= (1L << TdlibShift) && (mid & TdlibMask) == 0)
            mid >>= TdlibShift;

        messageId = (int)mid;
        return true;
    }
}
