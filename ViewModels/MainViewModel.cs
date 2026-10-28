using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using tgdl.Download;
using tgdl.Interop;
using tgdl.Models;
using tgdl.Mtproto;
using tgdl.Storage;

namespace tgdl.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DownloadEngine _engine = new();
    private readonly Dictionary<string, TaskItemViewModel> _map = new();
    private readonly Dictionary<string, double> _speeds = new();
    private readonly StringBuilder _logBuffer = new();
    private readonly Dispatcher _ui = Dispatcher.UIThread;
    private int _logLines;
    private bool _clearArmed;
    private DateTime _clearArmedAt;

    public MainViewModel()
    {
        Tasks = new ObservableCollection<TaskItemViewModel>();
        AuthKeyGroups = new ObservableCollection<AuthKeyGroupViewModel>();
        DcList = new List<int> { 1, 2, 3, 4, 5 };
        Settings = new SettingsViewModel();

        MtpLog.Sink = msg => _ui.Post(() => AppendLog(msg));

        _engine.TasksChanged += () => _ui.Post(SyncTasks);
        _engine.SessionChanged += () => _ui.Post(RefreshSession);
        _engine.TaskProgress += (task, sample) => _ui.Post(() => OnTaskProgress(task, sample));
        _engine.Log += msg => _ui.Post(() => AppendLog(msg));

        Settings.LoadFrom(SettingsStore.Current);
        SyncTasks();
        RefreshAuthKeys();
        RefreshSummary();

        AppendLog($"tgdl 启动。数据目录：{AppPaths.DataDir}");
        if (AuthKeyStore.TotalCount == 0)
            AppendLog("还没有 auth key：到右侧「Auth Key」页导入一条，或者把另一台机器的 data/authkeys.json 拷过来。");
        AppendLog("点顶部「启动连接」建立连接；直接加入任务也会自动启动。");

        RefreshSession();
        _engine.Start();
    }

    public ObservableCollection<TaskItemViewModel> Tasks { get; }
    public ObservableCollection<AuthKeyGroupViewModel> AuthKeyGroups { get; }
    public SettingsViewModel Settings { get; }
    public List<int> DcList { get; }

    [ObservableProperty] public partial string NewTarget { get; set; } = "";
    [ObservableProperty] public partial string LogText { get; set; } = "";
    [ObservableProperty] public partial string TotalSpeedText { get; set; } = "0 B/s";
    [ObservableProperty] public partial string QueueSummary { get; set; } = "";
    [ObservableProperty] public partial string AuthKeySummary { get; set; } = "";
    [ObservableProperty] public partial string StatusBar { get; set; } = "就绪";
    [ObservableProperty] public partial bool ShowImport { get; set; }
    [ObservableProperty] public partial bool ShowDerive { get; set; }
    [ObservableProperty] public partial string ImportHex { get; set; } = "";
    [ObservableProperty] public partial int ImportDcIndex { get; set; } = 4;
    [ObservableProperty] public partial bool ImportAsHome { get; set; } = true;
    [ObservableProperty] public partial int DeriveDcIndex { get; set; } = 3;
    [ObservableProperty] public partial decimal? DeriveCount { get; set; } = 2;
    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial string ClearKeysLabel { get; set; } = "清空全部";
    [ObservableProperty] public partial string SessionText { get; set; } = "未连接";
    [ObservableProperty] public partial string SessionButtonText { get; set; } = "启动连接";
    [ObservableProperty] public partial IBrush SessionBrush { get; set; } = new SolidColorBrush(Color.Parse("#9AA3AF"));
    [ObservableProperty] public partial int SelectedTab { get; set; }
    [ObservableProperty] public partial bool CanDerive { get; set; } = true;
    [ObservableProperty] public partial string DeriveHint { get; set; } = "";
    [ObservableProperty] public partial bool IsEmptyQueue { get; set; } = true;

    public event Action? LogAppended;

    // ------------------------------------------------------------------ 日志

    public void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _logBuffer.AppendLine(line);
        _logLines++;
        if (_logLines > 3000)
        {
            var text = _logBuffer.ToString();
            int cut = text.IndexOf('\n', text.Length / 3);
            if (cut > 0)
            {
                _logBuffer.Clear();
                _logBuffer.Append(text.Substring(cut + 1));
                _logLines = _logBuffer.ToString().Count(c => c == '\n');
            }
        }
        LogText = _logBuffer.ToString();
        LogAppended?.Invoke();
    }

    [RelayCommand]
    private void ClearLog()
    {
        _logBuffer.Clear();
        _logLines = 0;
        LogText = "";
    }

    // ------------------------------------------------------------------ 连接池启停

    private static readonly IBrush DotStopped = new SolidColorBrush(Color.Parse("#9AA3AF"));
    private static readonly IBrush DotBusy = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush DotReady = new SolidColorBrush(Color.Parse("#059669"));
    private static readonly IBrush DotFailed = new SolidColorBrush(Color.Parse("#DC2626"));

    private void RefreshSession()
    {
        var state = _engine.Session;
        SessionText = _engine.SessionText;
        SessionButtonText = state switch
        {
            SessionState.Ready => "停止连接",
            SessionState.Starting => "连接中…",
            _ => "启动连接",
        };
        SessionBrush = state switch
        {
            SessionState.Ready => DotReady,
            SessionState.Starting => DotBusy,
            SessionState.Failed => DotFailed,
            _ => DotStopped,
        };
    }

    [RelayCommand]
    private async Task ToggleSessionAsync()
    {
        // 只有「已就绪」时这个按钮才是停止；连接中再点等于忽略
        if (_engine.Session == SessionState.Ready)
        {
            await _engine.StopSessionAsync();
            StatusBar = "连接已关闭（队列保留，重新启动后继续）";
            RefreshSession();
            return;
        }
        if (_engine.Session == SessionState.Starting) return;
        await StartSessionCoreAsync();
    }

    /// <summary>启动连接；缺 key 时提示导入并把界面切到 Auth Key 页。</summary>
    private async Task StartSessionCoreAsync()
    {
        StatusBar = "正在建立连接…";
        try
        {
            var progress = new Progress<string>(AppendLog);
            await _engine.StartSessionAsync(progress, CancellationToken.None);
            StatusBar = "连接就绪：" + _engine.SessionText;
        }
        catch (NeedAuthKeyException nex)
        {
            AppendLog("需要导入 auth key —— " + nex.Message);
            StatusBar = nex.Message;
            SelectedTab = 0;
            ShowImport = true;
        }
        catch (Exception ex)
        {
            AppendLog("启动失败：" + ex.Message);
            StatusBar = "启动失败：" + ex.Message;
        }
        finally
        {
            RefreshSession();
        }
    }

    // ------------------------------------------------------------------ 任务

    private void SyncTasks()
    {
        var snapshot = _engine.Snapshot();
        var ids = snapshot.Select(t => t.Id).ToList();
        bool sameOrder = ids.Count == Tasks.Count && ids.Zip(Tasks, (a, b) => a == b.Id).All(x => x);

        if (sameOrder)
        {
            for (int i = 0; i < snapshot.Count; i++)
                _map[snapshot[i].Id].Update(snapshot[i], null);
            RefreshSummary();
            return;
        }

        Tasks.Clear();
        _map.Clear();
        foreach (var task in snapshot)
        {
            var vm = new TaskItemViewModel(task, StopTask, ResumeTask, RemoveTask, OpenFile, RevealFile);
            _map[task.Id] = vm;
            Tasks.Add(vm);
        }
        RefreshSummary();
    }

    private void OnTaskProgress(DownloadTask task, SpeedSample? sample)
    {
        if (_map.TryGetValue(task.Id, out var vm))
            vm.Update(task, sample);

        if (sample != null) _speeds[task.Id] = sample.BytesPerSecond;
        else if (task.Status != DownloadStatus.Downloading) _speeds.Remove(task.Id);

        double total = _speeds.Values.Sum();
        TotalSpeedText = total > 0 ? $"{total / 1048576.0:0.00} MiB/s" : "0 B/s";
    }

    private void RefreshSummary()
    {
        var snapshot = _engine.Snapshot();
        int active = snapshot.Count(t => t.IsActive);
        int queued = snapshot.Count(t => t.Status == DownloadStatus.Queued);
        int done = snapshot.Count(t => t.Status == DownloadStatus.Done);
        QueueSummary = $"{snapshot.Count} 个任务 · 进行中 {active} · 排队 {queued} · 完成 {done}";
        IsEmptyQueue = snapshot.Count == 0;
    }

    private void StopTask(TaskItemViewModel vm)
    {
        _engine.StopTask(vm.Id);
        AppendLog($"停止：{vm.Title}");
    }

    private void ResumeTask(TaskItemViewModel vm)
    {
        _engine.ResumeTask(vm.Id);
        AppendLog($"继续：{vm.Title}");
    }

    private void RemoveTask(TaskItemViewModel vm, bool deleteFile)
    {
        _engine.RemoveTask(vm.Id, deleteFile);
        _map.Remove(vm.Id);
        _speeds.Remove(vm.Id);
        AppendLog(deleteFile ? $"删除任务和文件：{vm.Title}" : $"删除任务记录：{vm.Title}");
    }

    private void OpenFile(TaskItemViewModel vm)
    {
        try
        {
            var path = vm.Task.LocalPath;
            if (string.IsNullOrEmpty(path)) return;
            ShellHelper.OpenFile(path);
        }
        catch (Exception ex)
        {
            AppendLog("打开文件失败：" + ex.Message);
        }
    }

    private void RevealFile(TaskItemViewModel vm)
    {
        try
        {
            var path = vm.Task.LocalPath;
            if (string.IsNullOrEmpty(path)) return;
            ShellHelper.RevealInExplorer(path);
        }
        catch (Exception ex)
        {
            AppendLog("定位文件失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (!TargetParser.TryParse(NewTarget, out var chatId, out var messageId, out var error))
        {
            StatusBar = error;
            AppendLog("输入有误：" + error);
            return;
        }

        if (_engine.Session != SessionState.Ready)
        {
            AppendLog("连接未启动，先自动启动连接池…");
            await StartSessionCoreAsync();
        }

        _engine.Enqueue(chatId, messageId, $"{chatId}:{messageId}");
        NewTarget = "";
        StatusBar = _engine.Session == SessionState.Ready
            ? $"已加入队列 {chatId}:{messageId}"
            : $"已加入队列 {chatId}:{messageId}（连接未就绪，启动后自动开始）";
    }

    [RelayCommand]
    private void ResumeQueue()
    {
        _engine.ResumeQueue();
        StatusBar = "队列已恢复";
    }

    [RelayCommand]
    private void PauseQueue()
    {
        _engine.PauseQueue();
        StatusBar = "队列已暂停";
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        try { ShellHelper.OpenFolder(SettingsStore.Current.EffectiveDownloadDir); }
        catch (Exception ex) { AppendLog("打开目录失败：" + ex.Message); }
    }

    // ------------------------------------------------------------------ auth key

    partial void OnDeriveDcIndexChanged(int value) => UpdateDeriveState();

    /// <summary>目标 DC 等于主 DC 时直接禁用派生按钮，别让用户点了才发现失败。</summary>
    private void UpdateDeriveState()
    {
        int target = DeriveDcIndex + 1;
        var home = AuthKeyStore.HomeDc;
        if (home == null)
        {
            CanDerive = false;
            DeriveHint = "还没有主 auth key，先导入一条。";
            return;
        }
        if (home == target)
        {
            CanDerive = false;
            DeriveHint = $"DC{target} 就是主 DC —— Telegram 不允许导出本 DC 的授权，派生不了。换个目标 DC。";
            return;
        }
        CanDerive = true;
        DeriveHint = $"从 DC{home} 导出授权、导进 DC{target} 的新连接，不需要重新登录。";
    }

    private void RefreshAuthKeys()
    {
        AuthKeyGroups.Clear();
        var home = AuthKeyStore.HomeDc;
        foreach (var dc in AuthKeyStore.DcIds)
        {
            var group = new AuthKeyGroupViewModel(dc, home == dc);
            var keys = AuthKeyStore.KeysFor(dc);
            for (int i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                group.Keys.Add(new AuthKeyItemViewModel(dc, key, i == 0 && home == dc, RemoveKey));
            }
            AuthKeyGroups.Add(group);
        }

        UpdateDeriveState();
        var parts = AuthKeyStore.DcIds.Select(dc => $"DC{dc} {AuthKeyStore.CountFor(dc)} 条");
        AuthKeySummary = AuthKeyStore.TotalCount == 0
            ? "还没有 auth key"
            : $"共 {AuthKeyStore.TotalCount} 条（{string.Join("，", parts)}）｜主 DC{home?.ToString() ?? "未设置"}";
    }

    private void RemoveKey(AuthKeyItemViewModel item)
    {
        AuthKeyStore.Remove(item.DcId, item.Hex);
        _ = _engine.Pool.DropAsync(item.DcId, item.Hex);
        AppendLog($"已删除 DC{item.DcId} 的 key {item.ShortHex}…");
        RefreshAuthKeys();
    }

    [RelayCommand]
    private void ToggleImport() => ShowImport = !ShowImport;

    [RelayCommand]
    private void ToggleDerive() => ShowDerive = !ShowDerive;

    [RelayCommand]
    private void ImportKey()
    {
        try
        {
            int dc = ImportDcIndex + 1;
            AuthKeyManager.Import(ImportHex, dc, ImportAsHome);
            var head = ImportHex.Trim();
            AppendLog($"已导入 auth key 到 DC{dc}（{(head.Length >= 8 ? head.Substring(0, 8) : head)}…）");
            ImportHex = "";
            RefreshAuthKeys();
            StatusBar = "导入成功";
        }
        catch (Exception ex)
        {
            AppendLog("导入失败：" + ex.Message);
            StatusBar = "导入失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeriveKeysAsync()
    {
        if (Busy) return;
        Busy = true;
        StatusBar = "正在派生 auth key…";
        try
        {
            var progress = new Progress<string>(AppendLog);
            int dc = DeriveDcIndex + 1;
            int count = (int)decimal.Clamp(DeriveCount ?? 1, 1, 12);
            var created = await AuthKeyManager.DeriveAsync(_engine.Pool, dc, count, progress, CancellationToken.None);
            AppendLog($"派生完成：DC{dc} 新增 {created.Count} 条");
            RefreshAuthKeys();
            StatusBar = $"派生完成，新增 {created.Count} 条";
        }
        catch (Exception ex)
        {
            AppendLog("派生失败：" + ex.Message);
            StatusBar = "派生失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void ClearKeys()
    {
        if (!_clearArmed || (DateTime.UtcNow - _clearArmedAt).TotalSeconds > 5)
        {
            _clearArmed = true;
            _clearArmedAt = DateTime.UtcNow;
            ClearKeysLabel = "再点一次确认";
            StatusBar = "再点一次「清空全部」确认清空所有 auth key";
            _ = Task.Delay(5000).ContinueWith(_ => _ui.Post(() =>
            {
                _clearArmed = false;
                ClearKeysLabel = "清空全部";
            }));
            return;
        }
        _clearArmed = false;
        ClearKeysLabel = "清空全部";
        AuthKeyStore.ClearAll();
        EntityStore.Reload();
        AppendLog("已清空所有 auth key");
        RefreshAuthKeys();
        StatusBar = "已清空";
    }

    [RelayCommand]
    private async Task SyncDialogsAsync()
    {
        if (Busy) return;
        Busy = true;
        StatusBar = "正在同步会话列表…";
        try
        {
            var n = await _engine.SyncDialogsAsync(10, CancellationToken.None);
            StatusBar = $"同步完成，缓存 {n} 个会话";
            RefreshAuthKeys();
        }
        catch (Exception ex)
        {
            AppendLog("同步失败：" + ex.Message);
            StatusBar = "同步失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    // ------------------------------------------------------------------ 设置

    [RelayCommand]
    private void SaveSettings()
    {
        var settings = Settings.ToSettings();
        SettingsStore.Save(settings);
        _engine.Pool.ApplySettings(settings);
        AppendLog($"设置已保存：{settings.AuthKeyCount} 条 key × 每条 {settings.ConcurrencyPerKey} 并发，" +
                  $"分片 {settings.ChunkSize / 1024} KiB，下载目录 {settings.EffectiveDownloadDir}");
        StatusBar = "设置已保存";
        Settings.LoadFrom(settings);
    }

    [RelayCommand]
    private void BrowseDownloadDir()
    {
        try
        {
            var dir = SettingsStore.Current.EffectiveDownloadDir;
            ShellHelper.OpenFolder(dir);
            Settings.DownloadDir = dir;
        }
        catch (Exception ex)
        {
            AppendLog("打开目录失败：" + ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _engine.DisposeAsync();
    }
}
