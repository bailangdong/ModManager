namespace ModManager.Core.Services;

/// <summary>
/// A point in the deployment transaction that a test host can observe. Production callers pass
/// <see langword="null"/>; the child-process crash tests use it to pause at a known instruction and
/// let the parent terminate the process for real.
/// </summary>
public enum DeploymentCheckpoint
{
    /// <summary>The transaction journal has been opened and marked applying, before any mutation.</summary>
    JournalOpened,

    /// <summary>The first mod file has been placed on disk.</summary>
    FirstFilePlaced,

    /// <summary>Every planned file has been placed, before verification and commit.</summary>
    AllFilesPlaced,

    /// <summary>Verification passed, immediately before the commit marker is written.</summary>
    BeforeCommit,
}

/// <summary>Receives deployment checkpoints. Test-only; never wired by the WPF host.</summary>
public interface IDeploymentCheckpointSink
{
    /// <summary>Called synchronously on the deployment thread.</summary>
    /// <param name="checkpoint">Checkpoint reached.</param>
    /// <param name="path">Absolute path involved, when one exists.</param>
    void Reached(DeploymentCheckpoint checkpoint, string? path);
}
