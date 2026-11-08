using System.Security.Cryptography;
using System.Text;
using ModManager.Core.Safety;

namespace ModManager.Core.Services;

/// <summary>
/// Builds a deployment plan from a profile and a mod set, touching no game files.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — Modrix's <c>deploy/plan.rs</c>, whose header states it performs "no I/O
/// whatsoever … makes it exhaustively unit-testable". Everything this class reads is the staging
/// area; everything it produces is data.
/// </para>
/// <para>
/// <b>The virtual file tree.</b> Not a tree — a flat desired-state map from
/// target-relative-path to the single winning provider. It is built by walking the enabled mods in
/// resolution order and letting <b>later mods win</b>, exactly as Modrix does ("later mod wins").
/// The plan is then the difference between that desired state and what the manifests say is
/// currently deployed.
/// </para>
/// <para>
/// <b>Determinism.</b> Every intermediate collection is a <see cref="SortedDictionary{TKey,TValue}"/>
/// with <see cref="StringComparer.Ordinal"/>, mirroring Modrix's use of <c>BTreeMap</c>. Note this
/// is deliberately <i>ordinal</i>, not culture-aware: Rust's string ordering is by bytes, and a
/// culture-sensitive sort would make the plan hash differ between machines that disagree about
/// collation. A stable order is what makes the plan hash a valid idempotence key.
/// </para>
/// </remarks>
public sealed class DeploymentPlanner : IDeploymentPlanner
{
    private readonly IPathSafetyService _pathSafety;
    private readonly IAppLogger? _logger;

    /// <summary>Creates the planner.</summary>
    /// <param name="pathSafety">Used to classify each planned path, never to mutate.</param>
    /// <param name="logger">Optional logger.</param>
    /// <remarks>
    /// Note there is no <c>IConflictService</c> dependency here. Detecting conflicts requires exactly
    /// the contender data this planner already builds while resolving the virtual tree, so conflicts
    /// are a by-product of planning rather than a second pass that could disagree with it. Modrix
    /// makes the same choice: its plan exposes <c>conflicts()</c> directly.
    /// </remarks>
    public DeploymentPlanner(IPathSafetyService pathSafety, IAppLogger? logger = null)
    {
        _pathSafety = pathSafety;
        _logger = logger;
    }

    /// <inheritdoc />
    public DeploymentPlan BuildPlan(
        GameDefinition definition,
        string installPath,
        Profile profile,
        IReadOnlyList<ModSetEntry> mods,
        IReadOnlyList<ConflictResolution> conflictOverrides)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(profile);

        var deploymentRoot = ResolveDeploymentRoot(definition, installPath);
        var enabled = OrderEnabledMods(profile, mods);
        var warnings = new List<string>();

        var resolved = ResolveDesiredTree(enabled, warnings);

        // Overrides come from two places: the profile's own per-path decisions, and the repository's
        // rules. Profile decisions win, because they are the more specific, more recent statement of
        // intent. Both are applied here so the plan and the reported conflicts agree.
        var overrides = MergeOverrides(profile, conflictOverrides);
        ApplyOverrides(resolved, overrides, warnings);
        var conflicts = BuildConflicts(resolved, overrides);

        return BuildOperations(definition, profile, deploymentRoot, enabled, mods, resolved, conflicts, warnings);
    }

    /// <inheritdoc />
    public DeploymentPlan BuildPreviewPlan(
        GameDefinition definition,
        string installPath,
        ModMetadata mod,
        IReadOnlyList<ModSetEntry> existingMods)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(mod);

        var deploymentRoot = ResolveDeploymentRoot(definition, installPath);
        var warnings = new List<string>();

        // The preview appends the candidate last, so it wins conflicts in the preview exactly as it
        // would after a real install. That keeps the preview honest rather than optimistic.
        var ordered = existingMods.ToList();
        ordered.Add(new ModSetEntry(mod, null, mod.StagingPath));

        var resolved = ResolveDesiredTree(ordered, warnings);
        var previewConflicts = BuildConflicts(resolved, new Dictionary<string, ConflictResolution>(StringComparer.OrdinalIgnoreCase));

        var files = new List<PlannedFile>();
        var operations = new List<FileOperation>();
        var rejections = new List<string>();

        foreach (var (relativeTarget, provider) in resolved.Desired)
        {
            if (string.Equals(provider.ModId, mod.Id, StringComparison.Ordinal))
            {
                var safety = ClassifyPath(definition.Id, deploymentRoot, relativeTarget, out var note, out var absolute);
                if (safety == PathSafetyVerdict.Unsafe)
                {
                    rejections.Add($"{relativeTarget}: {note}");
                    files.Add(new PlannedFile(relativeTarget, provider.SourcePath, provider.ModId, safety, note));
                    continue;
                }

                files.Add(new PlannedFile(relativeTarget, provider.SourcePath, provider.ModId, safety, note));
                operations.Add(new PlaceFileOperation
                {
                    TargetPath = absolute,
                    RelativeTarget = relativeTarget,
                    SourcePath = provider.SourcePath,
                    ModId = provider.ModId,
                    Method = PreferredMethod(definition),
                });
            }
        }

        var planHash = ComputePlanHash(definition.Id, profileId: "preview", deploymentRoot, files);

        return new DeploymentPlan
        {
            GameId = definition.Id,
            ProfileId = "preview",
            DeploymentRoot = deploymentRoot,
            Files = files,
            Operations = operations,
            Conflicts = previewConflicts,
            Warnings = warnings,
            EnabledModsInOrder = ordered.Select(m => m.Metadata.Id).ToList(),
            PlanHash = planHash,
        };
    }

    /// <summary>
    /// Orders the mods that participate in the plan: profile order first, then anything the profile
    /// has not seen appended in install order.
    /// </summary>
    /// <param name="profile">Profile supplying order and disabled set.</param>
    /// <param name="mods">All candidate mods.</param>
    /// <remarks>
    /// Order preference: explicit profile order, then install time, then id. The trailing id
    /// comparison matters — without a total order, two mods installed in the same millisecond would
    /// make the plan depend on the enumeration order of a dictionary, and determinism would be lost.
    /// </remarks>
    private static List<ModSetEntry> OrderEnabledMods(Profile profile, IReadOnlyList<ModSetEntry> mods)
    {
        var disabled = new HashSet<string>(profile.DisabledModIds, StringComparer.Ordinal);
        var byId = new Dictionary<string, ModSetEntry>(StringComparer.Ordinal);
        foreach (var mod in mods)
        {
            byId[mod.Metadata.Id] = mod;
        }

        var ordered = new List<ModSetEntry>(mods.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var modId in profile.ModOrder)
        {
            if (disabled.Contains(modId) || !seen.Add(modId))
            {
                continue;
            }

            if (byId.TryGetValue(modId, out var entry))
            {
                ordered.Add(entry);
            }
        }

        var remainder = mods
            .Where(m => !seen.Contains(m.Metadata.Id) && !disabled.Contains(m.Metadata.Id))
            .OrderBy(m => m.Metadata.InstalledAt)
            .ThenBy(m => m.Metadata.Id, StringComparer.Ordinal);

        ordered.AddRange(remainder);
        return ordered;
    }

    /// <summary>
    /// Resolves the flat desired-state map, recording every provider of every contested path.
    /// </summary>
    /// <param name="orderedMods">Mods in resolution order, lowest priority first.</param>
    /// <param name="warnings">Collects non-fatal remarks.</param>
    /// <remarks>
    /// Returns the contender lists as well as the winners. Keeping both is what lets us report every
    /// shadowed mod by name — Modrix's "nothing is hidden" commitment — while the plan itself stays
    /// compact. The extra memory is bounded by the number of contested paths, which is small.
    /// </remarks>
    private ResolutionResult ResolveDesiredTree(
        IReadOnlyList<ModSetEntry> orderedMods,
        List<string> warnings)
    {
        var contenders = new SortedDictionary<string, List<ProviderEntry>>(StringComparer.Ordinal);

        foreach (var mod in orderedMods)
        {
            foreach (var (relativeTarget, sourcePath) in EnumerateStagedFiles(mod, warnings))
            {
                if (!contenders.TryGetValue(relativeTarget, out var providers))
                {
                    providers = [];
                    contenders[relativeTarget] = providers;
                }

                // Same mod providing the same target twice: the last one wins and this is not a
                // conflict, because a mod cannot disagree with itself.
                if (providers.Count > 0 && string.Equals(providers[^1].ModId, mod.Metadata.Id, StringComparison.Ordinal))
                {
                    providers[^1] = new ProviderEntry(mod.Metadata.Id, sourcePath);
                    continue;
                }

                providers.Add(new ProviderEntry(mod.Metadata.Id, sourcePath));
            }
        }

        var desired = new SortedDictionary<string, ProviderEntry>(StringComparer.Ordinal);
        foreach (var (target, providers) in contenders)
        {
            desired[target] = providers[^1];
        }

        return new ResolutionResult(desired, contenders);
    }

    /// <summary>
    /// Merges per-path decisions from the profile and the repository into one lookup.
    /// </summary>
    /// <param name="profile">Profile supplying its own decisions.</param>
    /// <param name="repositoryOverrides">Rules stored for the game.</param>
    /// <remarks>
    /// Profile decisions are applied last so they win: a profile is the user's statement about
    /// <i>this</i> mod set, which is more specific than a game-wide rule.
    /// </remarks>
    private static Dictionary<string, ConflictResolution> MergeOverrides(
        Profile profile,
        IReadOnlyList<ConflictResolution> repositoryOverrides)
    {
        var merged = new Dictionary<string, ConflictResolution>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in repositoryOverrides)
        {
            merged[rule.RelativeTarget] = rule;
        }

        foreach (var (target, rule) in profile.ConflictOverrides)
        {
            merged[target] = rule;
        }

        return merged;
    }

    /// <summary>
    /// Forces the desired tree to honour per-path overrides, and removes a mod from a path when the
    /// user asked for it to be excluded.
    /// </summary>
    /// <param name="resolved">Resolution result, mutated in place.</param>
    /// <param name="overrides">Merged overrides.</param>
    /// <param name="warnings">Collects non-fatal remarks.</param>
    /// <remarks>
    /// An override naming a mod that does not compete for the path is inert — Modrix asserts exactly
    /// this, and we surface it rather than silently doing nothing, because a rule that looks active
    /// in the UI but has no effect is worse than no rule at all.
    /// </remarks>
    private static void ApplyOverrides(
        ResolutionResult resolved,
        Dictionary<string, ConflictResolution> overrides,
        List<string> warnings)
    {
        foreach (var (target, rule) in overrides)
        {
            if (rule.RelativeTarget == "*")
            {
                continue;
            }

            if (!resolved.Contenders.TryGetValue(target, out var contenders))
            {
                warnings.Add($"Conflict rule for '{target}' was ignored: no enabled mod provides that file.");
                continue;
            }

            var named = contenders.FirstOrDefault(c => string.Equals(c.ModId, rule.ModId, StringComparison.Ordinal));
            if (named is null)
            {
                warnings.Add($"Conflict rule for '{target}' was ignored: '{rule.ModId}' does not provide that file.");
                continue;
            }

            if (rule.Kind == ConflictResolutionKind.ExcludeMod)
            {
                resolved.Desired.Remove(target);
                continue;
            }

            resolved.Desired[target] = named;
        }
    }

    /// <summary>
    /// Turns the contender lists into the conflict set.
    /// </summary>
    /// <param name="resolved">Resolution result, after overrides.</param>
    /// <param name="overrides">Merged overrides, so resolved conflicts can say so.</param>
    /// <remarks>
    /// A conflict whose winner was pinned by an override is still <i>reported</i> — it is merely
    /// marked resolved. Modrix's test for this ("an override pins the winner") makes the same choice,
    /// and it matters: hiding a resolved conflict would make later un-pinning look like a new
    /// problem appearing out of nowhere.
    /// </remarks>
    private static IReadOnlyList<FileConflict> BuildConflicts(
        ResolutionResult resolved,
        Dictionary<string, ConflictResolution> overrides)
    {
        var conflicts = new List<FileConflict>();

        foreach (var (target, contenders) in resolved.Contenders)
        {
            if (contenders.Count < 2)
            {
                continue;
            }

            // After an exclusion the path may have no provider at all, which is a deliberate outcome,
            // not a conflict.
            if (!resolved.Desired.TryGetValue(target, out var winner))
            {
                continue;
            }

            var pinned = overrides.TryGetValue(target, out var rule)
                && rule.Kind == ConflictResolutionKind.PreferMod
                && string.Equals(rule.ModId, winner.ModId, StringComparison.Ordinal);

            var shadowed = contenders
                .Where(c => !string.Equals(c.ModId, winner.ModId, StringComparison.Ordinal))
                .Select(c => c.ModId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            conflicts.Add(new FileConflict(
                target,
                winner.ModId,
                shadowed,
                pinned ? ConflictResolutionKind.PreferMod : ConflictResolutionKind.Undecided,
                IsBlocking: false,
                Reason: pinned
                    ? $"'{winner.ModId}' supplies this file because you set a rule for it."
                    : $"'{winner.ModId}' loads after {string.Join(", ", shadowed.Select(m => $"'{m}'"))}, so it supplies this file."));
        }

        return conflicts;
    }

    /// <summary>
    /// Enumerates a staged mod's deployable files as (relative target, absolute source) pairs.
    /// </summary>
    /// <param name="mod">Mod to enumerate.</param>
    /// <param name="warnings">Collects non-fatal remarks.</param>
    /// <remarks>
    /// Dot-prefixed entries are skipped wholesale, following Modrix's rule that "any entry starting
    /// with <c>.</c>" is excluded from the deploy set. Two reasons, both worth keeping: it makes the
    /// staging area's own bookkeeping directories (<c>.fomod-src</c>, <c>.unmanaged</c>) structurally
    /// undeployable, and it keeps editor and OS droppings (<c>.git</c>, <c>.DS_Store</c>,
    /// <c>Thumbs.db</c>) out of the game folder.
    /// </remarks>
    private List<(string RelativeTarget, string SourcePath)> EnumerateStagedFiles(ModSetEntry mod, List<string> warnings)
    {
        var results = new List<(string, string)>();
        var root = mod.StagingRoot;

        if (!Directory.Exists(FileSystemOps.ToExtendedPath(root)))
        {
            warnings.Add($"Staging directory missing for '{mod.Metadata.Name}': {root}");
            return results;
        }

        foreach (var file in FileSystemOps.EnumerateFilesSafe(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsExcluded(relative))
            {
                continue;
            }

            // A path carrying our own marker characters is refused rather than sanitised, matching
            // Modrix's rule that an archive must never be able to forge an engine marker.
            if (PathSafetyService.ContainsForbiddenSegment(relative, out _))
            {
                warnings.Add($"Skipped '{relative}': the path contains a forbidden segment.");
                continue;
            }

            results.Add((relative, file));
        }

        results.Sort((left, right) => string.CompareOrdinal(left.Item1, right.Item1));
        return results;
    }

    /// <summary>True when a staged relative path must never be deployed.</summary>
    /// <param name="relativePath">Forward-slash relative path.</param>
    private static bool IsExcluded(string relativePath)
    {
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith('.') || segment.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Documentation is not mod content. Leaving it in the deploy set puts README/LICENSE files into
        // the game folder, and they can collide with another mod's identical README. Only the final
        // file name matters, so a mod may keep docs in any subdirectory without being penalised.
        var fileName = Path.GetFileName(relativePath);
        if (DocumentationNames.Any(name =>
                fileName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static readonly string[] DocumentationNames =
    [
        "readme", "read_me", "read-me", "license", "licence", "copying",
        "changelog", "changes", "install", "installation", "说明", "安装说明",
    ];

    /// <summary>
    /// Turns the desired tree plus the current manifests into the ordered operation list.
    /// </summary>
    /// <param name="definition">Game definition.</param>
    /// <param name="profile">Profile.</param>
    /// <param name="deploymentRoot">Absolute deployment root.</param>
    /// <param name="enabled">Enabled mods in order.</param>
    /// <param name="allMods">All mods, so manifests can be consulted.</param>
    /// <param name="resolved">Resolved tree and contender lists.</param>
    /// <param name="conflicts">Conflicts.</param>
    /// <param name="warnings">Collects non-fatal remarks.</param>
    private DeploymentPlan BuildOperations(
        GameDefinition definition,
        Profile profile,
        string deploymentRoot,
        IReadOnlyList<ModSetEntry> enabled,
        IReadOnlyList<ModSetEntry> allMods,
        ResolutionResult resolved,
        IReadOnlyList<FileConflict> conflicts,
        List<string> warnings)
    {
        var desired = resolved.Desired;
        var files = new List<PlannedFile>();
        var removes = new List<FileOperation>();
        var directories = new SortedDictionary<string, CreateDirectoryOperation>(StringComparer.Ordinal);
        var places = new List<FileOperation>();
        var method = PreferredMethod(definition);

        // --- removals -------------------------------------------------------------------------
        // Anything a manifest claims that the desired tree no longer wants, or that is now owned by a
        // different mod, must go. Walking manifests (rather than the disk) is what guarantees a mod can
        // only ever remove files it actually deployed.
        //
        // Every manifest in scope is consulted, enabled or not: a disabled mod's files must actually be
        // removed (the "real disable" rule), and a superseded mod's files must yield to the new owner.
        // The guard against over-removal is <c>stillOurs</c> below — if the desired tree still wants the
        // file from the same mod, nothing happens. All plan callers build the desired tree from the full
        // mod set, so this can only ever remove files that are genuinely no longer wanted.
        foreach (var mod in allMods)
        {
            if (mod.Manifest is not { } manifest)
            {
                continue;
            }

            foreach (var deployed in manifest.Files)
            {
                var stillOurs = desired.TryGetValue(deployed.RelativeTarget, out var provider)
                    && string.Equals(provider.ModId, deployed.ModId, StringComparison.Ordinal);

                if (stillOurs)
                {
                    continue;
                }

                // A target this plan is going to write again (because another mod now wins it) is NOT
                // deleted first: the placement itself backs the existing file up and records it, which
                // keeps both mods' claims truthful and lets the winner's uninstall restore the loser's
                // file. Deleting first would destroy that provenance.
                if (desired.ContainsKey(deployed.RelativeTarget))
                {
                    continue;
                }

                var safety = ClassifyPath(definition.Id, deploymentRoot, deployed.RelativeTarget, out var note, out var absolute);
                if (safety == PathSafetyVerdict.Unsafe)
                {
                    warnings.Add($"Refused to remove '{deployed.RelativeTarget}': {note}");
                    continue;
                }

                removes.Add(new DeleteFileOperation
                {
                    TargetPath = absolute,
                    RelativeTarget = deployed.RelativeTarget,
                    ExpectedHash = deployed.Sha256,
                    ModId = deployed.ModId,
                    BackupPath = deployed.BackupPath,
                    OriginalSha256 = deployed.OriginalSha256,
                });

                files.Add(new PlannedFile(deployed.RelativeTarget, null, deployed.ModId, PathSafetyVerdict.Overwrite, "Remove: no longer enabled or superseded."));
            }
        }

        // --- additions ------------------------------------------------------------------------
        foreach (var (relativeTarget, provider) in desired)
        {
            var safety = ClassifyPath(definition.Id, deploymentRoot, relativeTarget, out var note, out var absolute);
            if (safety == PathSafetyVerdict.Unsafe)
            {
                warnings.Add($"Refused to deploy '{relativeTarget}': {note}");
                files.Add(new PlannedFile(relativeTarget, provider.SourcePath, provider.ModId, safety, note));
                continue;
            }

            // Idempotence (Modrix invariant I2): if a manifest already records this exact mod
            // providing this exact path AND the file actually exists on disk AND this plan is not
            // about to remove that very path, it is left completely alone — not re-hashed, not
            // re-linked. The removal check matters: when another mod's file at the same target is
            // scheduled for deletion, "already deployed" would otherwise skip the placement and the
            // file would vanish from the game folder.
            // A target another mod already claims is always re-placed, never skipped as "unchanged":
            // after a load-order change the file on disk holds the other mod's bytes, and the cheap
            // drift check cannot tell two same-sized files apart.
            var claimedByAnotherMod = allMods.Any(other =>
                other.Manifest is { } otherManifest
                && !string.Equals(other.Metadata.Id, provider.ModId, StringComparison.Ordinal)
                && otherManifest.Files.Any(file =>
                    string.Equals(file.RelativeTarget, relativeTarget, StringComparison.OrdinalIgnoreCase)));

            var scheduledForRemoval = removes
                .OfType<DeleteFileOperation>()
                .Any(op => string.Equals(op.RelativeTarget, relativeTarget, StringComparison.OrdinalIgnoreCase));

            if (!scheduledForRemoval && !claimedByAnotherMod
                && IsAlreadyDeployed(allMods, relativeTarget, provider, deploymentRoot))
            {
                files.Add(new PlannedFile(relativeTarget, provider.SourcePath, provider.ModId, safety, "Unchanged; already deployed."));
                continue;
            }

            var directory = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(directory) && !directories.ContainsKey(directory))
            {
                directories[directory] = new CreateDirectoryOperation
                {
                    TargetPath = directory,
                    RemovableWhenEmpty = true,
                };
            }

            places.Add(new PlaceFileOperation
            {
                TargetPath = absolute,
                RelativeTarget = relativeTarget,
                SourcePath = provider.SourcePath,
                ModId = provider.ModId,
                Method = method,
            });

            var fileNote = safety == PathSafetyVerdict.Overwrite
                ? "Replaces an existing file; the original will be backed up."
                : "New file.";
            files.Add(new PlannedFile(relativeTarget, provider.SourcePath, provider.ModId, safety, fileNote));
        }

        // Ordering is fixed so the plan is reproducible and so the applier has a defined sequence:
        // removals first (frees paths that a rename-over would otherwise have to handle), then
        // directories, then placements.
        var operations = new List<FileOperation>(removes.Count + directories.Count + places.Count);
        operations.AddRange(removes);
        operations.AddRange(directories.Values);
        operations.AddRange(places);

        files.Sort((left, right) => string.CompareOrdinal(left.RelativeTarget, right.RelativeTarget));

        return new DeploymentPlan
        {
            GameId = definition.Id,
            ProfileId = profile.Id,
            DeploymentRoot = deploymentRoot,
            Files = files,
            Operations = operations,
            Conflicts = conflicts,
            Warnings = warnings,
            EnabledModsInOrder = enabled.Select(m => m.Metadata.Id).ToList(),
            PlanHash = ComputePlanHash(definition.Id, profile.Id, deploymentRoot, files),
        };
    }

    /// <summary>True when a manifest already records this mod as providing this path.</summary>
    /// <param name="mods">Mods with manifests.</param>
    /// <param name="relativeTarget">Target path.</param>
    /// <param name="provider">Intended provider.</param>
    /// <param name="deploymentRoot">Absolute deployment root, for the on-disk existence check.</param>
    private static bool IsAlreadyDeployed(
        IReadOnlyList<ModSetEntry> mods,
        string relativeTarget,
        ProviderEntry provider,
        string deploymentRoot)
    {
        foreach (var mod in mods)
        {
            if (!string.Equals(mod.Metadata.Id, provider.ModId, StringComparison.Ordinal)
                || mod.Manifest is not { } manifest)
            {
                continue;
            }

            foreach (var deployed in manifest.Files)
            {
                if (string.Equals(deployed.RelativeTarget, relativeTarget, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(deployed.SourcePath, provider.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    return FileStillMatchesRecord(deploymentRoot, deployed);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when the file on disk still looks exactly like the row we recorded for it.
    /// </summary>
    /// <param name="deploymentRoot">Deployment root.</param>
    /// <param name="deployed">Recorded row.</param>
    /// <remarks>
    /// Existence alone is not enough: when another mod currently supplies the bytes (or the original
    /// was restored), the file exists but its content is not ours — treating that as "already
    /// deployed" would silently skip the placement and leave the wrong bytes in the game folder.
    /// Size and last-write time are recorded at deploy time, so a mismatch on either means the file
    /// has been touched since; a row without a recorded time falls back to existence only.
    /// </remarks>
    private static bool FileStillMatchesRecord(string deploymentRoot, DeployedFile deployed)
    {
        var absolute = SafeCombine(deploymentRoot, deployed.RelativeTarget);
        if (!FileSystemOps.ExistsNoFollow(absolute))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(FileSystemOps.ToExtendedPath(absolute));

            if (deployed.Size > 0 && info.Length != deployed.Size)
            {
                return false;
            }

            if (deployed.LastWriteTimeUtc is { } recorded)
            {
                var drift = Math.Abs((info.LastWriteTimeUtc - recorded.UtcDateTime).TotalSeconds);
                if (drift > 2)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Classifies one planned path through the safety gate.</summary>
    /// <param name="gameId">Game id.</param>
    /// <param name="deploymentRoot">Deployment root.</param>
    /// <param name="relativeTarget">Target path relative to the root.</param>
    /// <param name="note">Explanation.</param>
    /// <param name="absolute">Resolved absolute path.</param>
    private PathSafetyVerdict ClassifyPath(
        string gameId,
        string deploymentRoot,
        string relativeTarget,
        out string note,
        out string absolute)
    {
        absolute = SafeCombine(deploymentRoot, relativeTarget);

        var result = _pathSafety.ValidateWrite(gameId, absolute);
        if (!result.Allowed)
        {
            // A refusal is worth a log line even though the caller also surfaces it: when a plan comes
            // back with a path missing, the reason needs to be findable after the UI has moved on.
            _logger?.Warn("Planner", $"拒绝 “{relativeTarget}”（位于 “{deploymentRoot}”）：{result.Reason}");
            note = result.Reason;
            return PathSafetyVerdict.Unsafe;
        }

        note = result.Reason;
        return result.Verdict;
    }

    /// <summary>
    /// Combines a root and a forward-slash relative path, refusing a path that would escape even
    /// lexically.
    /// </summary>
    /// <param name="root">Absolute root.</param>
    /// <param name="relative">Forward-slash relative path.</param>
    /// <remarks>
    /// Deliberately does not use <see cref="Path.Combine(string,string)"/> for the whole path,
    /// because <c>Combine</c> silently discards the root when the second argument is rooted. Joining
    /// segment by segment makes a rooted or traversing segment impossible to smuggle in.
    /// </remarks>
    public static string SafeCombine(string root, string relative)
    {
        var combined = root;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            combined = Path.Combine(combined, segment);
        }

        return combined;
    }

    /// <summary>Resolves the absolute deployment root for a game and install path.</summary>
    /// <param name="definition">Game definition.</param>
    /// <param name="installPath">Absolute install directory.</param>
    public static string ResolveDeploymentRoot(GameDefinition definition, string installPath)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var baseDirectory = definition.ModRootInUserProfile
            ? ResolveUserProfileBase()
            : installPath;

        // A loader-only definition (for example a game that ships BepInEx but has no generic Mods
        // folder) still has a well-defined destination: the loader's own plugin directory. Falling
        // back to the install root in that case would deploy plugins where the loader never scans.
        var modRoot = definition.ModRoot;
        if (string.IsNullOrWhiteSpace(modRoot) && definition.Loader is { } loader)
        {
            modRoot = loader.PluginsDirectory;
        }

        if (string.IsNullOrWhiteSpace(modRoot))
        {
            return Path.GetFullPath(baseDirectory);
        }

        // A user-chosen mod folder for a generic game can live anywhere safe, so an absolute root is used as
        // given. Path safety still decides whether anything may be written there: this method answers "where",
        // never "may I".
        if (Path.IsPathRooted(modRoot))
        {
            return Path.GetFullPath(modRoot);
        }

        var combined = baseDirectory;
        foreach (var segment in modRoot.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            combined = Path.Combine(combined, segment);
        }

        return Path.GetFullPath(combined);
    }

    /// <summary>
    /// The user-profile base for a game whose mods live in Documents.
    /// </summary>
    /// <remarks>
    /// Modrix goes further here and additionally maps into the Proton prefix on Linux, with the rule
    /// that a non-install base "must already exist — never fabricate a prefix". We keep the
    /// exists-check discipline but only implement the Windows layout, and say so rather than
    /// pretending to support Proton.
    /// </remarks>
    private static string ResolveUserProfileBase() =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>Preferred first-choice deployment method for a definition.</summary>
    /// <param name="definition">Game definition.</param>
    public static DeploymentMethod PreferredMethod(GameDefinition definition) =>
        definition.DeploymentPreference.Count > 0
            ? definition.DeploymentPreference[0]
            : DeploymentMethod.Copy;

    /// <summary>
    /// Deterministic fingerprint of the plan's inputs and outputs.
    /// </summary>
    /// <param name="gameId">Game id.</param>
    /// <param name="profileId">Profile id.</param>
    /// <param name="deploymentRoot">Deployment root.</param>
    /// <param name="files">Planned files.</param>
    /// <remarks>
    /// Used for two things: as the idempotence key ("is a redeploy a no-op?") and as the journal's
    /// record of which intent a transaction belongs to, so recovery can tell whether it is repairing
    /// the current intent or a superseded one.
    /// </remarks>
    public static string ComputePlanHash(
        string gameId,
        string profileId,
        string deploymentRoot,
        IReadOnlyList<PlannedFile> files)
    {
        var builder = new StringBuilder()
            .Append(gameId).Append('\n')
            .Append(profileId).Append('\n')
            .Append(deploymentRoot.ToLowerInvariant()).Append('\n');

        foreach (var file in files.OrderBy(f => f.RelativeTarget, StringComparer.Ordinal))
        {
            builder.Append(file.RelativeTarget.ToLowerInvariant())
                .Append('|')
                .Append(file.ModId)
                .Append('|')
                .Append((file.SourcePath ?? string.Empty).ToLowerInvariant())
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    /// <summary>One mod's claim on one target path.</summary>
    /// <param name="ModId">Claimant.</param>
    /// <param name="SourcePath">Absolute staged source.</param>
    private sealed record ProviderEntry(string ModId, string SourcePath);

    /// <summary>
    /// The resolution outcome: the winning provider per target, plus every contender per target so
    /// shadowed mods can be named in the conflict report.
    /// </summary>
    /// <param name="Desired">Winning provider per target path.</param>
    /// <param name="Contenders">All providers per target path, in resolution order.</param>
    private sealed record ResolutionResult(
        SortedDictionary<string, ProviderEntry> Desired,
        SortedDictionary<string, List<ProviderEntry>> Contenders);
}
