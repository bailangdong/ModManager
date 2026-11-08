namespace ModManager.Core;

/// <summary>How a local mod relates to the online entry it came from.</summary>
public enum NexusLinkState
{
    /// <summary>The local mod carries no Nexus id, so nothing can be checked automatically.</summary>
    NotLinked = 0,

    /// <summary>Linked, but no version information has been retrieved yet.</summary>
    Unknown = 1,

    /// <summary>Linked and the installed version matches the published one.</summary>
    UpToDate = 2,

    /// <summary>Linked and a newer version is published.</summary>
    UpdateAvailable = 3,

    /// <summary>The check could not be completed (offline, not authenticated, rate limited).</summary>
    CheckFailed = 4,
}

/// <summary>
/// The association between one installed mod and its Nexus entry.
/// </summary>
/// <param name="LocalModId">Local mod id.</param>
/// <param name="LocalName">Local display name.</param>
/// <param name="NexusModId">Nexus mod id the mod was installed from.</param>
/// <param name="InstalledVersion">Version currently installed.</param>
/// <param name="LatestVersion">Newest published version, when known.</param>
/// <param name="State">Relationship state.</param>
/// <param name="Note">Explanation for the interface.</param>
public sealed record NexusLibraryLink(
    string LocalModId,
    string LocalName,
    int NexusModId,
    string? InstalledVersion,
    string? LatestVersion,
    NexusLinkState State,
    string? Note)
{
    /// <summary>True when an update can be offered.</summary>
    public bool CanUpdate => State == NexusLinkState.UpdateAvailable;

    /// <summary>Badge text in Chinese.</summary>
    public string StateText => State switch
    {
        NexusLinkState.NotLinked => "未关联",
        NexusLinkState.Unknown => "未检查",
        NexusLinkState.UpToDate => "已是最新",
        NexusLinkState.UpdateAvailable => "有更新",
        NexusLinkState.CheckFailed => "检查失败",
        _ => State.ToString(),
    };
}

/// <summary>
/// Associates installed mods with the Nexus mods they came from, and checks them for updates.
/// </summary>
/// <remarks>
/// Association uses the stable Nexus mod id recorded at install time. Name matching is deliberately not
/// used: two different mods can share a name, and one mod can be renamed upstream, so a name-based guess
/// would eventually offer the user an update to somebody else's mod. A mod without a recorded id is
/// reported as <see cref="NexusLinkState.NotLinked"/> rather than guessed at.
/// </remarks>
public interface INexusLibraryLinker
{
    /// <summary>Associates installed mods with Nexus ids, without any network access.</summary>
    /// <param name="mods">Installed mods.</param>
    IReadOnlyList<NexusLibraryLink> LinksFor(IReadOnlyList<ModMetadata> mods);

    /// <summary>Asks Nexus for newer versions of the linked mods.</summary>
    /// <param name="gameDomain">Game domain to query.</param>
    /// <param name="mods">Installed mods.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationResult<IReadOnlyList<NexusLibraryLink>>> CheckForUpdatesAsync(
        string gameDomain,
        IReadOnlyList<ModMetadata> mods,
        CancellationToken cancellationToken = default);

    /// <summary>Finds the installed mod (if any) that corresponds to a Nexus mod id.</summary>
    /// <param name="mods">Installed mods.</param>
    /// <param name="nexusModId">Nexus mod id.</param>
    ModMetadata? FindInstalled(IReadOnlyList<ModMetadata> mods, int nexusModId);
}
