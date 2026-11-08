using ModManager.Core.Services;

namespace ModManager.Core;

/// <summary>Application settings, persisted as JSON.</summary>
/// <remarks>
/// Secrets policy: this record is written to disk, so the Nexus <b>API key</b> is deliberately
/// <i>not</i> a property here. It is stored separately by
/// <c>ISecretStore</c>, which is the only component allowed to read it back. That keeps an
/// accidental settings export or a log dump from leaking a credential — a requirement of the
/// user's spec §7.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Schema version, so an older file can be migrated rather than misread.</summary>
    public int Version { get; init; } = 1;

    /// <summary>UI theme id.</summary>
    public string Theme { get; init; } = "Light";

    /// <summary>True to use the light Fluent palette.</summary>
    public bool UseLightTheme { get; init; } = true;

    /// <summary>True to follow the OS accent colour.</summary>
    public bool UseSystemAccent { get; init; } = true;

    /// <summary>Log level name: Trace, Debug, Information, Warning, Error.</summary>
    public string LogLevel { get; init; } = "Information";

    /// <summary>Days of logs to keep before pruning.</summary>
    public int LogRetentionDays { get; init; } = 14;

    /// <summary>Download behaviour.</summary>
    public DownloadOptions Downloads { get; init; } = new();

    /// <summary>Nexus integration state (never contains the key itself).</summary>
    public NexusSettings Nexus { get; init; } = new();

    /// <summary>Translation service used to render mod titles and text in Chinese.</summary>
    public TranslationSettings Translation { get; init; } = new();

    /// <summary>Deployment behaviour.</summary>
    public DeploymentOptions Deployment { get; init; } = new();

    /// <summary>Safety behaviour.</summary>
    public SafetyOptions Safety { get; init; } = new();

    /// <summary>UI preferences.</summary>
    public UiOptions Ui { get; init; } = new();

    /// <summary>Absolute path of the last selected game's install directory, for convenience.</summary>
    public string? LastSelectedGameId { get; init; }
}

/// <summary>Nexus integration settings. Holds no credential material.</summary>
public sealed record NexusSettings
{
    /// <summary>Which auth mechanism the user configured.</summary>
    public NexusAuthMode AuthMode { get; init; } = NexusAuthMode.None;

    /// <summary>True when an API key is present in the secret store (mirrored here for the UI only).</summary>
    public bool HasApiKey { get; init; }

    /// <summary>Cached account name for display.</summary>
    public string? UserName { get; init; }

    /// <summary>True when the cached account is premium. Used only to explain rate limits.</summary>
    public bool IsPremium { get; init; }

    /// <summary>Last successful authentication, for the Settings card.</summary>
    public DateTimeOffset? LastAuthenticatedAt { get; init; }

    /// <summary>Include adult-flagged results in search. Off by default; the user must opt in.</summary>
    public bool IncludeAdultContent { get; init; }

    /// <summary>How long search responses may be cached.</summary>
    public TimeSpan MetadataCacheDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Minimum interval between API calls, to stay polite and inside rate limits.</summary>
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>Deployment behaviour settings.</summary>
public sealed record DeploymentOptions
{
    /// <summary>
    /// Ordered methods the engine may try. The game definition can narrow this further, but never
    /// widen it beyond what the user allowed — a user who distrusts hardlinks must be able to say so.
    /// </summary>
    public IReadOnlyList<DeploymentMethod> AllowedMethods { get; init; } =
        [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy];

    /// <summary>Back up a game file before overwriting it. Turning this off is explicitly unsafe.</summary>
    public bool BackupBeforeOverwrite { get; init; } = true;

    /// <summary>Verify hashes after deploying.</summary>
    public bool VerifyAfterDeploy { get; init; } = true;

    /// <summary>Roll back automatically when a deploy fails partway.</summary>
    public bool RollbackOnFailure { get; init; } = true;

    /// <summary>Keep journals after a successful commit, for auditing.</summary>
    public bool KeepCommittedJournals { get; init; } = true;

    /// <summary>Delete backups older than this during maintenance. Null keeps them forever.</summary>
    public TimeSpan? BackupRetention { get; init; }

    /// <summary>Warn and require confirmation when a mod is not symmetric (undeploy will not restore the game exactly).</summary>
    public bool WarnOnAsymmetricDeploy { get; init; } = true;
}

/// <summary>Safety settings for <see cref="PathSafetyService"/>.</summary>
public sealed record SafetyOptions
{
    /// <summary>
    /// Extra protected path prefixes, in addition to the built-in drive-root and system-directory
    /// blocklist. Users can add a directory they never want touched.
    /// </summary>
    public IReadOnlyList<string> ExtraProtectedPaths { get; init; } = [];

    /// <summary>
    /// Trusted game roots the user approved. Every write must fall inside one of these.
    /// </summary>
    public IReadOnlyList<string> ApprovedGameRoots { get; init; } = [];

    /// <summary>Refuse recursive delete outright. On by default; disabling it is a footgun.</summary>
    public bool RefuseRecursiveDeleteOutsideModRoot { get; init; } = true;

    /// <summary>Refuse to follow reparse points when resolving a path for deletion.</summary>
    public bool RefuseReparsePointsOnDelete { get; init; } = true;

    /// <summary>Maximum path length we will attempt; beyond this we refuse rather than rely on undecorated paths.</summary>
    public int MaxPathLength { get; init; } = 32_000;
}

/// <summary>UI preferences.</summary>
public sealed record UiOptions
{
    /// <summary>Show the installation preview for every install, not only low-confidence ones.</summary>
    public bool AlwaysShowInstallPreview { get; init; }

    /// <summary>Page name opened at startup; empty means the first page (Dashboard).</summary>
    public string StartupPageName { get; init; } = string.Empty;

    /// <summary>Columns shown on the Mods page.</summary>
    public IReadOnlyList<string> ModColumns { get; init; } =
        ["Name", "Author", "Version", "Source", "State", "Conflicts"];

    /// <summary>Page size for the Nexus browser.</summary>
    public int NexusPageSize { get; init; } = 20;

    /// <summary>True to confirm before uninstalling.</summary>
    public bool ConfirmUninstall { get; init; } = true;

    /// <summary>True to play a subtle highlight when a mod state changes.</summary>
    public bool AnimateStateChanges { get; init; } = true;
}
