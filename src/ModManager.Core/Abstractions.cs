namespace ModManager.Core;

/// <summary>Structured logging surface. Implementations write to file and to the in-app Logs page.</summary>
public interface IAppLogger
{
    /// <summary>Writes one entry.</summary>
    void Log(LogEntry entry);

    /// <summary>Returns the most recent entries, newest last.</summary>
    IReadOnlyList<LogEntry> Recent(int count = 500);

    /// <summary>Raised for every entry so the Logs page can stream.</summary>
    event EventHandler<LogEntry>? EntryWritten;
}

/// <summary>Convenience extensions so call sites stay short.</summary>
public static class AppLoggerExtensions
{
    /// <summary>Logs at Trace.</summary>
    public static void Trace(this IAppLogger logger, string category, string message, string? detail = null)
        => logger.Log(new LogEntry(DateTimeOffset.UtcNow, LogLevel.Trace, category, message, detail));

    /// <summary>Logs at Debug.</summary>
    public static void Debug(this IAppLogger logger, string category, string message, string? detail = null)
        => logger.Log(new LogEntry(DateTimeOffset.UtcNow, LogLevel.Debug, category, message, detail));

    /// <summary>Logs at Information.</summary>
    public static void Info(this IAppLogger logger, string category, string message, string? detail = null)
        => logger.Log(new LogEntry(DateTimeOffset.UtcNow, LogLevel.Information, category, message, detail));

    /// <summary>Logs at Warning.</summary>
    public static void Warn(this IAppLogger logger, string category, string message, string? detail = null)
        => logger.Log(new LogEntry(DateTimeOffset.UtcNow, LogLevel.Warning, category, message, detail));

    /// <summary>Logs at Error.</summary>
    public static void Error(this IAppLogger logger, string category, string message, string? detail = null)
        => logger.Log(new LogEntry(DateTimeOffset.UtcNow, LogLevel.Error, category, message, detail));
}

/// <summary>
/// Storage for secrets, kept separate from <see cref="AppSettings"/> on purpose.
/// </summary>
/// <remarks>
/// SECURITY CONTRACT — the user's spec §7 forbids hardcoded tokens and requires credentials to
/// live in Settings. Splitting the secret store out means:
/// <list type="bullet">
/// <item>the settings file (which users share when reporting bugs) can never contain the key;</item>
/// <item>log redaction has a single choke point;</item>
/// <item>an OS-backed implementation (DPAPI/Credential Manager) can be swapped in without
/// touching any caller.</item>
/// </list>
/// Implementations must never write the secret into a log, an exception message, or a
/// diagnostics dump.
/// </remarks>
public interface ISecretStore
{
    /// <summary>Reads a secret, or null when unset.</summary>
    string? Get(string name);

    /// <summary>Stores a secret.</summary>
    void Set(string name, string value);

    /// <summary>Removes a secret.</summary>
    void Remove(string name);

    /// <summary>True when a secret exists.</summary>
    bool Contains(string name);

    /// <summary>Well-known secret name for the Nexus API key.</summary>
    const string NexusApiKey = "nexus.apiKey";

    /// <summary>
    /// Key for the translation service, kept in the same encrypted store as the Nexus credential.
    /// </summary>
    const string TranslationApiKey = "translation.apiKey";

    /// <summary>Well-known secret name for the Nexus OAuth refresh token.</summary>
    const string NexusRefreshToken = "nexus.refreshToken";

    /// <summary>Well-known secret name for the Nexus OAuth access token.</summary>
    const string NexusAccessToken = "nexus.accessToken";
}

/// <summary>
/// The single gate every filesystem mutation must pass through.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the user's spec §17 is non-negotiable: no recursive operation on a drive
/// root, no <c>Directory.Delete(gameDirectory)</c>, no clearing a whole Mods folder, no path
/// traversal, no symlink escape, no operation outside the approved game root. Those are all
/// symptoms of the same missing thing — a component that knows what "inside" means and can prove
/// it. Centralising that is the only way to make the guarantee auditable rather than aspirational.
/// </para>
/// <para>
/// <b>Contract:</b> every mutating filesystem call in this codebase takes its path from
/// <see cref="ValidateWrite"/> / <see cref="ValidateDelete"/> first. A caller that skips the gate
/// is a bug, and the review checklist says so.
/// </para>
/// </remarks>
public interface IPathSafetyService
{
    /// <summary>
    /// Registers a game root as approved for writes. Called when a game is added and validated, so
    /// that later operations have a root to test against.
    /// </summary>
    void ApproveGameRoot(string gameId, string installPath, string modRootPath);

    /// <summary>Removes a game root, e.g. when the game is forgotten.</summary>
    void RevokeGameRoot(string gameId);

    /// <summary>Returns the approved roots, for the Settings page to display.</summary>
    IReadOnlyList<ApprovedRoot> ApprovedRoots { get; }

    /// <summary>
    /// Validates a prospective write. Refuses anything outside an approved root, anything under a
    /// protected system/drive-root path, anything escaping through <c>..</c>, and anything whose
    /// canonical form resolves outside the root it claims to be in.
    /// </summary>
    /// <param name="gameId">Game whose root must contain the path.</param>
    /// <param name="prospectivePath">Path the caller intends to write.</param>
    PathSafetyResult ValidateWrite(string gameId, string prospectivePath);

    /// <summary>
    /// Validates a deletion. Stricter than <see cref="ValidateWrite"/>: refuses reparse points by
    /// default, refuses directory deletes unless the directory is one we created, and refuses
    /// drive roots and protected directories outright.
    /// </summary>
    /// <param name="gameId">Game whose root must contain the path.</param>
    /// <param name="prospectivePath">Path the caller intends to delete.</param>
    /// <param name="isDirectory">True when deleting a directory tree.</param>
    PathSafetyResult ValidateDelete(string gameId, string prospectivePath, bool isDirectory);

    /// <summary>
    /// Validates a path inside the application's own data tree (staging, downloads, backups,
    /// journals). Same traversal rules, different root set.
    /// </summary>
    /// <param name="appOwnedPath">Path inside the app data tree.</param>
    PathSafetyResult ValidateAppPath(string appOwnedPath);

    /// <summary>
    /// Creates a directory only after validating it. Returns false with a reason instead of throwing,
    /// so callers can aggregate refusals into the installation preview.
    /// </summary>
    /// <param name="gameId">Game context.</param>
    /// <param name="directoryPath">Directory to create.</param>
    /// <param name="reason">Why it was refused.</param>
    bool TryCreateDirectory(string gameId, string directoryPath, out string? reason);

    /// <summary>
    /// Deletes a file only after validating it. Refuses rather than throwing.
    /// </summary>
    /// <param name="gameId">Game context.</param>
    /// <param name="filePath">File to delete.</param>
    /// <param name="reason">Why it was refused.</param>
    bool TryDeleteFile(string gameId, string filePath, out string? reason);

    /// <summary>
    /// Removes a directory only when it is empty and validated, or when it was recorded as created
    /// by us. Never recursive. This is what makes "don't delete the user's Mods folder" structural
    /// rather than a matter of remembering.
    /// </summary>
    /// <param name="gameId">Game context.</param>
    /// <param name="directoryPath">Directory to remove.</param>
    /// <param name="reason">Why it was refused.</param>
    bool TryRemoveEmptyDirectory(string gameId, string directoryPath, out string? reason);

    /// <summary>
    /// Canonicalises a path: expands short names, resolves existing reparse points, and normalises
    /// separators and casing. Returns null when the path cannot be resolved safely.
    /// </summary>
    /// <param name="path">Path to normalise.</param>
    string? Canonicalize(string path);

    /// <summary>True when <paramref name="candidate"/> is inside <paramref name="root"/> after canonicalisation.</summary>
    /// <param name="candidate">Path to test.</param>
    /// <param name="root">Containing root.</param>
    bool IsInside(string candidate, string root);
}

/// <summary>An approved write root, shown in Settings so the guarantee is inspectable.</summary>
/// <param name="GameId">Game it belongs to.</param>
/// <param name="InstallPath">Canonical install directory.</param>
/// <param name="ModRootPath">Canonical mod root.</param>
/// <param name="ApprovedAt">When it was approved.</param>
public sealed record ApprovedRoot(string GameId, string InstallPath, string ModRootPath, DateTimeOffset ApprovedAt);

/// <summary>Persistence for the game library.</summary>
public interface IGameRepository
{
    /// <summary>All registered instances.</summary>
    IReadOnlyList<GameInstance> All();

    /// <summary>One instance, or null.</summary>
    GameInstance? Get(string gameId);

    /// <summary>Inserts or replaces.</summary>
    void Upsert(GameInstance instance);

    /// <summary>Removes a registration. Deletes no files.</summary>
    bool Remove(string gameId);
}

/// <summary>Persistence for mod library records, manifests and profiles.</summary>
public interface IModRepository
{
    /// <summary>Mods staged for a game.</summary>
    IReadOnlyList<ModMetadata> ForGame(string gameId);

    /// <summary>All mods across games.</summary>
    IReadOnlyList<ModMetadata> All();

    /// <summary>One mod.</summary>
    ModMetadata? Get(string modId);

    /// <summary>Inserts or replaces a mod record.</summary>
    void Upsert(ModMetadata mod);

    /// <summary>Removes a mod record and its manifest.</summary>
    bool Remove(string modId);

    /// <summary>Manifests for a game.</summary>
    IReadOnlyList<ModManifest> ManifestsForGame(string gameId);

    /// <summary>One mod's manifest.</summary>
    ModManifest? GetManifest(string gameId, string modId);

    /// <summary>Commits a manifest.</summary>
    void SaveManifest(ModManifest manifest);

    /// <summary>Removes a manifest.</summary>
    bool RemoveManifest(string gameId, string modId);

    /// <summary>Profiles for a game.</summary>
    IReadOnlyList<Profile> ProfilesForGame(string gameId);

    /// <summary>One profile.</summary>
    Profile? GetProfile(string profileId);

    /// <summary>Inserts or replaces a profile.</summary>
    void SaveProfile(Profile profile);

    /// <summary>Removes a profile.</summary>
    bool RemoveProfile(string profileId);
}

/// <summary>Persistence for downloads, backups and known conflict resolutions.</summary>
public interface IDownloadRepository
{
    /// <summary>All download jobs, newest first.</summary>
    IReadOnlyList<DownloadRecord> All();

    /// <summary>One job.</summary>
    DownloadRecord? Get(string downloadId);

    /// <summary>Inserts or replaces a job.</summary>
    void Upsert(DownloadRecord record);

    /// <summary>Removes a job.</summary>
    bool Remove(string downloadId);
}

/// <summary>Persistence for backups of overwritten game files.</summary>
public interface IBackupRepository
{
    /// <summary>Backups for a game.</summary>
    IReadOnlyList<BackupEntry> ForGame(string gameId);

    /// <summary>Backups taken for one mod.</summary>
    IReadOnlyList<BackupEntry> ForMod(string gameId, string modId);

    /// <summary>Records a backup.</summary>
    void Add(BackupEntry entry);

    /// <summary>Marks a backup as consumed.</summary>
    void MarkRestored(string backupPath);

    /// <summary>Removes a record.</summary>
    bool Remove(string backupPath);
}

/// <summary>Persistence for per-path conflict decisions.</summary>
public interface IConflictRepository
{
    /// <summary>Rules recorded for a game.</summary>
    IReadOnlyList<ConflictResolution> ForGame(string gameId);

    /// <summary>Inserts or replaces a rule, keyed by path.</summary>
    void Upsert(string gameId, ConflictResolution resolution);

    /// <summary>Removes the rules for one path.</summary>
    bool Remove(string gameId, string relativeTarget);
}

/// <summary>Persistence for application settings.</summary>
public interface ISettingsStore
{
    /// <summary>Loads settings, creating defaults when nothing is stored.</summary>
    AppSettings Load();

    /// <summary>Saves settings.</summary>
    void Save(AppSettings settings);
}
