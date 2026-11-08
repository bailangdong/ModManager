namespace ModManager.Core.Services;

/// <summary>
/// Default implementation of <see cref="INexusLibraryLinker"/> on top of the existing provider.
/// </summary>
/// <remarks>
/// This is an adapter, not a second client: all HTTP, authentication and rate limiting stay inside
/// <see cref="INexusProvider"/>. The linker only decides which local mods are comparable to which online
/// entries, and translates the provider's answer into a state the interface can show.
/// </remarks>
public sealed class NexusLibraryLinker : INexusLibraryLinker
{
    private readonly INexusProvider _provider;

    /// <summary>Creates the linker.</summary>
    /// <param name="provider">Nexus provider.</param>
    public NexusLibraryLinker(INexusProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    /// <inheritdoc />
    public IReadOnlyList<NexusLibraryLink> LinksFor(IReadOnlyList<ModMetadata> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var links = new List<NexusLibraryLink>();
        foreach (var mod in mods)
        {
            if (mod.NexusModId is not { } nexusId)
            {
                continue;
            }

            var state = mod.AvailableVersion is { Length: > 0 } available
                && VersionOrder.IsNewer(available, mod.Version)
                    ? NexusLinkState.UpdateAvailable
                    : NexusLinkState.Unknown;

            links.Add(new NexusLibraryLink(
                mod.Id,
                mod.Name,
                nexusId,
                mod.Version,
                mod.AvailableVersion,
                state,
                state == NexusLinkState.UpdateAvailable
                    ? $"可更新到 {mod.AvailableVersion}。"
                    : "已记录 Nexus 标识，尚未检查版本。"));
        }

        return links;
    }

    /// <inheritdoc />
    public ModMetadata? FindInstalled(IReadOnlyList<ModMetadata> mods, int nexusModId)
    {
        ArgumentNullException.ThrowIfNull(mods);
        return mods.FirstOrDefault(mod => mod.NexusModId == nexusModId);
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<NexusLibraryLink>>> CheckForUpdatesAsync(
        string gameDomain,
        IReadOnlyList<ModMetadata> mods,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        ArgumentNullException.ThrowIfNull(mods);

        var linked = mods.Where(mod => mod.NexusModId is not null).ToList();
        if (linked.Count == 0)
        {
            return OperationResult<IReadOnlyList<NexusLibraryLink>>.Ok([]);
        }

        var triples = linked
            .Select(mod => (mod.Id, mod.NexusModId!.Value, mod.Version))
            .ToList();

        var result = await _provider
            .CheckForUpdatesAsync(gameDomain, triples, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded || result.Value is null)
        {
            // A failed check is reported as such rather than as "up to date": claiming a mod is current
            // when we could not ask is exactly the kind of quiet lie this application must not tell.
            var failed = linked
                .Select(mod => new NexusLibraryLink(
                    mod.Id,
                    mod.Name,
                    mod.NexusModId!.Value,
                    mod.Version,
                    null,
                    NexusLinkState.CheckFailed,
                    result.Error ?? "无法检查更新。"))
                .ToList();

            // The per-mod links are still returned alongside the failure so the interface can show which
            // mods could not be checked; the record is constructed directly because the value-carrying
            // failure case has no helper.
            return new OperationResult<IReadOnlyList<NexusLibraryLink>>(
                false,
                failed,
                result.Error ?? "无法检查更新。");
        }

        var byModId = result.Value.ToDictionary(info => info.ModId, StringComparer.Ordinal);
        var links = new List<NexusLibraryLink>();

        foreach (var mod in linked)
        {
            if (!byModId.TryGetValue(mod.Id, out var info))
            {
                links.Add(new NexusLibraryLink(
                    mod.Id,
                    mod.Name,
                    mod.NexusModId!.Value,
                    mod.Version,
                    null,
                    NexusLinkState.Unknown,
                    "该 Mod 未在本游戏的返回结果中，无法判断版本。"));
                continue;
            }

            var hasUpdate = VersionOrder.IsNewer(info.LatestVersion, mod.Version)
                || (info.HasUpdate && VersionOrder.Compare(info.LatestVersion, mod.Version) > 0);

            links.Add(new NexusLibraryLink(
                mod.Id,
                mod.Name,
                mod.NexusModId!.Value,
                mod.Version,
                info.LatestVersion,
                hasUpdate ? NexusLinkState.UpdateAvailable : NexusLinkState.UpToDate,
                hasUpdate
                    ? $"可更新到 {info.LatestVersion}。"
                    : "已是最新版本。"));
        }

        return OperationResult<IReadOnlyList<NexusLibraryLink>>.Ok(links);
    }
}
