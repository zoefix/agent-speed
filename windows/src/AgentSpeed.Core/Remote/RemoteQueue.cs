namespace AgentSpeed.Core.Remote;

/// <summary>
/// A serial executor on the thread pool: work items run one at a time, in the order posted. A connection's state is only
/// touched on its queue. <see cref="Sync{T}"/> from inside the queue runs inline instead of waiting on itself.
/// </summary>
internal sealed class RemoteQueue
{
    [ThreadStatic]
    private static RemoteQueue? _current;

    private readonly Lock _lock = new();
    private readonly Queue<Action> _work = new();
    private bool _running;

    public RemoteQueue(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public bool IsCurrent => _current == this;

    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_lock)
        {
            _work.Enqueue(work);
            if (_running)
            {
                return;
            }

            _running = true;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static queue => queue.Drain(), this, preferLocal: false);
    }

    public void Sync(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Sync<bool>(() =>
        {
            work();
            return true;
        });
    }

    public T Sync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (IsCurrent)
        {
            return work();
        }

        using var done = new ManualResetEventSlim(false);
        T result = default!;
        Exception? failure = null;
        Post(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (failure is not null)
        {
            throw new InvalidOperationException("Remote queue work failed.", failure);
        }

        return result;
    }

    private void Drain()
    {
        var previous = _current;
        _current = this;
        try
        {
            while (true)
            {
                Action work;
                lock (_lock)
                {
                    if (!_work.TryDequeue(out work!))
                    {
                        _running = false;
                        return;
                    }
                }

                try
                {
                    work();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    RemoteLog.Write($"{Name}: work item failed ({error.GetType().Name})");
                }
            }
        }
        finally
        {
            _current = previous;
        }
    }
}

/// <summary>A cancellable one-shot timer.</summary>
internal interface IRemoteScheduledWork
{
    void Cancel();
}

/// <summary>The wall clock and timers of the connections; tests replace it with a clock they advance by hand.</summary>
internal interface IRemoteScheduling
{
    DateTimeOffset Now { get; }

    /// <summary>Runs <paramref name="work"/> once on <paramref name="queue"/> at <paramref name="deadline"/> (wall clock: a deadline that passed during sleep fires on wake).</summary>
    IRemoteScheduledWork Schedule(DateTimeOffset deadline, RemoteQueue queue, Action work);
}

internal sealed class SystemRemoteScheduling : IRemoteScheduling
{
    /// <summary>Timers check the wall clock at least this often, so sleep never delays a deadline by more.</summary>
    private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(5);

    public static SystemRemoteScheduling Instance { get; } = new();

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public IRemoteScheduledWork Schedule(DateTimeOffset deadline, RemoteQueue queue, Action work) => new WallTimer(deadline, queue, work);

    private sealed class WallTimer : IRemoteScheduledWork
    {
        private readonly DateTimeOffset _deadline;
        private readonly RemoteQueue _queue;
        private readonly Action _work;
        private readonly Timer _timer;
        private int _state;

        public WallTimer(DateTimeOffset deadline, RemoteQueue queue, Action work)
        {
            _deadline = deadline;
            _queue = queue;
            _work = work;
            _timer = new Timer(static self => ((WallTimer)self!).Check(), this, Timeout.Infinite, Timeout.Infinite);
            Arm();
        }

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _state, 2) != 2)
            {
                _timer.Dispose();
            }
        }

        private void Arm()
        {
            var remaining = _deadline - DateTimeOffset.UtcNow;
            var step = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining > MaxStep ? MaxStep : remaining;
            try
            {
                _timer.Change(step, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Check()
        {
            if (Volatile.Read(ref _state) != 0)
            {
                return;
            }

            if (DateTimeOffset.UtcNow < _deadline)
            {
                Arm();
                return;
            }

            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                return;
            }

            _timer.Dispose();
            _queue.Post(_work);
        }
    }
}
