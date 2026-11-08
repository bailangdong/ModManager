using System.Diagnostics;

namespace ModManager.Core.Services;

/// <summary>
/// A named-mutex lease whose acquire and release both happen on one dedicated thread.
/// </summary>
/// <remarks>
/// A Windows mutex has thread affinity: the thread that calls <see cref="Mutex.WaitOne()"/> must also
/// call <see cref="Mutex.ReleaseMutex()"/>. An async method can resume on a different pool thread, so
/// acquiring directly inside <c>AcquireAsync</c> would make release throw. This class parks a
/// background thread on the mutex for the lifetime of the lease; disposing the lease signals that
/// thread to release and exit. The OS releases the mutex automatically if the process dies, so a
/// crash cannot leave a permanent lock. Cancellation and timeout are polled every 50 ms.
/// </remarks>
internal sealed class CrossProcessMutexLease : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(5);

    private readonly string _name;
    private readonly TimeSpan _timeout;
    private readonly CancellationToken _cancellationToken;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new(false);
    private Exception? _failure;
    private bool _ownsMutex;
    private int _disposed;

    private CrossProcessMutexLease(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        _name = name;
        _timeout = timeout;
        _cancellationToken = cancellationToken;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ModManager cross-process deployment lock",
        };
    }

    public static async Task<CrossProcessMutexLease> AcquireAsync(
        string name,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var lease = new CrossProcessMutexLease(name, timeout, cancellationToken);
        lease._thread.Start();

        try
        {
            await lease._ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        if (lease._failure is not null)
        {
            var failure = lease._failure;
            lease.Dispose();
            throw failure;
        }

        return lease;
    }

    private void Run()
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, _name);
            var started = Stopwatch.StartNew();

            while (true)
            {
                if (_cancellationToken.IsCancellationRequested)
                {
                    _failure = new OperationCanceledException(_cancellationToken);
                    return;
                }

                var acquired = false;
                try
                {
                    acquired = mutex.WaitOne(PollInterval);
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner died without releasing. Ownership is transferred to us and
                    // the mutex is safe to use; that is exactly the recovery path we want.
                    acquired = true;
                }

                if (acquired)
                {
                    _ownsMutex = true;
                    break;
                }

                if (started.Elapsed >= _timeout)
                {
                    _failure = new TimeoutException(
                        $"Timed out after {_timeout.TotalSeconds:0.#}s waiting for the cross-process deployment lock '{_name}'.");
                    return;
                }
            }

            _ready.TrySetResult(true);
            _release.Wait();
        }
        catch (Exception ex)
        {
            _failure = ex;
        }
        finally
        {
            try
            {
                if (_ownsMutex)
                {
                    mutex?.ReleaseMutex();
                }
            }
            catch
            {
                // If release fails the OS releases the mutex when this process exits; never mask the
                // original failure or crash the deployment thread.
            }
            finally
            {
                mutex?.Dispose();
                _ready.TrySetResult(false);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ready.TrySetResult(false);
        _release.Set();
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(JoinTimeout);
        }

        // Do not dispose the event while the owner thread could still be parked on it. Leaking one
        // ManualResetEventSlim is safer than an ObjectDisposedException on a deployment thread.
        if (!_thread.IsAlive)
        {
            _release.Dispose();
        }
    }
}
