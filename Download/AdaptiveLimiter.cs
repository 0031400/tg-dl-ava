using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace tgdl.Download;

/// <summary>
/// 每条连接自己的在途额度闸门（照搬 Python 版的自适应限流）：
/// 撞到 FLOOD_WAIT 就把额度砍半并设一个全局冷却点，连续成功后再慢慢涨回去。
/// </summary>
public sealed class AdaptiveLimiter
{
    private readonly object _gate = new();
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();
    private readonly int _max;
    private readonly int _min;
    private readonly bool _adaptive;
    private int _limit;
    private int _active;
    private int _okStreak;
    private int _floods;
    private long _floodUntilTicks;

    public AdaptiveLimiter(int limit, bool adaptive)
    {
        _max = Math.Max(1, limit);
        _min = Math.Max(1, _max / 4);
        _adaptive = adaptive;
        _limit = _max;
    }

    public int Limit { get { lock (_gate) return _limit; } }

    public bool Adaptive => _adaptive;
    public int MaxLimit => _max;
    public int FloodCount { get { lock (_gate) return _floods; } }

    public async Task AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            TaskCompletionSource<bool>? waiter = null;
            lock (_gate)
            {
                if (_active < _limit)
                {
                    _active++;
                    return;
                }
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(waiter);
            }
            using var reg = ct.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(), waiter);
            try
            {
                // Release 会把名额直接转交给被唤醒的 waiter，所以这里成功即代表拿到名额
                await waiter.Task.ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                if (waiter.Task.IsCompletedSuccessfully) Release();  // 名额已经转过来，交回去
                throw;
            }
        }
    }

    public void Release()
    {
        TaskCompletionSource<bool>? toWake = null;
        lock (_gate)
        {
            if (_active > 0) _active--;
            while (_waiters.Count > 0)
            {
                var w = _waiters.Dequeue();
                if (w.TrySetResult(true)) { toWake = w; break; }
            }
        }
    }

    public void NoteSuccess()
    {
        if (!_adaptive) return;
        lock (_gate)
        {
            _okStreak++;
            if (_okStreak >= 16 && _limit < _max)
            {
                _okStreak = 0;
                _limit++;
            }
        }
    }

    /// <summary>记录一次限流，返回当前额度。</summary>
    public int NoteFlood(int seconds)
    {
        lock (_gate)
        {
            _okStreak = 0;
            _floods++;
            long until = Environment.TickCount64 + (long)(seconds + 0.5) * 1000;
            if (until > _floodUntilTicks) _floodUntilTicks = until;
            if (_adaptive) _limit = Math.Max(_min, _limit / 2);
            return _limit;
        }
    }

    public async Task WaitCooldownAsync(CancellationToken ct)
    {
        long until;
        lock (_gate) until = _floodUntilTicks;
        long delay = until - Environment.TickCount64;
        if (delay > 0) await Task.Delay((int)delay, ct).ConfigureAwait(false);
    }
}
