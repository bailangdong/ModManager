using ModManager.Core.Safety;

namespace ModManager.Core.Services;

/// <summary>
/// Detects and explains file-level conflicts between mods.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — Modrix's three-layer override model, read straight off its schema:
/// <c>file_overrides</c> &gt; <c>mod_rules</c> &gt; load order. The comment on <c>file_overrides</c>
/// states it exactly: "a per-file exception - for this one target the named mod provides the file,
/// <b>regardless of mod rules or load order</b>."
/// </para>
/// <para>
/// The user's spec §12 requires that the user can see <c>Mod A → a.pak → Mod B overrides</c> and can
/// choose A, choose B, or disable either, and that nothing is ever silently overwritten. Modrix's
/// plan reports the same thing and adds a commitment worth keeping: it lists <i>every</i> shadowed
/// mod, not just the loser nearest the top, so "nothing is hidden".
/// </para>
/// </remarks>
public sealed class ConflictService : IConflictService
{
    /// <inheritdoc />
    public IReadOnlyList<FileConflict> Analyze(
        string gameId,
        IReadOnlyList<ModSetEntry> mods,
        IReadOnlyList<ConflictResolution> overrides)
    {
        ArgumentNullException.ThrowIfNull(mods);

        _ = gameId;
        var contenders = BuildContenders(mods);
        var overrideLookup = BuildOverrideLookup(overrides);
        var conflicts = new List<FileConflict>();

        foreach (var (target, providers) in contenders)
        {
            if (providers.Count < 2)
            {
                continue;
            }

            var winner = providers[^1];
            if (overrideLookup.TryGetValue(target, out var rule)
                && providers.FirstOrDefault(p => string.Equals(p.ModId, rule.ModId, StringComparison.Ordinal)) is { } pinned)
            {
                winner = pinned;
            }

            var shadowed = providers
                .Where(p => !string.Equals(p.ModId, winner.ModId, StringComparison.Ordinal))
                .Select(p => p.ModId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var isResolved = overrideLookup.ContainsKey(target);

            conflicts.Add(new FileConflict(
                target,
                winner.ModId,
                shadowed,
                isResolved ? ConflictResolutionKind.PreferMod : ConflictResolutionKind.Undecided,
                IsBlocking: false,
                Reason: isResolved
                    ? $"'{winner.ModId}' supplies this file because you set a rule for it."
                    : $"'{winner.ModId}' loads after {string.Join(", ", shadowed.Select(m => $"'{m}'"))}."));
        }

        // Unresolved first, then by how many mods are involved. Modrix orders its summary the same
        // way, and it is the right order: the user's attention should go to the undecided, most
        // entangled conflicts before the already-settled trivia.
        return conflicts
            .OrderBy(c => c.Resolution != ConflictResolutionKind.Undecided)
            .ThenByDescending(c => c.OverriddenModIds.Count)
            .ThenBy(c => c.RelativeTarget, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public ConflictExplanation Explain(string gameId, string relativeTarget, IReadOnlyList<ModSetEntry> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);

        _ = gameId;
        var contenders = BuildContenders(mods);

        if (!contenders.TryGetValue(relativeTarget, out var providers) || providers.Count == 0)
        {
            return new ConflictExplanation(
                relativeTarget,
                [],
                string.Empty,
                $"{relativeTarget} is not provided by any enabled mod.");
        }

        var winner = providers[^1];
        var steps = new List<ConflictStep>();

        foreach (var provider in providers)
        {
            var entry = mods.FirstOrDefault(m => string.Equals(m.Metadata.Id, provider.ModId, StringComparison.Ordinal));
            var size = TryGetSize(provider.SourcePath);

            steps.Add(new ConflictStep(
                provider.ModId,
                entry?.Metadata.Name ?? provider.ModId,
                provider.SourcePath,
                size,
                Wins: string.Equals(provider.ModId, winner.ModId, StringComparison.Ordinal),
                IsDisabled: false));
        }

        var explanation = providers.Count == 1
            ? $"Only '{winner.ModId}' provides {relativeTarget}."
            : $"'{winner.ModId}' wins {relativeTarget} because it loads last of {providers.Count} mods providing it.";

        return new ConflictExplanation(relativeTarget, steps, winner.ModId, explanation);
    }

    /// <summary>
    /// Builds the contender map: every path any mod provides, with all providers in resolution order.
    /// </summary>
    /// <param name="mods">Mods, already in resolution order.</param>
    /// <remarks>
    /// The same enumeration rules as the planner apply (dot-prefixed entries excluded, forbidden
    /// segments refused), so the conflict report can never describe a file the planner would not
    /// deploy. Two code paths disagreeing about what is deployable is exactly the kind of drift that
    /// makes a "conflict" appear or vanish depending on which screen you look at.
    /// </remarks>
    private static SortedDictionary<string, List<(string ModId, string SourcePath)>> BuildContenders(
        IReadOnlyList<ModSetEntry> mods)
    {
        var contenders = new SortedDictionary<string, List<(string ModId, string SourcePath)>>(StringComparer.Ordinal);

        foreach (var mod in mods)
        {
            if (!Directory.Exists(FileSystemOps.ToExtendedPath(mod.StagingRoot)))
            {
                continue;
            }

            foreach (var file in FileSystemOps.EnumerateFilesSafe(mod.StagingRoot))
            {
                var relative = Path.GetRelativePath(mod.StagingRoot, file).Replace('\\', '/');
                if (IsExcluded(relative) || PathSafetyService.ContainsForbiddenSegment(relative, out _))
                {
                    continue;
                }

                if (!contenders.TryGetValue(relative, out var providers))
                {
                    providers = [];
                    contenders[relative] = providers;
                }

                // A mod cannot conflict with itself: a repeated path from the same mod replaces its own
                // earlier claim rather than adding a contender.
                if (providers.Count > 0 && string.Equals(providers[^1].ModId, mod.Metadata.Id, StringComparison.Ordinal))
                {
                    providers[^1] = (mod.Metadata.Id, file);
                    continue;
                }

                providers.Add((mod.Metadata.Id, file));
            }
        }

        return contenders;
    }

    /// <summary>Builds an override lookup keyed case-insensitively.</summary>
    /// <param name="overrides">Overrides to index.</param>
    private static Dictionary<string, ConflictResolution> BuildOverrideLookup(IReadOnlyList<ConflictResolution> overrides)
    {
        var lookup = new Dictionary<string, ConflictResolution>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in overrides)
        {
            lookup[rule.RelativeTarget] = rule;
        }

        return lookup;
    }

    /// <summary>True when a staged relative path is excluded from deployment.</summary>
    /// <param name="relativePath">Relative path.</param>
    private static bool IsExcluded(string relativePath)
        => DeploymentPlanner.IsExcluded(relativePath);

    /// <summary>Reads a file size, tolerating failure.</summary>
    /// <param name="path">File to stat.</param>
    private static long TryGetSize(string path)
    {
        try
        {
            return new FileInfo(FileSystemOps.ToExtendedPath(path)).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
