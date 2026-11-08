using System.Collections.Concurrent;

namespace ModManager.Core.Services;

/// <summary>
/// In-process, per-game deployment lock.
/// </summary>
/// <remarks>
/// Scope is deliberately one game id. Two games have disjoint target trees, so serializing them
/// globally would only make the user wait; two operations on the same game share journals, backups
/// and manifests and must not interleave. The semaphore is never disposed or removed: a waiter may
/// still hold a reference, and the set of game ids is tiny.
/// </remarks>
public sealed class InstallationGate : IInstallationGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<IDisposable> AcquireAsync(
        string gameId,
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var gate = _gates.GetOrAdd(gameId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Lease(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
