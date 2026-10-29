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
        Accounts = new ObservableCollection<AccountItemViewModel>();
        PickItems = new ObservableCollection<MediaPickItemViewModel>();
        Settings = new SettingsViewModel();

        MtpLog.Sink = msg => _ui.Post(() => AppendLog(msg));

        _engine.TasksChanged += () => _ui.Post(SyncTasks);
        _engine.SessionChanged += () => _ui.Post(RefreshSession);
        _engine.TaskProgress += (task, sample) => _ui.Post(() => OnTaskProgress(task, sample));
        _engine.Log += msg => _ui.Post(() => AppendLog(msg));

        Settings.LoadFrom(SettingsStore.Current);
        SyncTasks();
        RefreshAccounts();
        RefreshSummary();

        AppendLog($"tgdl 启动。数据目录：{AppPaths.DataDir}");
        if (AccountStore.Count == 0)
            AppendLog("还没有账号：到右侧「账号」页粘贴一条 auth key，选好它属于哪个 DC，点「导入并验证」。");
        else
            AppendLog($"已加载 {AccountStore.Count} 个账号。点顶部「启动连接」验证并建立连接。");

        RefreshSession();
        _engine.Start();
    }

    public ObservableCollection<TaskItemViewModel> Tasks { get; }
    public ObservableCollection<AccountItemViewModel> Accounts { get; }
    public ObservableCollection<MediaPickItemViewModel> PickItems { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] public partial string NewTarget { get; set; } = "";
    [ObservableProperty] public partial string LogText { get; set; } = "";
    [ObservableProperty] public partial string TotalSpeedText { get; set; } = "0 B/s";
    [ObservableProperty] public partial string QueueSummary { get; set; } = "";
    [ObservableProperty] public partial string AccountSummary { get; set; } = "";
    [ObservableProperty] public partial string StatusBar { get; set; } = "就绪";
    [ObservableProperty] public partial bool IsEmptyQueue { get; set; } = true;

    // 账号导入
    [ObservableProperty] public partial bool ShowImport { get; set; }
    [ObservableProperty] public partial string ImportHex { get; set; } = "";
    [ObservableProperty] public partial int ImportDcIndex { get; set; } = 4;
    [ObservableProperty] public partial string ImportStatus { get; set; } = "";
    [ObservableProperty] public partial string ClearAccountsLabel { get; set; } = "清空全部";
    [ObservableProperty] public partial bool Busy { get; set; }

    // 会话
    [ObservableProperty] public partial string SessionText { get; set; } = "未连接";
    [ObservableProperty] public partial string SessionButtonText { get; set; } = "启动连接";
    [ObservableProperty] public partial IBrush SessionBrush { get; set; } = new SolidColorBrush(Color.Parse("#9AA3AF"));
    [ObservableProperty] public partial int SelectedTab { get; set; }

    // 相册多选
    [ObservableProperty] public partial bool IsPicking { get; set; }
    [ObservableProperty] public partial string PickTitle { get; set; } = "";
    [ObservableProperty] public partial string PickSummary { get; set; } = "";

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
            if (_engine.Session != SessionState.Ready)
            {
                _engine.Enqueue(chatId, messageId, $"{chatId}:{messageId}");
                NewTarget = "";
                StatusBar = "连接未就绪，任务已排队，启动后自动开始";
                return;
            }
        }

        // 先解析，看这条消息里是不是有多个文件（相册）
        List<MtpApi.MediaEntry> media;
        try
        {
            media = await _engine.ResolveMediaListAsync(chatId, messageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppendLog($"解析失败（直接按单条入队）：{ex.Message}");
            _engine.Enqueue(chatId, messageId, $"{chatId}:{messageId}");
            NewTarget = "";
            return;
        }

        if (media.Count <= 1)
        {
            _engine.Enqueue(chatId, messageId, $"{chatId}:{messageId}");
            NewTarget = "";
            StatusBar = $"已加入队列 {chatId}:{messageId}";
            return;
        }

        PickItems.Clear();
        foreach (var m in media) PickItems.Add(new MediaPickItemViewModel(chatId, m));
        PickTitle = $"这条消息里有 {media.Count} 个文件，选择要下载的：";
        UpdatePickSummary();
        IsPicking = true;
        NewTarget = "";
        StatusBar = $"发现 {media.Count} 个文件，请选择";
    }

    private void UpdatePickSummary()
    {
        var sel = PickItems.Where(p => p.Selected).ToList();
        PickSummary = $"已选 {sel.Count}/{PickItems.Count} 个 · 共 {DownloadEngine.FormatSize(sel.Sum(p => p.Size))}";
    }

    partial void OnIsPickingChanged(bool value) => UpdatePickSummary();

    [RelayCommand]
    private void TogglePick(MediaPickItemViewModel item)
    {
        item.Selected = !item.Selected;
        UpdatePickSummary();
    }

    [RelayCommand]
    private void SelectAllPick()
    {
        foreach (var p in PickItems) p.Selected = true;
        UpdatePickSummary();
    }

    [RelayCommand]
    private void SelectNonePick()
    {
        foreach (var p in PickItems) p.Selected = false;
        UpdatePickSummary();
    }

    [RelayCommand]
    private void CancelPick()
    {
        IsPicking = false;
        PickItems.Clear();
        StatusBar = "已取消";
    }

    [RelayCommand]
    private void ConfirmPick()
    {
        var sel = PickItems.Where(p => p.Selected).ToList();
        if (sel.Count == 0)
        {
            StatusBar = "没有选中任何文件";
            return;
        }
        foreach (var item in sel)
            _engine.Enqueue(item.ChatId, item.MessageId, $"{item.ChatId}:{item.MessageId}");
        IsPicking = false;
        PickItems.Clear();
        StatusBar = $"已加入队列 {sel.Count} 个文件";
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

    private async Task StartSessionCoreAsync()
    {
        StatusBar = "正在验证账号并建立连接…";
        try
        {
            var progress = new Progress<string>(AppendLog);
            await _engine.StartSessionAsync(progress, CancellationToken.None);
            StatusBar = "连接就绪：" + _engine.SessionText;
        }
        catch (NeedAuthKeyException nex)
        {
            AppendLog("需要导入账号 —— " + nex.Message);
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
            RefreshAccounts();
        }
    }

    // ------------------------------------------------------------------ 账号

    private void RefreshAccounts()
    {
        var all = AccountStore.All();
        int use = Math.Min(SettingsStore.Current.AccountCount, all.Count);
        Accounts.Clear();
        for (int i = 0; i < all.Count; i++)
            Accounts.Add(new AccountItemViewModel(all[i], i < use, RemoveAccount, VerifyAccount));

        AccountSummary = all.Count == 0
            ? "还没有账号"
            : $"{all.Count} 个账号（本次使用 {use} 个）｜" +
              string.Join("，", all.Select(a => $"{a.Title}·DC{a.HomeDc}"));
    }

    [RelayCommand]
    private void ToggleImport() => ShowImport = !ShowImport;

    [RelayCommand]
    private async Task ImportAccountAsync()
    {
        if (Busy) return;
        Busy = true;
        ImportStatus = "正在连接验证…";
        StatusBar = "正在导入并验证账号…";
        try
        {
            var progress = new Progress<string>(AppendLog);
            var acc = await AuthKeyManager.ImportAndVerifyAsync(
                ImportHex, ImportDcIndex + 1, _engine.Pool.Options, _engine.Pool, progress, CancellationToken.None);

            ImportHex = "";
            ShowImport = false;
            ImportStatus = $"验证通过：{acc.Title}";
            AppendLog($"账号「{acc.Title}」导入成功（user_id {acc.UserId}，主 DC{acc.HomeDc}）");
            RefreshAccounts();
            StatusBar = $"账号「{acc.Title}」已导入并验证";
        }
        catch (Exception ex)
        {
            ImportStatus = ex.Message;
            AppendLog("导入失败：" + ex.Message);
            StatusBar = "导入失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private void RemoveAccount(AccountItemViewModel vm)
    {
        AccountStore.Remove(vm.Account.Id);
        _ = _engine.Pool.DropAccountAsync(vm.Account.Id);
        AppendLog($"已删除账号「{vm.Title}」");
        RefreshAccounts();
    }

    private async void VerifyAccount(AccountItemViewModel vm)
    {
        try
        {
            var progress = new Progress<string>(AppendLog);
            await AuthKeyManager.VerifyAsync(_engine.Pool, vm.Account, progress, CancellationToken.None);
            RefreshAccounts();
        }
        catch (Exception ex)
        {
            AppendLog($"账号「{vm.Title}」验证失败：{ex.Message}");
            StatusBar = $"验证失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearAccounts()
    {
        if (!_clearArmed || (DateTime.UtcNow - _clearArmedAt).TotalSeconds > 5)
        {
            _clearArmed = true;
            _clearArmedAt = DateTime.UtcNow;
            ClearAccountsLabel = "再点一次确认";
            StatusBar = "再点一次「清空全部」确认删除所有账号";
            _ = Task.Delay(5000).ContinueWith(_ => _ui.Post(() =>
            {
                _clearArmed = false;
                ClearAccountsLabel = "清空全部";
            }));
            return;
        }
        _clearArmed = false;
        ClearAccountsLabel = "清空全部";
        AccountStore.ClearAll();
        EntityStore.Reload();
        AppendLog("已清空所有账号");
        RefreshAccounts();
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
        AppendLog($"设置已保存：{settings.AccountCount} 个账号 × 每个 {settings.ConcurrencyPerAccount} 并发，" +
                  $"分片 {settings.ChunkSize / 1024} KiB，下载目录 {settings.EffectiveDownloadDir}");
        StatusBar = "设置已保存";
        Settings.LoadFrom(settings);
        RefreshAccounts();
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
