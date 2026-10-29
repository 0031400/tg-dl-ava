using System;
using System.IO;

namespace tgdl.Storage;

/// <summary>所有数据都放在 exe 同目录的 data 文件夹里。</summary>
public static class AppPaths
{
    public static string DataDir { get; } = EnsureDir(Path.Combine(AppContext.BaseDirectory, "data"));

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string AccountsFile => Path.Combine(DataDir, "accounts.json");
    public static string DcIpFile => Path.Combine(DataDir, "dc-ip.json");
    /// <summary>老版本的单 key 池，只用于一次性迁移。</summary>
    public static string LegacyAuthKeysFile => Path.Combine(DataDir, "authkeys.json");
    public static string TasksFile => Path.Combine(DataDir, "tasks.json");
    public static string EntitiesFile => Path.Combine(DataDir, "entities.json");
    public static string LogFile => Path.Combine(DataDir, "app.log");

    public static string DefaultDownloadDir => EnsureDir(Path.Combine(AppContext.BaseDirectory, "downloads"));

    private static string EnsureDir(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* ignore */ }
        return path;
    }

    /// <summary>先写临时文件再替换，避免断电时把配置写坏。</summary>
    public static void WriteAllTextAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
