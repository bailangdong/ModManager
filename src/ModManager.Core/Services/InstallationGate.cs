using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ModManager.Core.Services;

/// <summary>
/// Two-layer deployment lock: an in-process gate keyed by the normalized game directory, plus a
/// Windows named mutex so two ModManager processes cannot write the same directory at once.
/// </summary>
/// <remarks>
/// Scope is one game directory, not the whole application. Different games have disjoint target
/// trees, so serializing them globally would only make the user wait. The in-process semaphore also
/// makes the gate safe when two callers use the same instance on different threads; the named mutex
/// covers separate processes. Process exit releases the mutex automatically, so a crash cannot leave
/// a permanent lock. Named-mutex acquisition/release is delegated to a dedicated thread because a
/// mutex is owned by the thread that acquired it.
/// </remarks>
public sealed class InstallationGate : IInstallationGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _crossProcess;
    private readonly TimeSpan _crossProcessTimeout;

    /// <summary>Creates a gate.</summary>
    /// <param name="crossProcess">When true, also take a named mutex for cross-process exclusion.</param>
    /// <param name="crossProcessTimeout">Maximum wait for the named mutex; defaults to two minutes.</param>
    public InstallationGate(bool crossProcess = true, TimeSpan? crossProcessTimeout = null)
    {
        _crossProcess = crossProcess;
        _crossProcessTimeout = crossProcessTimeout ?? TimeSpan.FromMinutes(2);
    }

    /// <inheritdoc />
    public async Task<IDisposable> AcquireAsync(
        string gameId,
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var resource = NormalizeResourceKey(gameId);
        var gate = _gates.GetOrAdd(resource, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        CrossProcessMutexLease? crossProcess = null;
        try
        {
            if (_crossProcess)
            {
                crossProcess = await CrossProcessMutexLease
                    .AcquireAsync(BuildMutexName(resource), _crossProcessTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new Lease(gate, crossProcess);
        }
        catch
        {
            crossProcess?.Dispose();
            gate.Release();
            throw;
        }
    }

    /// <summary>
    /// Normalizes a game id or directory path into the key used by both lock layers. Paths are made
    /// absolute and case-insensitive; a bare id is only trimmed and upper-cased so two processes
    /// agree even when they were started from different working directories.
    /// </summary>
    internal static string NormalizeResourceKey(string value)
    {
        var trimmed = value.Trim();
        var looksLikePath = Path.IsPathRooted(trimmed)
            || trimmed.Contains(Path.DirectorySeparatorChar)
            || trimmed.Contains(Path.AltDirectorySeparatorChar);

        if (!looksLikePath)
        {
            return trimmed.ToUpperInvariant();
        }

        if (!PhysicalPathResolver.TryResolve(trimmed, out var resolved, out var error))
        {
            // Do not fall back to the lexical path: two aliases would then get different lock
            // identities and could deploy into the same game directory concurrently.
            throw new IOException(error ?? "The deployment root could not be resolved to a physical path.");
        }

        return resolved;
    }

    private static string BuildMutexName(string normalizedResource)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedResource)))[..32];

        // Local\ is per Windows session. Global\ would need a privilege that a normal desktop user
        // may not have, and the manager only coordinates instances in the same session.
        var prefix = OperatingSystem.IsWindows() ? @"Local\" : "ModManager.";
        return prefix + "ModManager.Deploy." + hash;
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;
        private CrossProcessMutexLease? _crossProcess;

        public Lease(SemaphoreSlim gate, CrossProcessMutexLease? crossProcess)
        {
            _gate = gate;
            _crossProcess = crossProcess;
        }

        public void Dispose()
        {
            var crossProcess = Interlocked.Exchange(ref _crossProcess, null);
            crossProcess?.Dispose();
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
