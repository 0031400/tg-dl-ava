using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using tgdl.Mtproto;
using tgdl.Storage;

namespace tgdl.Download;

/// <summary>0.5s 一次的速度采样。</summary>
public sealed record SpeedSample(long WindowBytes, double BytesPerSecond, double[] PerConnection)
{
    public double TotalMib => BytesPerSecond / 1048576.0;
}

/// <summary>连接池的生命周期状态（界面上的「启动 / 停止」）。</summary>
public enum SessionState
{
    Stopped,
    Starting,
    Ready,
    Failed,
}

internal sealed class LocationExpiredException : Exception
{
    public LocationExpiredException() : base("file_reference 已过期，需要重新取消息") { }
}

/// <summary>连接断了：不能在同一连接上重试，要重建连接后从断点继续。</summary>
internal sealed class ConnectionLostException : Exception
{
    public ConnectionLostException(string message, Exception? inner = null) : base(message, inner)
    {
        // 服务端用 4 字节错误码踢连接时，-429 表示限流，要等久一点再重连
        IsFlood = message.Contains("-429", StringComparison.Ordinal);
    }

    public bool IsFlood { get; }
}

/// <summary>服务端说这个文件不在当前 DC（FILE_MIGRATE_n），要换 DC 重来。</summary>
internal sealed class FileMigrateException : Exception
{
    public FileMigrateException(int dcId) : base($"FILE_MIGRATE_{dcId}") => DcId = dcId;

    public int DcId { get; }
}

/// <summary>
/// 下载引擎：一个 FIFO 队列，同一时刻只下一个文件，
/// 用 N 条 auth key 连接 × 每条 C 个在途请求把带宽吃满。
/// </summary>
public sealed class DownloadEngine : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly List<DownloadTask> _tasks = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConnectionPool _pool = new();
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private string? _currentId;
    private CancellationTokenSource? _currentCts;
    private bool _queuePaused;
    private volatile SessionState _session = SessionState.Stopped;
    private volatile string _sessionText = "未连接";
    private CancellationTokenSource? _sessionCts;

    public DownloadEngine()
    {
        TaskStore.Load();
        lock (_lock) _tasks.AddRange(TaskStore.Snapshot());
        _pool.ApplySettings(SettingsStore.Current);
    }

    public ConnectionPool Pool => _pool;

    /// <summary>任务列表结构变化（增删、状态切换）。</summary>
    public event Action? TasksChanged;

    /// <summary>某个任务的进度刷新（每 0.5s 一次）。</summary>
    public event Action<DownloadTask, SpeedSample?>? TaskProgress;

    public event Action<string>? Log;

    public string? CurrentTaskId { get { lock (_lock) return _currentId; } }
    public bool QueuePaused => _queuePaused;

    public SessionState Session => _session;
    public string SessionText => _sessionText;

    /// <summary>连接池状态变化（启动/停止/失败）。</summary>
    public event Action? SessionChanged;

    private void SetSession(SessionState state, string text)
    {
        _session = state;
        _sessionText = text;
        SessionChanged?.Invoke();
    }

    private void Emit(string message)
    {
        // 只走 Log 事件：MtpLog.Sink 也接到了 UI，两边都写会导致每行日志出现两次
        Log?.Invoke(message);
    }

    public List<DownloadTask> Snapshot()
    {
        lock (_lock) return _tasks.Select(t => t.Clone()).ToList();
    }

    public DownloadTask? Find(string id)
    {
        lock (_lock) return _tasks.FirstOrDefault(t => t.Id == id)?.Clone();
    }

    private void Persist()
    {
        List<DownloadTask> copy;
        lock (_lock) copy = _tasks.Select(t => t.Clone()).ToList();
        TaskStore.Replace(copy);
    }

    private void RaiseChanged()
    {
        Persist();
        TasksChanged?.Invoke();
    }

    // ------------------------------------------------------------------ 连接池启停

    /// <summary>
    /// 启动：立刻按设置的 auth key 数量把主 DC 的连接建好（不够就现场派生并写进 authkeys.json）。
    /// 建好后队列才开始取任务。
    /// </summary>
    public async Task StartSessionAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (_session is SessionState.Starting or SessionState.Ready) return;

        var settings = SettingsStore.Current;
        _pool.ApplySettings(settings);

        // 停止按钮要在"正在连接"期间也能生效，所以这里挂一个自己的取消源
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sessionCts = cts;
        var token = cts.Token;

        SetSession(SessionState.Starting, "正在连接…");
        Emit("正在建立连接…");

        try
        {
            var home = AuthKeyStore.HomeDc
                       ?? throw new NeedAuthKeyException(null, "还没有 auth key：请到「Auth Key」页导入一条（或把 data/authkeys.json 拷进来）");
            if (AuthKeyStore.PrimaryKeyHex == null)
                throw new NeedAuthKeyException(home, $"主 DC{home} 上没有可用的 auth key，请导入一条");

            int want = Math.Clamp(settings.AuthKeyCount, 1, 12);

            // 先用主连接确认 key 真的能用（不是过期/被重置的）
            var control = await _pool.GetControlAsync(token).ConfigureAwait(false);
            await MtpApi.GetConfigAsync(control, token).ConfigureAwait(false);
            Emit($"主连接就绪：DC{home}（auth_key {control.Key.ShortHex}…）");

            // 主 DC 不能派生新 key（见 AuthKeyManager 的说明），所以只把已有的 key 连上；
            // 「设置的数量」对主 DC 来说最多只能到"你实际拥有的 key 数"。
            var homeKeys = AuthKeyStore.KeysFor(home);
            int made = 1;
            foreach (var k in homeKeys.Skip(1).Take(Math.Max(0, want - 1)))
            {
                await _pool.GetAsync(home, k, token).ConfigureAwait(false);
                made++;
            }
            if (made < want)
            {
                Emit($"主 DC{home} 只有 {made} 条可用 key（设置要求 {want} 条）。" +
                     $"主 DC 无法派生更多 —— 只有登录时产生的那些 key 能用；" +
                     $"下载其他 DC 的文件时会自动派生到 {want} 条。");
            }
            Emit($"DC{home} 连接就绪：{made} 条");

            SetSession(SessionState.Ready, $"DC{home} · {made} 条连接");
            _signal.Release();
        }
        catch (OperationCanceledException)
        {
            Emit("启动已取消");
            SetSession(SessionState.Stopped, "未连接");
        }
        catch (RpcException rex) when (rex.Code == 401)
        {
            SetSession(SessionState.Failed, "auth key 无效");
            throw new NeedAuthKeyException(AuthKeyStore.HomeDc,
                $"auth key 无效或已被服务端重置（{rex.Text}），请重新导入一条 DC{AuthKeyStore.HomeDc} 的 key");
        }
        catch (Exception ex)
        {
            SetSession(SessionState.Failed, "连接失败");
            Emit("启动失败：" + ex.Message);
            throw;
        }
        finally
        {
            if (ReferenceEquals(_sessionCts, cts)) _sessionCts = null;
        }
    }

    /// <summary>停止：中断当前任务并关掉所有 TCP 连接。</summary>
    public async Task StopSessionAsync()
    {
        if (_session == SessionState.Stopped) return;

        // 正在连接中也能停：取消启动流程
        try { _sessionCts?.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
        lock (_lock) _currentCts?.Cancel();
        SetSession(SessionState.Stopped, "未连接");
        Emit("正在断开所有连接…");

        // 给正在跑的 worker 一点时间退出来，避免文件句柄被占用
        await Task.Delay(200).ConfigureAwait(false);
        await _pool.CloseAllAsync().ConfigureAwait(false);
        Emit("连接已全部关闭");
    }

    // ------------------------------------------------------------------ 队列操作

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    public void PauseQueue()
    {
        _queuePaused = true;
        Emit("队列已暂停（当前任务继续跑完）");
        TasksChanged?.Invoke();
    }

    public void ResumeQueue()
    {
        _queuePaused = false;
        _signal.Release();
        Emit("队列已恢复");
        TasksChanged?.Invoke();
    }

    /// <summary>加入一个 chat_id:message_id 到队列。</summary>
    public DownloadTask Enqueue(long chatId, int messageId, string input)
    {
        var task = new DownloadTask
        {
            ChatId = chatId,
            MessageId = messageId,
            Input = input,
            Status = DownloadStatus.Queued,
            Title = $"{input}",
        };
        lock (_lock) _tasks.Add(task);
        Emit($"已加入队列：{input}");
        RaiseChanged();
        _signal.Release();
        return task;
    }

    /// <summary>停止当前任务（保留已下分片，状态变 Paused）。</summary>
    public void StopTask(string id)
    {
        lock (_lock)
        {
            if (_currentId == id)
            {
                _currentCts?.Cancel();
                return;
            }
            var t = _tasks.FirstOrDefault(x => x.Id == id);
            if (t != null && t.Status is DownloadStatus.Queued)
            {
                t.Status = DownloadStatus.Paused;
            }
        }
        RaiseChanged();
    }

    /// <summary>继续（重新排队）。</summary>
    public void ResumeTask(string id)
    {
        lock (_lock)
        {
            var t = _tasks.FirstOrDefault(x => x.Id == id);
            if (t == null || t.Status is DownloadStatus.Downloading or DownloadStatus.Resolving or DownloadStatus.Done) return;
            t.Status = DownloadStatus.Queued;
            t.Error = null;
        }
        Emit($"已重新排队：{id}");
        RaiseChanged();
        _signal.Release();
    }

    /// <summary>删除任务记录，可选删除已下载的文件。</summary>
    public void RemoveTask(string id, bool deleteFile)
    {
        DownloadTask? task;
        lock (_lock)
        {
            task = _tasks.FirstOrDefault(x => x.Id == id);
            if (task == null) return;
            if (_currentId == id) _currentCts?.Cancel();
            _tasks.Remove(task);
        }
        if (deleteFile && !string.IsNullOrEmpty(task.LocalPath))
        {
            // 正在下载的任务文件被占用，取消后要等句柄释放，所以放到后台重试几次
            var path = task.LocalPath;
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        if (!File.Exists(path)) return;
                        File.Delete(path);
                        Emit($"已删除文件：{path}");
                        return;
                    }
                    catch (IOException) { await Task.Delay(150).ConfigureAwait(false); }
                    catch (UnauthorizedAccessException) { await Task.Delay(150).ConfigureAwait(false); }
                    catch (Exception ex) { Emit($"删除文件失败：{ex.Message}"); return; }
                }
                Emit($"删除文件失败：{path} 仍被占用");
            });
        }
        RaiseChanged();
    }

    public void MoveUp(string id)
    {
        lock (_lock)
        {
            int i = _tasks.FindIndex(t => t.Id == id);
            if (i <= 0) return;
            var t = _tasks[i];
            if (t.IsActive) return;
            _tasks.RemoveAt(i);
            _tasks.Insert(i - 1, t);
        }
        RaiseChanged();
    }

    // ------------------------------------------------------------------ 主循环

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_session != SessionState.Ready)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }
            if (_queuePaused)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            DownloadTask? next;
            lock (_lock) next = _tasks.FirstOrDefault(t => t.Status == DownloadStatus.Queued);

            if (next == null)
            {
                try { await _signal.WaitAsync(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            await RunTaskAsync(next, ct).ConfigureAwait(false);
        }
    }

    private void SetStatus(string id, DownloadStatus status, string? error = null)
    {
        lock (_lock)
        {
            var t = _tasks.FirstOrDefault(x => x.Id == id);
            if (t == null) return;
            t.Status = status;
            if (error != null) t.Error = error;
            if (status == DownloadStatus.Done) t.Error = null;
        }
        RaiseChanged();
    }

    private async Task RunTaskAsync(DownloadTask task, CancellationToken queueCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(queueCt);
        lock (_lock)
        {
            _currentId = task.Id;
            _currentCts = cts;
        }

        var ct = cts.Token;
        int attempts = 0;
        try
        {
            while (true)
            {
                attempts++;
                try
                {
                    await ExecuteTaskAsync(task, ct).ConfigureAwait(false);
                    break;
                }
                catch (LocationExpiredException) when (attempts <= 2)
                {
                    Emit("file_reference 过期，重新取一次消息…");
                    lock (_lock)
                    {
                        var t = _tasks.FirstOrDefault(x => x.Id == task.Id);
                        if (t != null) { t.Location = null; }
                        task.Location = null;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(task.Id, DownloadStatus.Paused);
            Emit($"已停止：{task.Title}");
        }
        catch (Exception ex)
        {
            SetStatus(task.Id, DownloadStatus.Failed, ex.Message);
            Emit($"任务失败：{task.Title} — {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _currentId = null;
                _currentCts = null;
            }
            TaskProgress?.Invoke(task, null);
            Persist();
        }
    }

    private async Task ExecuteTaskAsync(DownloadTask task, CancellationToken ct)
    {
        var settings = SettingsStore.Current;
        _pool.ApplySettings(settings);

        if (string.IsNullOrEmpty(task.Location) || task.Size <= 0)
        {
            SetStatus(task.Id, DownloadStatus.Resolving);
            await ResolveAsync(task, ct).ConfigureAwait(false);
            lock (_lock)
            {
                var t = _tasks.FirstOrDefault(x => x.Id == task.Id);
                if (t != null) { t.Title = task.Title; t.Size = task.Size; t.DcId = task.DcId; t.Location = task.Location; }
            }
            RaiseChanged();
        }

        SetStatus(task.Id, DownloadStatus.Downloading);
        var progress = new Progress<string>(s => Emit(s));

        // 正常情况下 dc_id 是消息里自带的，一次就对；
        // 万一服务端回 FILE_MIGRATE_n（文件被挪到别的 DC），跟着它换 DC 继续。
        for (int attempt = 0; ; attempt++)
        {
            // 死掉的连接会在 GetAsync 里被自动替换成新的
            var conns = await _pool.GetDownloadConnectionsAsync(task.DcId, settings.AuthKeyCount, progress, ct)
                .ConfigureAwait(false);
            Emit(attempt == 0
                ? $"开始下载 {task.Title}（{FormatSize(task.Size)}，文件在 DC{task.DcId}，" +
                  $"{conns.Count} 条连接 × {settings.ConcurrencyPerKey} 并发）"
                : $"继续下载 {task.Title}（已下 {FormatSize(task.Done)} / {FormatSize(task.Size)}，" +
                  $"{conns.Count} 条连接）");

            try
            {
                await RunSessionAsync(task, conns, ct).ConfigureAwait(false);
                break;
            }
            catch (ConnectionLostException lost) when (attempt < 5)
            {
                int wait = (lost.IsFlood ? 4000 : 800) * (attempt + 1);
                Emit($"连接断了（{lost.Message}），{wait / 1000.0:0.#} 秒后重连并从断点继续…");
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (FileMigrateException mig) when (attempt < 2)
            {
                Emit($"服务端说这个文件其实在 DC{mig.DcId}（FILE_MIGRATE_{mig.DcId}），切过去继续");
                task.DcId = mig.DcId;
                lock (_lock)
                {
                    var t = _tasks.FirstOrDefault(x => x.Id == task.Id);
                    if (t != null) t.DcId = mig.DcId;
                }
                RaiseChanged();
            }
        }

        lock (_lock)
        {
            var t = _tasks.FirstOrDefault(x => x.Id == task.Id);
            if (t != null)
            {
                t.Done = t.Size;
                t.Status = DownloadStatus.Done;
                t.ChunkMap = null;
                t.Error = null;
            }
        }
        Emit($"下载完成：{task.Title} → {task.LocalPath}");
        RaiseChanged();

        if (settings.OpenFolderWhenDone)
        {
            try { Interop.ShellHelper.RevealInExplorer(task.LocalPath!); } catch { /* ignore */ }
        }
    }

    /// <summary>取消息 → 拿到文件名/大小/DC/位置。</summary>
    private async Task ResolveAsync(DownloadTask task, CancellationToken ct)
    {
        var peer = EntityStore.Resolve(task.ChatId);
        if (peer == null)
        {
            Emit($"本地没有 chat_id={task.ChatId} 的 access_hash，先同步会话列表…");
            await SyncDialogsAsync(3, ct).ConfigureAwait(false);
            peer = EntityStore.Resolve(task.ChatId);
        }
        if (peer == null)
            throw new MtpException($"找不到 chat_id={task.ChatId}。请先点「同步会话列表」，并确认这个账号在该会话里");

        var conn = await _pool.GetControlAsync(ct).ConfigureAwait(false);
        var message = await MtpApi.GetMessageAsync(conn, peer.Value, task.MessageId, ct).ConfigureAwait(false);
        var media = MtpApi.ExtractMedia(message);

        task.Title = SanitizeFileName(media.FileName);
        task.Size = media.Size;
        task.DcId = media.DcId > 0 ? media.DcId : AuthKeyStore.HomeDc ?? 2;
        task.Location = media.LocationBase64;
        task.LocalPath = PickFreePath(SettingsStore.Current.EffectiveDownloadDir, task.Title);
        Emit($"解析成功：{task.Title}（{FormatSize(task.Size)}，DC{task.DcId}）");
    }

    /// <summary>同步会话列表，缓存 access_hash。</summary>
    public async Task<int> SyncDialogsAsync(int maxPages, CancellationToken ct)
    {
        var conn = await _pool.GetControlAsync(ct).ConfigureAwait(false);
        int total = 0;
        PeerRef? offsetPeer = null;
        int offsetDate = 0, offsetId = 0;
        for (int page = 0; page < Math.Max(1, maxPages); page++)
        {
            var res = await MtpApi.GetDialogsAsync(conn, 100, offsetPeer, offsetDate, offsetId, ct).ConfigureAwait(false);
            if (res.Entities.Count == 0) break;
            EntityStore.PutRange(res.Entities);
            total += res.Entities.Count;
            if (res.LastPeer == null) break;
            offsetPeer = res.LastPeer;
            offsetDate = res.LastDate;
            offsetId = res.LastMsgId;
            if (res.Entities.Count < 100) break;
        }
        Emit($"会话列表同步完成，缓存 {total} 个会话（本地共 {EntityStore.Count} 个）");
        return total;
    }

    // ------------------------------------------------------------------ 并行下载

    private sealed class ChunkState
    {
        public long Done;
        public int Cursor = -1;
        public Exception? Failure;
    }

    private async Task RunSessionAsync(DownloadTask task, List<MtpConnection> conns, CancellationToken ct)
    {
        var settings = SettingsStore.Current;
        int chunk = NormalizeChunk(settings.ChunkSize);
        long size = task.Size;
        if (size <= 0) throw new MtpException("文件大小为 0");
        if (string.IsNullOrEmpty(task.Location)) throw new MtpException("缺少文件定位信息");

        var location = Convert.FromBase64String(task.Location);
        var path = task.LocalPath ??= PickFreePath(settings.EffectiveDownloadDir, task.Title);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        int chunkCount = (int)((size + chunk - 1) / chunk);
        var completed = DecodeChunkMap(task.ChunkMap, chunkCount);

        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        SafeFileHandle handle = fs.SafeFileHandle;
        ChunkState? state = null;
        try
        {
            if (fs.Length != size) fs.SetLength(size);

            long already = 0;
            var pending = new List<long>(chunkCount);
            for (int i = 0; i < chunkCount; i++)
            {
                long off = (long)i * chunk;
                long len = Math.Min(chunk, size - off);
                if (completed[i]) already += len;
                else pending.Add(off);
            }
            if (pending.Count == 0)
            {
                task.Done = size;
                return;
            }
            if (already > 0) Emit($"续传：已有 {FormatSize(already)}，剩 {FormatSize(size - already)}");

            var st = state = new ChunkState { Done = already };
            var limiters = conns.Select(_ => new AdaptiveLimiter(settings.ConcurrencyPerKey, settings.AdaptiveFlood)).ToList();
            var connBytes = new long[conns.Count];

            void Report(long done, SpeedSample? sample)
            {
                lock (_lock)
                {
                    var t = _tasks.FirstOrDefault(x => x.Id == task.Id);
                    if (t != null) t.Done = done;
                }
                task.Done = done;
                TaskProgress?.Invoke(task, sample);
            }

            // 任一 worker 失败时用它把其它 worker 一起停掉
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // 严格 0.5s 采样：窗口内写入的字节数 / 实际间隔，然后清零
            using var sampleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sampler = Task.Run(async () =>
            {
                long lastTick = Stopwatch.GetTimestamp();
                try
                {
                    while (!sampleCts.IsCancellationRequested)
                    {
                        await Task.Delay(500, sampleCts.Token).ConfigureAwait(false);
                        long now = Stopwatch.GetTimestamp();
                        double dt = (now - lastTick) / (double)Stopwatch.Frequency;
                        lastTick = now;
                        if (dt <= 0) continue;
                        var per = new double[conns.Count];
                        long window = 0;
                        for (int i = 0; i < conns.Count; i++)
                        {
                            long b = Interlocked.Exchange(ref connBytes[i], 0);
                            per[i] = b / dt;
                            window += b;
                        }
                        Report(Interlocked.Read(ref st.Done), new SpeedSample(window, window / dt, per));
                    }
                }
                catch (OperationCanceledException) { /* 正常结束 */ }
            }, CancellationToken.None);

            var workers = new List<Task>();
            for (int ci = 0; ci < conns.Count; ci++)
            {
                int connIndex = ci;
                for (int slot = 0; slot < settings.ConcurrencyPerKey; slot++)
                {
                    workers.Add(Task.Run(() => WorkerAsync(connIndex, conns[connIndex], limiters[connIndex],
                        connBytes, location, chunk, size, pending, completed, st, handle, sessionCts, ct), ct));
                }
            }

            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            finally
            {
                sampleCts.Cancel();
                try { await sampler.ConfigureAwait(false); } catch { /* ignore */ }
            }

            if (st.Failure != null)
            {
                SaveChunkMap(task, completed, chunkCount, st.Done);
                throw st.Failure!;
            }

            ct.ThrowIfCancellationRequested();
            fs.Flush(true);
            SaveChunkMap(task, completed, chunkCount, st.Done);
            if (st.Done < size)
                throw new MtpException($"下载不完整：{FormatSize(st.Done)} / {FormatSize(size)}");
        }
        catch (OperationCanceledException)
        {
            SaveChunkMap(task, completed, chunkCount, state?.Done ?? 0);
            throw;
        }
        finally
        {
            try { fs.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>每个 worker 的循环：取一个偏移 → 限流 → 拉取 → 按偏移写盘。</summary>
    private async Task WorkerAsync(int connIndex, MtpConnection conn, AdaptiveLimiter limiter, long[] connBytes,
        byte[] location, int chunk, long size, List<long> pending, bool[] completed,
        ChunkState state, SafeFileHandle handle, CancellationTokenSource sessionCts, CancellationToken ct)
    {
        while (true)
        {
            if (ct.IsCancellationRequested) return;
            int i = Interlocked.Increment(ref state.Cursor);
            if (i >= pending.Count) return;
            long offset = pending[i];
            long remaining = size - offset;
            int expected = (int)Math.Min(chunk, remaining);
            // limit 必须永远传"整个分片"：Telegram 要求 limit 是 4096 的倍数，
            // 传"剩余字节数"（文件末尾那一片必然不是 4096 的倍数）会得到 400 LIMIT_INVALID。
            // 末尾服务端只返回剩下的字节，所以按 expected 校验即可。
            int limit = chunk;

            try
            {
                await limiter.WaitCooldownAsync(ct).ConfigureAwait(false);
                await limiter.AcquireAsync(ct).ConfigureAwait(false);
                byte[] data;
                try
                {
                    data = await FetchAsync(conn, connIndex, location, offset, limit, limiter, ct).ConfigureAwait(false);
                }
                finally
                {
                    limiter.Release();
                }

                if (data.Length == 0)
                    throw new MtpException($"offset {offset} 返回了 0 字节");

                int n = Math.Min(data.Length, expected);
                RandomAccess.Write(handle, data.AsSpan(0, n), offset);
                Interlocked.Add(ref state.Done, n);
                Interlocked.Add(ref connBytes[connIndex], n);

                if (n < expected)
                    throw new MtpException($"分片不完整 offset={offset} 期望 {expected} 实际 {n}");
                completed[i] = true;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref state.Failure, ex, null);
                sessionCts.Cancel();   // 其它 worker 立刻停手
                return;
            }
        }
    }

    private async Task<byte[]> FetchAsync(MtpConnection conn, int connIndex, byte[] location, long offset, int limit,
        AdaptiveLimiter limiter, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await limiter.WaitCooldownAsync(ct).ConfigureAwait(false);
            try
            {
                var data = await MtpApi.GetFileAsync(conn, location, offset, limit, ct).ConfigureAwait(false);
                limiter.NoteSuccess();
                return data;
            }
            catch (RpcException ex) when (ex.IsFloodWait)
            {
                int seconds = ex.FloodSeconds;
                int before = limiter.Limit;
                int now = limiter.NoteFlood(seconds);
                string tail = limiter.Adaptive
                    ? (now < before ? $"并发降到 {now}/{limiter.MaxLimit}" : $"并发 {now}/{limiter.MaxLimit}")
                    : $"并发保持 {now}/{limiter.MaxLimit}（自适应已关闭）";
                Emit($"连接 #{connIndex + 1} FLOOD_WAIT {seconds}s：这条连接暂停 {seconds}s，其余继续" +
                     $"（{tail}，累计 {limiter.FloodCount} 次）");
                if (limiter.FloodCount == 10)
                {
                    Emit("提示：这条连接已经限流 10 次。把「每条 key 的并发」降到 2~3，" +
                         "或打开「自适应限流」，通常比硬扛更快。");
                }
                last = ex;
            }
            catch (RpcException ex) when (ex.Text.Contains("FILE_REFERENCE", StringComparison.Ordinal))
            {
                throw new LocationExpiredException();
            }
            catch (RpcException ex) when (ex.MigrateDc is int migrateDc && migrateDc > 0)
            {
                throw new FileMigrateException(migrateDc);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsConnectionLost(conn, ex))
            {
                // 连接已经死了，在它上面重试没有意义：交给上层重连
                throw new ConnectionLostException(conn.FatalError?.Message ?? ex.Message, ex);
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(Math.Min(500 * (attempt + 1), 4000), ct).ConfigureAwait(false);
            }
        }
        throw new MtpException($"offset {offset} 重试 6 次仍失败：{last?.Message}", last ?? new Exception());
    }

    private static bool IsConnectionLost(MtpConnection conn, Exception ex)
    {
        if (conn.IsDead) return true;
        if (ex is IOException or ObjectDisposedException) return true;
        if (ex is MtpException m)
            return m.Message.Contains("连接已断开", StringComparison.Ordinal)
                   || m.Message.Contains("连接中断", StringComparison.Ordinal);
        return false;
    }

    // ------------------------------------------------------------------ 辅助

    private static int NormalizeChunk(int chunk)
    {
        chunk = Math.Clamp(chunk, 4096, 1024 * 1024);
        return chunk / 4096 * 4096;
    }

    private static bool[] DecodeChunkMap(string? map, int chunkCount)
    {
        var result = new bool[chunkCount];
        if (string.IsNullOrWhiteSpace(map)) return result;
        foreach (var part in map.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-');
            if (!int.TryParse(range[0], out int from)) continue;
            int to = from;
            if (range.Length > 1) int.TryParse(range[1], out to);
            for (int i = Math.Max(0, from); i <= Math.Min(chunkCount - 1, to); i++) result[i] = true;
        }
        return result;
    }

    private static string EncodeChunkMap(bool[] completed)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < completed.Length)
        {
            if (!completed[i]) { i++; continue; }
            int start = i;
            while (i + 1 < completed.Length && completed[i + 1]) i++;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(start);
            if (i > start) sb.Append('-').Append(i);
            i++;
        }
        return sb.ToString();
    }

    private void SaveChunkMap(DownloadTask task, bool[] completed, int chunkCount, long done)
    {
        var map = EncodeChunkMap(completed);
        // 本地这份也要更新：重连/换 DC 时会拿它当续传依据
        task.ChunkMap = map;
        task.Done = done;
        lock (_lock)
        {
            var t = _tasks.FirstOrDefault(x => x.Id == task.Id);
            if (t == null) return;
            t.ChunkMap = map;
            t.Done = done;
        }
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var clean = sb.ToString().Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "download";
        if (clean.Length > 180) clean = clean.Substring(0, 180);
        return clean;
    }

    private static string PickFreePath(string dir, string fileName)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.00} {units[u]}";
    }

    public async ValueTask DisposeAsync()
    {
        _session = SessionState.Stopped;
        try { _sessionCts?.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
        _cts.Cancel();
        lock (_lock) _currentCts?.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        Persist();
        await _pool.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _signal.Dispose();
    }
}
