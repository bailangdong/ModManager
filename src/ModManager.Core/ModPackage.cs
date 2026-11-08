namespace ModManager.Core;

/// <summary>
/// Identity of a mod package at its source. Kept deliberately platform-neutral: Nexus,
/// Thunderstore and a user-picked local archive all fit the same shape.
/// </summary>
/// <remarks>
/// The three objects the product must never conflate are separated in code:
/// <list type="number">
/// <item><see cref="PackageSourceRef"/> — the mod as it exists on a platform.</item>
/// <item><see cref="ModPackage"/> — the bytes we downloaded or imported, in the local library.</item>
/// <item><see cref="ModMetadata"/> — one staged/deployed instance of those bytes in a game.</item>
/// </list>
/// A package can be installed into more than one game or profile without being downloaded again.
/// </remarks>
public sealed record PackageSourceRef
{
    /// <summary>Stable platform key, e.g. <c>nexusmods</c>, <c>thunderstore</c>, <c>github</c>, <c>local</c>.</summary>
    public required string Platform { get; init; }

    /// <summary>
    /// Canonical game key in the application's namespace (the same value as
    /// <c>GameDefinition.GameId</c>), e.g. <c>stardewvalley</c> or <c>skyrimse</c>. The platform's own
    /// domain, when it differs, belongs in <see cref="PageUrl"/>, not in this key.
    /// </summary>
    public required string GameKey { get; init; }

    /// <summary>Mod key in the platform's own namespace.</summary>
    public required string ModKey { get; init; }

    /// <summary>File/version key when the platform distinguishes several files for one mod.</summary>
    public string? FileKey { get; init; }

    /// <summary>Human-facing page URL, without credentials or short-lived download tokens.</summary>
    public string? PageUrl { get; init; }

    /// <summary>True when this source identifies a concrete package (platform + game + mod + file).</summary>
    public bool IsConcrete =>
        !string.IsNullOrWhiteSpace(Platform)
        && !string.IsNullOrWhiteSpace(GameKey)
        && !string.IsNullOrWhiteSpace(ModKey)
        && !string.IsNullOrWhiteSpace(FileKey);
}

/// <summary>What the local package library knows about one archive.</summary>
public enum PackageState
{
    /// <summary>Archive present and hash recorded; ready to install.</summary>
    Ready = 0,

    /// <summary>Archive was removed or moved; metadata remains for history.</summary>
    Missing = 1,

    /// <summary>Archive no longer matches the recorded hash; do not install without re-acquiring.</summary>
    Corrupt = 2,
}

/// <summary>
/// One downloaded or imported archive in the local package library.
/// </summary>
/// <remarks>
/// This is the hand-off object between acquisition and installation. It contains no game path and
/// no platform API handle: the installer receives the archive plus this metadata and never needs
/// to know whether the bytes came from Nexus, Thunderstore, GitHub or a file dialog.
/// </remarks>
public sealed record ModPackage
{
    /// <summary>Stable package id. For remote packages: platform/game/mod/file. For local: local/hash.</summary>
    public required string PackageId { get; init; }

    /// <summary>Where the package came from.</summary>
    public required PackageSourceRef Source { get; init; }

    /// <summary>Display name captured at acquisition time.</summary>
    public required string Name { get; init; }

    /// <summary>Author as declared by the source, when known.</summary>
    public string? Author { get; init; }

    /// <summary>Version string as declared by the source, when known.</summary>
    public string? Version { get; init; }

    /// <summary>Short description / summary, when known.</summary>
    public string? Description { get; init; }

    /// <summary>Absolute path of the archive inside the package library.</summary>
    public required string ArchivePath { get; init; }

    /// <summary>SHA-256 of the archive bytes. The dedupe and integrity key.</summary>
    public required string ArchiveSha256 { get; init; }

    /// <summary>Archive size in bytes.</summary>
    public long ArchiveSizeBytes { get; init; }

    /// <summary>Detected archive format.</summary>
    public ArchiveFormat ArchiveFormat { get; init; } = ArchiveFormat.Zip;

    /// <summary>When the package entered the library.</summary>
    public DateTimeOffset AcquiredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the hash was last verified.</summary>
    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>Current availability of the archive.</summary>
    public PackageState State { get; init; } = PackageState.Ready;

    /// <summary>Dependency declarations captured with the package, if the source supplied them.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>Optional hint from the source/analyzer: FOMOD, BepInEx, SMAPI, etc.</summary>
    public string? InstallMethodHint { get; init; }

    /// <summary>Optional cached cover image path, relative to the app data directory.</summary>
    public string? CoverImagePath { get; init; }

    /// <summary>True when the archive still exists at <see cref="ArchivePath"/>.</summary>
    public bool IsArchivePresent => File.Exists(ArchivePath);
}

/// <summary>Request to commit a completed download or an imported archive into the library.</summary>
public sealed record PackageCommitRequest
{
    /// <summary>Archive file to take ownership of (copied, never moved from a user-selected path).</summary>
    public required string ArchivePath { get; init; }

    /// <summary>Platform-neutral source identity.</summary>
    public required PackageSourceRef Source { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Author, when known.</summary>
    public string? Author { get; init; }

    /// <summary>Version, when known.</summary>
    public string? Version { get; init; }

    /// <summary>Description, when known.</summary>
    public string? Description { get; init; }

    /// <summary>Expected SHA-256 from the platform, when supplied.</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>Expected size from the platform, when supplied.</summary>
    public long? ExpectedSizeBytes { get; init; }

    /// <summary>Dependency declarations captured from the platform.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>Optional install-method hint.</summary>
    public string? InstallMethodHint { get; init; }

    /// <summary>Optional cached cover image path.</summary>
    public string? CoverImagePath { get; init; }
}

/// <summary>Result of importing a local archive.</summary>
/// <param name="Package">The package in the library.</param>
/// <param name="WasAlreadyPresent">True when an identical archive was already in the library.</param>
public sealed record PackageImportResult(ModPackage Package, bool WasAlreadyPresent);

/// <summary>Counters for the library page and diagnostics.</summary>
public sealed record PackageLibrarySummary(
    int Total,
    int Ready,
    int Missing,
    int Corrupt,
    long TotalBytes);

/// <summary>Well-known platform keys, so call sites do not spell them differently.</summary>
public static class PackagePlatforms
{
    public const string NexusMods = "nexusmods";
    public const string Thunderstore = "thunderstore";
    public const string GitHub = "github";
    public const string CurseForge = "curseforge";
    public const string Local = "local";
    public const string Other = "other";
}
