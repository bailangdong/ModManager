using ModManager.Core.Safety;

namespace ModManager.Core.Services;

/// <summary>
/// Reports what a mod needs that is not installed.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — the user's spec §15, which is specific about the missing-loader case: detect
/// whether the loader is installed and, if it is missing, show <i>Missing Dependency</i> rather than
/// "直接把 Mod 塞进游戏目录" — jamming the mod into the game folder anyway. That distinction is worth
/// honouring precisely because a mod deployed without its loader fails silently: the game starts, nothing
/// happens, and there is no error to search for. That is the worst possible failure mode, so it is
/// surfaced as a blocking issue instead.
/// </para>
/// <para>
/// Nexus requirements come through the provider as a graph of mod ids, but resolving that graph
/// automatically requires the user to have approved each download — so this service reports what is
/// missing rather than fetching it. Silently installing three more mods because one asked for them is not
/// a thing a mod manager should do on its own.
/// </para>
/// </remarks>
public sealed class DependencyService : IDependencyService
{
    /// <inheritdoc />
    public IReadOnlyList<MissingDependency> FindMissing(
        IGameAdapter adapter,
        string installPath,
        IReadOnlyList<ModMetadata> mods)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(mods);

        var missing = new List<MissingDependency>();

        // A missing mod loader is a blocker, and it is reported once for the game rather than per mod, so
        // the user sees one actionable item instead of twenty copies of the same one.
        var loader = adapter.CheckLoader(installPath);
        if (loader.IsBlocking)
        {
            missing.Add(new MissingDependency(
                "__game__",
                $"Mod loader not installed: {adapter.Definition.Loader?.DisplayName ?? "required loader"}",
                IsLoader: true,
                adapter.Definition.Loader?.DownloadUrl));
        }

        var installedIds = new HashSet<string>(mods.Select(mod => mod.Id), StringComparer.Ordinal);
        var installedNames = new HashSet<string>(
            mods.Select(mod => mod.Name).Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            foreach (var dependency in mod.Dependencies)
            {
                if (dependency.Length == 0)
                {
                    continue;
                }

                // A dependency may be recorded as a mod id or as a published name, because the two
                // sources differ: Nexus gives names, our own records give ids.
                if (installedIds.Contains(dependency) || installedNames.Contains(dependency))
                {
                    continue;
                }

                missing.Add(new MissingDependency(
                    mod.Id,
                    dependency,
                    IsLoader: false,
                    ActionUrl: mod.SourceUrl));
            }

            // Requirements the source page stated in prose cannot be resolved automatically, but they are
            // still worth showing: they are usually the real reason a mod does not work.
            foreach (var requirement in mod.Requirements)
            {
                if (requirement.Length == 0
                    || installedIds.Contains(requirement)
                    || installedNames.Contains(requirement))
                {
                    continue;
                }

                missing.Add(new MissingDependency(mod.Id, requirement, IsLoader: false, ActionUrl: mod.SourceUrl));
            }
        }

        return missing;
    }

    /// <inheritdoc />
    public IReadOnlyList<HealthIssue> FindIncompatibilities(IReadOnlyList<ModMetadata> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var issues = new List<HealthIssue>();
        var byId = mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mod in mods)
        {
            foreach (var incompatible in mod.Incompatibilities)
            {
                if (!byId.TryGetValue(incompatible, out var other))
                {
                    // An incompatibility naming a mod that is not installed is not a problem.
                    continue;
                }

                // Report each pair once, whichever side declared it.
                var key = string.CompareOrdinal(mod.Id, other.Id) < 0
                    ? $"{mod.Id}|{other.Id}"
                    : $"{other.Id}|{mod.Id}";

                if (!reported.Add(key))
                {
                    continue;
                }

                issues.Add(new HealthIssue(
                    IssueSeverity.Warning,
                    $"“{mod.Name}” 与 “{other.Name}” 被声明为互不兼容",
                    "Mod 作者声明这两个 Mod 不能一起使用。",
                    "禁用其中一个，或到 Mod 页面查看是否有兼容补丁。",
                    RelatedModId: mod.Id,
                    IsBlocking: false));
            }
        }

        return issues;
    }
}

/// <summary>
/// Aggregates everything wrong with a game's mod setup into one actionable list.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix's <c>health.rs</c> and its "deploy blockers" concept: a health problem
/// marked blocking stops a deployment before the applier runs at all. That ordering matters, because
/// discovering a missing loader <i>after</i> writing two hundred files into a game folder is a poor
/// experience even when it is recoverable.
/// </remarks>
public sealed class HealthService : IHealthService
{
    private readonly IDependencyService _dependencies;
    private readonly IVerifyService _verify;
    private readonly IModRepository _mods;

    /// <summary>Creates the health service.</summary>
    /// <param name="dependencies">Dependency checker.</param>
    /// <param name="verify">Verification service.</param>
    /// <param name="mods">Mod repository, for manifests.</param>
    public HealthService(IDependencyService dependencies, IVerifyService verify, IModRepository mods)
    {
        _dependencies = dependencies;
        _verify = verify;
        _mods = mods;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HealthIssue>> CheckAsync(
        IGameAdapter adapter,
        GameInstance instance,
        IReadOnlyList<ModMetadata> mods,
        IReadOnlyList<FileConflict> conflicts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(conflicts);

        var issues = new List<HealthIssue>();

        // 1. The game's own checks: loader presence, directory existence, and anything the adapter adds.
        issues.AddRange(adapter.RunHealthChecks(instance.InstallPath));

        // 2. Missing dependencies.
        foreach (var missing in _dependencies.FindMissing(adapter, instance.InstallPath, mods))
        {
            issues.Add(new HealthIssue(
                missing.IsLoader ? IssueSeverity.Error : IssueSeverity.Warning,
                missing.IsLoader ? missing.Requirement : $"缺少前置：{missing.Requirement}",
                missing.IsLoader
                    ? "已安装的 Mod 在没有它的情况下无法加载。"
                    : $"“{NameOf(mods, missing.ModId)}” 需要的内容尚未安装。",
                missing.ActionUrl is { Length: > 0 } url ? $"可从 {url} 获取" : "请安装缺失的前置内容。",
                missing.ModId == "__game__" ? null : missing.ModId,
                IsBlocking: missing.IsLoader));
        }

        // 3. Declared incompatibilities.
        issues.AddRange(_dependencies.FindIncompatibilities(mods));

        // 4. Unresolved conflicts. Not blocking: a conflict resolved by load order is normal and expected,
        //    and blocking on it would make the common case refuse to deploy.
        var unresolved = conflicts.Count(conflict => conflict.Resolution == ConflictResolutionKind.Undecided);
        if (unresolved > 0)
        {
            issues.Add(new HealthIssue(
                IssueSeverity.Info,
                $"{unresolved} 个文件冲突由加载顺序决定",
                "有多个 Mod 提供同一个文件，加载顺序靠后的 Mod 生效。",
                "打开“冲突”页查看生效的 Mod，或为它指定一条规则。",
                RelatedModId: null,
                IsBlocking: false));
        }

        // 5. File drift: files changed since they were deployed. Reported because it explains otherwise
        //    baffling behaviour — a mod that appears installed but has no effect.
        var manifests = _mods.ManifestsForGame(adapter.GameId);
        if (manifests.Count > 0)
        {
            var report = await _verify
                .VerifyGameAsync(adapter.GameId, manifests, instance.InstallPath, cancellationToken)
                .ConfigureAwait(false);

            if (report.Modified > 0)
            {
                issues.Add(new HealthIssue(
                    IssueSeverity.Warning,
                    $"{report.Modified} 个已部署文件在安装后被改动",
                    "这些文件被本程序之外的东西修改过，通常是其他工具或你自己的编辑。",
                    "可在 Mod 详情里查看文件列表。卸载时会保留被改动的文件，不会强行删除。",
                    RelatedModId: null,
                    IsBlocking: false));
            }

            if (report.Missing > 0)
            {
                issues.Add(new HealthIssue(
                    IssueSeverity.Warning,
                    $"{report.Missing} 个已部署文件缺失",
                    "本程序部署的文件已不在原位，可能是游戏更新或清理工具删除了它们。",
                    "重新部署即可恢复。",
                    RelatedModId: null,
                    IsBlocking: false));
            }
        }

        return issues;
    }

    /// <summary>Looks up a mod's display name, falling back to its id.</summary>
    /// <param name="mods">Mods to search.</param>
    /// <param name="modId">Mod id.</param>
    private static string NameOf(IReadOnlyList<ModMetadata> mods, string modId) =>
        mods.FirstOrDefault(mod => string.Equals(mod.Id, modId, StringComparison.Ordinal))?.Name ?? modId;
}

/// <summary>
/// Finds hand-installed content sitting in a game folder.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix's <c>external.rs</c> and its <c>[[external_scan]]</c> rules. The purpose is
/// honesty: a mod manager that only lists what it deployed gives a misleading picture of a game folder
/// that already had mods in it, and the user's own hand-installed mods are exactly the ones most likely to
/// conflict with what the manager adds.
/// <para>
/// These files are reported and never managed. Adopting them automatically would mean taking ownership of
/// files whose provenance is unknown, which is how a manager ends up deleting something it never installed.
/// </para>
/// </remarks>
public sealed class ExternalContentService : IExternalContentService
{
    /// <summary>Maximum entries reported, so a large game folder cannot flood the UI.</summary>
    public const int MaxResults = 5_000;

    /// <summary>Maximum directory depth walked.</summary>
    public const int MaxDepth = 4;

    /// <inheritdoc />
    public Task<IReadOnlyList<ExternalContent>> ScanAsync(
        IGameAdapter adapter,
        string installPath,
        IReadOnlyList<ModManifest> manifests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(manifests);

        var results = new List<ExternalContent>();
        var definition = adapter.Definition;

        if (definition.ExternalScans.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ExternalContent>>(results);
        }

        // Everything this manager deployed, so it is excluded and only genuinely unmanaged content is
        // reported.
        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deploymentRoot = DeploymentPlanner.ResolveDeploymentRoot(definition, installPath);

        foreach (var manifest in manifests)
        {
            foreach (var file in manifest.Files)
            {
                managed.Add(file.RelativeTarget.Replace('\\', '/'));
            }
        }

        var baseFiles = new HashSet<string>(definition.BaseFiles, StringComparer.OrdinalIgnoreCase);

        foreach (var scan in definition.ExternalScans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (results.Count >= MaxResults)
            {
                break;
            }

            var scanRoot = deploymentRoot;
            foreach (var segment in scan.Directory.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                scanRoot = Path.Combine(scanRoot, segment);
            }

            if (!Directory.Exists(FileSystemOps.ToExtendedPath(scanRoot)))
            {
                continue;
            }

            ScanRoot(
                scan,
                scanRoot,
                deploymentRoot,
                managed,
                baseFiles,
                definition.Id,
                results,
                cancellationToken);
        }

        var ordered = results
            .OrderBy(entry => entry.IsBaseGameFile)
            .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .ToList();

        return Task.FromResult<IReadOnlyList<ExternalContent>>(ordered);
    }

    /// <summary>Scans one configured root.</summary>
    /// <param name="scan">Scan rule.</param>
    /// <param name="scanRoot">Absolute directory to scan.</param>
    /// <param name="deploymentRoot">Deployment root, for relative paths.</param>
    /// <param name="managed">Relative paths this manager deployed.</param>
    /// <param name="baseFiles">Vanilla file names.</param>
    /// <param name="gameId">Game id.</param>
    /// <param name="results">Results sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static void ScanRoot(
        ExternalScanDefinition scan,
        string scanRoot,
        string deploymentRoot,
        IReadOnlySet<string> managed,
        IReadOnlySet<string> baseFiles,
        string gameId,
        List<ExternalContent> results,
        CancellationToken cancellationToken)
    {
        var isFolderScan = string.Equals(scan.Kind, "folder", StringComparison.OrdinalIgnoreCase);

        if (isFolderScan)
        {
            foreach (var directory in EnumerateDirectoriesBounded(scanRoot, deploymentRoot, cancellationToken))
            {
                if (results.Count >= MaxResults)
                {
                    return;
                }

                var relative = Path.GetRelativePath(deploymentRoot, directory).Replace('\\', '/');

                // The scan root itself is the deploy root for most games; reporting it would be noise.
                if (relative.Length == 0 || relative == ".")
                {
                    continue;
                }

                if (IsManaged(managed, relative) || IsBaseFile(baseFiles, relative, scan.SkipBaseFiles))
                {
                    continue;
                }

                results.Add(new ExternalContent(
                    gameId,
                    relative,
                    IsDirectory: true,
                    scan.Label,
                    TryDirectorySize(directory),
                    IsBaseGameFile: false));
            }

            return;
        }

        foreach (var file in EnumerateFilesBounded(scanRoot, deploymentRoot, cancellationToken))
        {
            if (results.Count >= MaxResults)
            {
                return;
            }

            var relative = Path.GetRelativePath(deploymentRoot, file).Replace('\\', '/');
            var fileName = Path.GetFileName(relative);
            var extension = Path.GetExtension(fileName).TrimStart('.');

            if (scan.Extensions.Count > 0
                && !scan.Extensions.Any(ext => string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (IsManaged(managed, relative))
            {
                continue;
            }

            // A vanilla file is listed but flagged rather than hidden: the user asked to see what is in the
            // folder, and "this is a game file, not a mod" is a useful answer.
            var isBase = IsBaseFile(baseFiles, fileName, scan.SkipBaseFiles);
            if (isBase && scan.SkipBaseFiles)
            {
                continue;
            }

            results.Add(new ExternalContent(
                gameId,
                relative,
                IsDirectory: false,
                scan.Label,
                TryFileSize(file),
                isBase));
        }
    }

    /// <summary>Enumerates directories breadth-first, bounded by depth and count.</summary>
    /// <param name="root">Directory to walk.</param>
    /// <param name="stopRoot">Root never to walk past.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// An explicit work list with a depth bound rather than recursion, matching the discipline Modrix
    /// applies to untrusted input. A game folder is not hostile, but it can contain a junction loop, and
    /// recursion over that would not terminate.
    /// </remarks>
    private static IEnumerable<string> EnumerateDirectoriesBounded(
        string root,
        string stopRoot,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;

        while (queue.Count > 0 && visited < MaxResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (current, depth) = queue.Dequeue();
            visited++;

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(FileSystemOps.ToExtendedPath(current));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                // A reparse point is never descended into: it may point outside the game folder entirely.
                if (FileSystemOps.IsReparsePoint(child))
                {
                    continue;
                }

                if (!IsInside(child, stopRoot))
                {
                    continue;
                }

                if (depth > 0)
                {
                    yield return child;
                }

                if (depth + 1 <= MaxDepth)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }
    }

    /// <summary>Enumerates files one level deep, bounded by count.</summary>
    /// <param name="root">Directory to scan.</param>
    /// <param name="stopRoot">Root never to walk past.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static IEnumerable<string> EnumerateFilesBounded(
        string root,
        string stopRoot,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var count = 0;

        while (queue.Count > 0 && count < MaxResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (current, depth) = queue.Dequeue();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(FileSystemOps.ToExtendedPath(current));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                count++;
                if (count > MaxResults)
                {
                    yield break;
                }

                yield return file;
            }

            if (depth + 1 > MaxDepth)
            {
                continue;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(FileSystemOps.ToExtendedPath(current));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in directories)
            {
                if (!FileSystemOps.IsReparsePoint(child) && IsInside(child, stopRoot))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }
    }

    /// <summary>True when this manager deployed the path or something under it.</summary>
    /// <param name="managed">Deployed relative paths.</param>
    /// <param name="relative">Relative path to test.</param>
    private static bool IsManaged(IReadOnlySet<string> managed, string relative) =>
        managed.Contains(relative) || managed.Any(path => path.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a name matches the game's vanilla file list.</summary>
    /// <param name="baseFiles">Vanilla file patterns.</param>
    /// <param name="name">File or directory name.</param>
    /// <param name="skipBase">Whether matching is being used to skip.</param>
    /// <remarks>
    /// A trailing <c>*</c> means prefix match, exactly as Modrix specifies for entries like <c>cc*</c>
    /// covering Creation Club content.
    /// </remarks>
    private static bool IsBaseFile(IReadOnlySet<string> baseFiles, string name, bool skipBase)
    {
        _ = skipBase;

        foreach (var pattern in baseFiles)
        {
            if (pattern.EndsWith('*'))
            {
                if (name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when a path lies inside a root.</summary>
    /// <param name="candidate">Path to test.</param>
    /// <param name="root">Containing root.</param>
    private static bool IsInside(string candidate, string root) =>
        PathSafetyService.IsInsideOrEqual(candidate, root);

    /// <summary>Reads a file size, tolerating failure.</summary>
    /// <param name="path">File path.</param>
    private static long TryFileSize(string path)
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

    /// <summary>Reads a directory's total size, bounded.</summary>
    /// <param name="path">Directory path.</param>
    private static long TryDirectorySize(string path)
    {
        try
        {
            var total = 0L;
            var count = 0;

            foreach (var file in Directory.EnumerateFiles(FileSystemOps.ToExtendedPath(path), "*", SearchOption.AllDirectories))
            {
                if (++count > 20_000)
                {
                    break;
                }

                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // A file that vanished mid-walk simply does not contribute.
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
