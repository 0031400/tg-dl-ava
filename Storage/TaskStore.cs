using System;
using System.Collections.Generic;
using System.Linq;

namespace tgdl.Storage;

public enum DownloadStatus
{
    Queued = 0,
    Resolving = 1,
    Downloading = 2,
    Paused = 3,
    Done = 4,
    Failed = 5,
}

/// <summary>一条下载任务（持久化在 data/tasks.json）。</summary>
public sealed class DownloadTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long ChatId { get; set; }
    public int MessageId { get; set; }

    /// <summary>原始输入，形如 -1001234567890:42。</summary>
    public string Input { get; set; } = "";

    public string Title { get; set; } = "";
    public long Size { get; set; }
    public long Done { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public string? Error { get; set; }

    /// <summary>最终文件路径。</summary>
    public string? LocalPath { get; set; }

    /// <summary>预取到的 InputFileLocation（base64），续传时不用重新取消息。</summary>
    public string? Location { get; set; }

    public int DcId { get; set; }

    /// <summary>累计"正在下载"的毫秒数（暂停/排队的时间不算）。</summary>
    public long ElapsedMs { get; set; }

    /// <summary>已完成的分片区间，如 "0-12,14-20"。</summary>
    public string? ChunkMap { get; set; }

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public bool IsFinished => Status is DownloadStatus.Done;
    public bool IsActive => Status is DownloadStatus.Resolving or DownloadStatus.Downloading;

    public double Progress => Size <= 0 ? 0 : Math.Clamp((double)Done / Size, 0, 1);

    public DownloadTask Clone() => (DownloadTask)MemberwiseClone();
}

public static class TaskStore
{
    private static readonly object Gate = new();
    private static List<DownloadTask> _items = new();

    public static void Load()
    {
        lock (Gate)
        {
            _items = JsonIo.Read(AppPaths.TasksFile, AppJson.Default.ListDownloadTask) ?? new List<DownloadTask>();
            // 上次退出时还在跑的任务，重启后当成暂停
            foreach (var t in _items)
            {
                if (t.IsActive) t.Status = DownloadStatus.Paused;
            }
        }
    }

    public static List<DownloadTask> Snapshot()
    {
        lock (Gate) return _items.Select(t => t.Clone()).ToList();
    }

    public static void Replace(IEnumerable<DownloadTask> items)
    {
        lock (Gate) _items = items.Select(t => t.Clone()).ToList();
        Save();
    }

    public static void Add(DownloadTask task)
    {
        lock (Gate) _items.Add(task.Clone());
        Save();
    }

    public static void Remove(string id)
    {
        lock (Gate) _items.RemoveAll(t => t.Id == id);
        Save();
    }

    public static DownloadTask? Find(string id)
    {
        lock (Gate) return _items.FirstOrDefault(t => t.Id == id)?.Clone();
    }

    public static void Update(string id, Action<DownloadTask> mutate)
    {
        lock (Gate)
        {
            var t = _items.FirstOrDefault(x => x.Id == id);
            if (t == null) return;
            mutate(t);
            t.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        Save();
    }

    private static int _saveCounter;

    public static void Save()
    {
        List<DownloadTask> copy;
        lock (Gate) copy = _items.Select(t => t.Clone()).ToList();
        JsonIo.Write(AppPaths.TasksFile, copy, AppJson.Default.ListDownloadTask);
        _saveCounter++;
    }
}
