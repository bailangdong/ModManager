namespace ModManager.Core.Services;

/// <summary>
/// Serializes deployments for one game while allowing different games to proceed independently.
/// </summary>
/// <remarks>
/// The lock is deliberately per game, not global: installing Skyrim must not block an SDV install.
/// A lease is released by <see cref="IDisposable.Dispose"/>, so it is also released on exceptions and
/// cancellation. Process exit releases it automatically because it is in-memory only.
/// </remarks>
public interface IInstallationGate
{
    /// <summary>
    /// Waits for exclusive access to a game's deployment state.
    /// </summary>
    /// <param name="gameId">Game whose files are about to change.</param>
    /// <param name="operation">Human-readable operation, used for diagnostics.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A lease that releases the game when disposed.</returns>
    Task<IDisposable> AcquireAsync(string gameId, string operation, CancellationToken cancellationToken = default);
}
