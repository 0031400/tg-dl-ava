using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tgdl.Storage;

/// <summary>应用设置（data/settings.json）。</summary>
public sealed class AppSettings
{
    public int ApiId { get; set; } = 2040;
    public string ApiHash { get; set; } = "b18441a1ff607e10a989891a5462e627";

    /// <summary>同时使用多少条 auth key（每条 key 一条 TCP 连接）。</summary>
    public int AuthKeyCount { get; set; } = 3;

    /// <summary>每条 key 的并发请求数。总并发 = AuthKeyCount × ConcurrencyPerKey。</summary>
    public int ConcurrencyPerKey { get; set; } = 4;

    /// <summary>分片大小（字节），512 KiB 是 Telegram 的推荐上限。</summary>
    public int ChunkSize { get; set; } = 512 * 1024;

    /// <summary>下载目录，空表示 exe 同目录的 downloads。</summary>
    public string DownloadDir { get; set; } = "";

    /// <summary>撞到 FLOOD_WAIT 时自动降低该连接的并发。</summary>
    public bool AdaptiveFlood { get; set; } = true;

    /// <summary>文件下载完成后是否自动打开所在文件夹。</summary>
    public bool OpenFolderWhenDone { get; set; }

    [JsonIgnore]
    public string EffectiveDownloadDir =>
        string.IsNullOrWhiteSpace(DownloadDir) ? AppPaths.DefaultDownloadDir : DownloadDir;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>与 Python 版 tg-dl 完全兼容的 authkeys.json 结构。</summary>
public sealed class AuthKeyFile
{
    [JsonPropertyName("home_dc")] public int? HomeDc { get; set; }
    [JsonPropertyName("home_ip")] public string? HomeIp { get; set; }
    [JsonPropertyName("home_port")] public int? HomePort { get; set; }
    [JsonPropertyName("keys")] public Dictionary<string, List<string>> Keys { get; set; } = new();
}

public sealed class EntityRecord
{
    public int Kind { get; set; }
    public long Id { get; set; }
    public long AccessHash { get; set; }
    public string Title { get; set; } = "";
    public string? Username { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AuthKeyFile))]
[JsonSerializable(typeof(List<DownloadTask>))]
[JsonSerializable(typeof(Dictionary<string, EntityRecord>))]
internal sealed partial class AppJson : JsonSerializerContext
{
}

public static class JsonIo
{
    public static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize(text, info);
        }
        catch
        {
            return null;
        }
    }

    public static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        try
        {
            var text = JsonSerializer.Serialize(value, info);
            AppPaths.WriteAllTextAtomic(path, text);
        }
        catch
        {
            // 写配置失败不致命
        }
    }
}

public static class SettingsStore
{
    private static readonly object Gate = new();
    private static AppSettings? _current;

    public static AppSettings Current
    {
        get
        {
            lock (Gate)
            {
                if (_current != null) return _current;
                var loaded = JsonIo.Read(AppPaths.SettingsFile, AppJson.Default.AppSettings);
                if (loaded == null)
                {
                    loaded = new AppSettings();
                    JsonIo.Write(AppPaths.SettingsFile, loaded, AppJson.Default.AppSettings);
                }
                _current = loaded;
                return _current;
            }
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            _current = settings;
            JsonIo.Write(AppPaths.SettingsFile, settings, AppJson.Default.AppSettings);
        }
    }
}
