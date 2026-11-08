namespace ModManager.Core.Services;

/// <summary>
/// Serializes deployments that touch the same deployment root, while allowing different roots and
/// different games to proceed independently. The gate also takes a cross-process named mutex.
/// </summary>
/// <remarks>
/// The lock is deliberately keyed by the normalized deployment root, not global: installing Skyrim
/// must not block an SDV install. A lease is released by <see cref="IDisposable.Dispose"/>, so it is
/// also released on exceptions and cancellation. Process exit releases the named mutex
/// automatically; an abandoned mutex is taken over by the next process.
/// </remarks>
public interface IInstallationGate
{
    /// <summary>
    /// Waits for exclusive access to a deployment root.
    /// </summary>
    /// <param name="resourceKey">Deployment root or, for a loader install, the game install path.</param>
    /// <param name="operation">Human-readable operation, used for diagnostics.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A lease that releases the deployment root when disposed.</returns>
    Task<IDisposable> AcquireAsync(string resourceKey, string operation, CancellationToken cancellationToken = default);
}
