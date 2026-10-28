using System;
using System.Diagnostics;
using System.IO;

namespace tgdl.Interop;

/// <summary>调用系统默认程序打开文件 / 定位到资源管理器。</summary>
public static class ShellHelper
{
    public static void OpenFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("文件不存在", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void RevealInExplorer(string path)
    {
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        else
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
    }

    public static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
