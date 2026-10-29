using System;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using tgdl.Download;
using tgdl.Storage;

namespace tgdl.ViewModels;

/// <summary>任务列表里的一行。</summary>
public sealed partial class TaskItemViewModel : ObservableObject
{
    private readonly Action<TaskItemViewModel> _onStop;
    private readonly Action<TaskItemViewModel> _onResume;
    private readonly Action<TaskItemViewModel, bool> _onRemove;
    private readonly Action<TaskItemViewModel> _onOpen;
    private readonly Action<TaskItemViewModel> _onReveal;

    private DownloadTask _task;
    private double _speed;
    private long _windowBytes;
    private string _connText = "";

    private static readonly IBrush QueuedBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush PausedBrush = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush DoneBrush = new SolidColorBrush(Color.Parse("#059669"));
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#DC2626"));

    public TaskItemViewModel(DownloadTask task,
        Action<TaskItemViewModel> onStop,
        Action<TaskItemViewModel> onResume,
        Action<TaskItemViewModel, bool> onRemove,
        Action<TaskItemViewModel> onOpen,
        Action<TaskItemViewModel> onReveal)
    {
        _task = task;
        _onStop = onStop;
        _onResume = onResume;
        _onRemove = onRemove;
        _onOpen = onOpen;
        _onReveal = onReveal;
    }

    public string Id => _task.Id;
    public DownloadTask Task => _task;

    public string Title => string.IsNullOrWhiteSpace(_task.Title) ? _task.Input : _task.Title;

    public string Subtitle
    {
        get
        {
            var size = _task.Size > 0 ? DownloadEngine.FormatSize(_task.Size) : "大小未知";
            var done = _task.Size > 0 ? $"{_task.Progress * 100:0.0}%" : "";
            return $"{_task.Input}  ·  {size}  ·  {done}";
        }
    }

    /// <summary>文件实际所在的 DC（从消息里的 document/photo.dc_id 读出来，不是用户选的）。</summary>
    public string DcText => _task.DcId > 0 ? $"DC{_task.DcId}" : "";
    public bool HasDc => _task.DcId > 0;

    public string StatusText => _task.Status switch
    {
        DownloadStatus.Queued => "排队中",
        DownloadStatus.Resolving => "解析中",
        DownloadStatus.Downloading => "下载中",
        DownloadStatus.Paused => "已暂停",
        DownloadStatus.Done => "已完成",
        DownloadStatus.Failed => "失败",
        _ => "未知",
    };

    public IBrush StatusBrush => _task.Status switch
    {
        DownloadStatus.Queued => QueuedBrush,
        DownloadStatus.Resolving or DownloadStatus.Downloading => RunningBrush,
        DownloadStatus.Paused => PausedBrush,
        DownloadStatus.Done => DoneBrush,
        _ => FailedBrush,
    };

    public double ProgressValue => _task.Progress;

    public string SpeedText => _task.Status == DownloadStatus.Downloading && _speed > 0
        ? $"{_speed / 1048576.0:0.00} MiB/s"
        : "";

    /// <summary>「上个 0.5s 拿到的数据量」——按需求直接显示窗口字节数。</summary>
    public string WindowText => _task.Status == DownloadStatus.Downloading && _windowBytes > 0
        ? $"最近 0.5s +{DownloadEngine.FormatSize(_windowBytes)}"
        : "";

    /// <summary>每条连接的速度，拼成一行：`l xz 3.2 · hello 2.8 · withme 4.1 MiB/s`。</summary>
    public string ConnText => _connText;
    public bool HasConnText => _connText.Length > 0;

    /// <summary>累计下载用时（暂停/排队的时间不算）。</summary>
    public string ElapsedText
    {
        get
        {
            if (_task.ElapsedMs <= 0) return "";
            var t = TimeSpan.FromMilliseconds(_task.ElapsedMs);
            return t.TotalHours >= 1
                ? $"用时 {(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"用时 {t.Minutes:00}:{t.Seconds:00}";
        }
    }

    public string? ErrorText => string.IsNullOrEmpty(_task.Error) ? null : _task.Error;
    public bool HasError => !string.IsNullOrEmpty(_task.Error);

    public bool CanStop => _task.Status is DownloadStatus.Resolving or DownloadStatus.Downloading;
    public bool CanResume => _task.Status is DownloadStatus.Paused or DownloadStatus.Failed;
    public bool IsDone => _task.Status == DownloadStatus.Done;
    public bool HasFile => !string.IsNullOrEmpty(_task.LocalPath);

    public void Update(DownloadTask task, SpeedSample? sample)
    {
        _task = task;
        if (sample != null)
        {
            _speed = sample.AverageBytesPerSecond;   // 用 3 秒滑动平均，避免 0/20 来回跳
            _windowBytes = sample.WindowBytes;
            _connText = sample.PerConnection.Count == 0
                ? ""
                : string.Join(" · ", sample.PerConnection.Select(c =>
                    $"{(c.Label.Length > 8 ? c.Label.Substring(0, 8) : c.Label)} {c.Speed / 1048576.0:0.0}")) + " MiB/s";
        }
        else if (task.Status != DownloadStatus.Downloading)
        {
            _speed = 0;
            _windowBytes = 0;
            _connText = "";
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(DcText));
        OnPropertyChanged(nameof(HasDc));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(WindowText));
        OnPropertyChanged(nameof(ConnText));
        OnPropertyChanged(nameof(HasConnText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(HasFile));
    }

    [RelayCommand]
    private void Stop() => _onStop(this);

    [RelayCommand]
    private void Resume() => _onResume(this);

    [RelayCommand]
    private void DeleteRecord() => _onRemove(this, false);

    [RelayCommand]
    private void DeleteWithFile() => _onRemove(this, true);

    [RelayCommand]
    private void Open() => _onOpen(this);

    [RelayCommand]
    private void Reveal() => _onReveal(this);
}
