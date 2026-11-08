namespace ModManager.Core.Services;

/// <summary>
/// Resolves a mod's dependency graph — direct and indirect — into a tree the interface can render.
/// </summary>
/// <remarks>
/// <para>
/// The existing <see cref="IDependencyService"/> answers "is anything missing?" for a whole game and is
/// what the health check uses. This resolver answers the different question the online browser needs:
/// "for <i>this</i> mod, what does its dependency tree look like, and which branches are broken?" — which
/// requires recursion, cycle detection and version comparison.
/// </para>
/// <para>
/// Two rules are deliberate. A dependency is matched by stable identity first (mod id, or Nexus mod id
/// for references that carry one) and only then by name; matching on names alone is what makes a manager
/// install the wrong mod. And the resolver never fetches anything: it reports what is missing so the user
/// decides, because silently pulling three more mods because one asked for them is not something a mod
/// manager should do on its own.
/// </para>
/// </remarks>
public sealed class DependencyResolver
{
    /// <summary>Depth limit, so a pathological graph cannot exhaust the stack.</summary>
    public const int MaxDepth = 12;

    /// <summary>Resolves the dependency tree of one installed mod.</summary>
    /// <param name="root">Mod to resolve for.</param>
    /// <param name="all">Every installed mod, used to satisfy dependencies.</param>
    public DependencyReport ResolveLocal(ModMetadata root, IReadOnlyList<ModMetadata> all)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(all);

        var index = BuildIndex(all);
        var declared = root.Dependencies.Concat(root.Requirements).ToList();

        // The root's own name seeds the branch so that "A needs B, B needs A" is recognised as a cycle
        // rather than being mistaken for a self-reference deeper down the tree.
        var nodes = ResolveNames(declared, index, root.Id, [root.Name], 0, expandLocal: true);

        return new DependencyReport(root.Name, nodes);
    }

    /// <summary>Resolves a set of declared requirement strings against what is installed.</summary>
    /// <param name="rootName">Name of the mod the requirements belong to.</param>
    /// <param name="requirements">Declared requirement strings.</param>
    /// <param name="installed">Installed mods.</param>
    public DependencyReport ResolveRequirements(
        string rootName,
        IEnumerable<string> requirements,
        IReadOnlyList<ModMetadata> installed)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(installed);

        var index = BuildIndex(installed);
        var nodes = ResolveNames(requirements.ToList(), index, null, [], 0, expandLocal: true);
        return new DependencyReport(rootName, nodes);
    }

    /// <summary>
    /// Resolves a Nexus mod's requirement references against what is installed.
    /// </summary>
    /// <param name="rootName">Nexus mod name.</param>
    /// <param name="references">Requirement references from the API.</param>
    /// <param name="installed">Installed mods.</param>
    /// <param name="expand">
    /// Optional callback returning the requirements of a referenced Nexus mod, used to follow the graph
    /// deeper. When null the tree is one level deep, which is what a browse view can afford.
    /// </param>
    public DependencyReport ResolveNexusReferences(
        string rootName,
        IReadOnlyList<NexusModReference> references,
        IReadOnlyList<ModMetadata> installed,
        Func<int, IReadOnlyList<NexusModReference>>? expand = null)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(installed);

        var index = BuildIndex(installed);
        var nodes = new List<DependencyNode>();

        foreach (var reference in references)
        {
            nodes.Add(BuildNexusNode(reference, index, [], 0, expand));
        }

        return new DependencyReport(rootName, nodes);
    }

    /// <summary>Resolves a list of declaration strings, recursing into installed dependencies.</summary>
    /// <param name="declarations">Requirement strings.</param>
    /// <param name="index">Installed mod index.</param>
    /// <param name="rootId">Root mod id, for self-reference detection.</param>
    /// <param name="path">Names already on the current branch, for cycle detection.</param>
    /// <param name="depth">Current depth.</param>
    /// <param name="expandLocal">True to follow an installed mod's own dependencies.</param>
    private static List<DependencyNode> ResolveNames(
        IReadOnlyList<string> declarations,
        ModIndex index,
        string? rootId,
        IReadOnlyList<string> path,
        int depth,
        bool expandLocal)
    {
        var nodes = new List<DependencyNode>();
        if (depth > MaxDepth)
        {
            return nodes;
        }

        foreach (var declaration in declarations)
        {
            if (string.IsNullOrWhiteSpace(declaration))
            {
                continue;
            }

            var requirement = VersionOrder.Parse(declaration);
            nodes.Add(BuildNode(requirement, index, rootId, path, depth, expandLocal));
        }

        return nodes;
    }

    /// <summary>Builds one node from a parsed declaration.</summary>
    /// <param name="requirement">Parsed declaration.</param>
    /// <param name="index">Installed mod index.</param>
    /// <param name="rootId">Root mod id.</param>
    /// <param name="path">Active branch names.</param>
    /// <param name="depth">Current depth.</param>
    /// <param name="expandLocal">True to recurse into an installed dependency.</param>
    private static DependencyNode BuildNode(
        VersionRequirement requirement,
        ModIndex index,
        string? rootId,
        IReadOnlyList<string> path,
        int depth,
        bool expandLocal)
    {
        if (!requirement.IsResolvable)
        {
            return new DependencyNode(
                requirement.Name,
                null,
                null,
                DependencyNodeState.Unsupported,
                requirement.MinimumVersion,
                null,
                requirement.UnsupportedReason ?? "无法自动解析，请在 Mod 页面确认。",
                []);
        }

        if (!index.TryFind(requirement.Name, out var installed))
        {
            return new DependencyNode(
                requirement.Name,
                null,
                null,
                requirement.Optional ? DependencyNodeState.OptionalMissing : DependencyNodeState.Missing,
                requirement.MinimumVersion,
                null,
                requirement.Optional ? "可选依赖，未安装。" : "缺失，需要安装。",
                []);
        }

        // A dependency that resolves back to the root at the top level is a self-reference; deeper down it
        // is a cycle, which the branch check below catches.
        if (rootId is not null && depth == 0 && string.Equals(installed.Id, rootId, StringComparison.Ordinal))
        {
            return new DependencyNode(
                installed.Name,
                installed.Id,
                installed.NexusModId,
                DependencyNodeState.SelfReference,
                requirement.MinimumVersion,
                installed.Version,
                "该 Mod 依赖自身，声明可能有误。",
                []);
        }

        if (path.Any(name => string.Equals(name, requirement.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return new DependencyNode(
                requirement.Name,
                installed.Id,
                installed.NexusModId,
                DependencyNodeState.Cyclic,
                requirement.MinimumVersion,
                installed.Version,
                "检测到循环依赖，已停止展开。",
                []);
        }

        var satisfied = requirement.IsSatisfiedBy(installed.Version);
        var state = satisfied ? DependencyNodeState.Satisfied : DependencyNodeState.InstalledOutdated;
        var note = satisfied
            ? "已安装。"
            : $"已安装 {installed.Version ?? "未知版本"}，需要 {requirement.MinimumVersion} 或更高。";

        var children = new List<DependencyNode>();
        if (expandLocal && satisfied && depth < MaxDepth)
        {
            var branch = path.Concat([requirement.Name]).ToList();
            var declared = installed.Dependencies.Concat(installed.Requirements).ToList();
            children = ResolveNames(declared, index, rootId, branch, depth + 1, expandLocal);
        }

        return new DependencyNode(
            installed.Name,
            installed.Id,
            installed.NexusModId,
            state,
            requirement.MinimumVersion,
            installed.Version,
            note,
            children);
    }

    /// <summary>Builds one node from a Nexus requirement reference.</summary>
    /// <param name="reference">Reference from the API.</param>
    /// <param name="index">Installed mod index.</param>
    /// <param name="path">Active branch names.</param>
    /// <param name="depth">Current depth.</param>
    /// <param name="expand">Optional recursion callback.</param>
    private static DependencyNode BuildNexusNode(
        NexusModReference reference,
        ModIndex index,
        IReadOnlyList<string> path,
        int depth,
        Func<int, IReadOnlyList<NexusModReference>>? expand)
    {
        if (path.Contains(reference.Name, StringComparer.OrdinalIgnoreCase))
        {
            return new DependencyNode(
                reference.Name,
                null,
                reference.ModId,
                DependencyNodeState.Cyclic,
                null,
                null,
                "检测到循环依赖，已停止展开。",
                []);
        }

        var installed = index.FindByNexusId(reference.ModId) ?? index.FindByName(reference.Name);

        // The requirements of a missing dependency are still expanded when the caller can supply them:
        // "this mod needs something that itself needs two more things" is exactly the situation a user
        // wants to see before starting, not after.
        var children = new List<DependencyNode>();
        if (expand is not null && depth < MaxDepth)
        {
            var branch = path.Concat([reference.Name]).ToList();
            foreach (var child in expand(reference.ModId))
            {
                children.Add(BuildNexusNode(child, index, branch, depth + 1, expand));
            }
        }

        if (installed is null)
        {
            return new DependencyNode(
                reference.Name,
                null,
                reference.ModId,
                DependencyNodeState.Missing,
                null,
                null,
                reference.Notes is { Length: > 0 } notes ? $"缺失。{notes}" : "缺失，需要安装。",
                children);
        }

        return new DependencyNode(
            installed.Name,
            installed.Id,
            reference.ModId,
            DependencyNodeState.Installed,
            null,
            installed.Version,
            "已安装。",
            children);
    }

    /// <summary>Builds a lookup that matches installed mods by id, name and Nexus id.</summary>
    /// <param name="mods">Installed mods.</param>
    private static ModIndex BuildIndex(IReadOnlyList<ModMetadata> mods) => new(mods);

    /// <summary>Lookup over installed mods, matching on stable identity first and names second.</summary>
    private sealed class ModIndex
    {
        private readonly IReadOnlyList<ModMetadata> _mods;

        /// <summary>Creates the index.</summary>
        /// <param name="mods">Installed mods.</param>
        public ModIndex(IReadOnlyList<ModMetadata> mods) => _mods = mods;

        /// <summary>Finds a mod by Nexus mod id.</summary>
        /// <param name="nexusModId">Nexus mod id.</param>
        public ModMetadata? FindByNexusId(int nexusModId) =>
            _mods.FirstOrDefault(mod => mod.NexusModId == nexusModId);

        /// <summary>Finds a mod by display name.</summary>
        /// <param name="name">Name to match.</param>
        public ModMetadata? FindByName(string name) =>
            _mods.FirstOrDefault(mod => string.Equals(mod.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Finds a mod by id first, then by name.</summary>
        /// <param name="key">Id or name.</param>
        /// <param name="found">Matching mod, when found.</param>
        public bool TryFind(string key, out ModMetadata found)
        {
            found = _mods.FirstOrDefault(mod => string.Equals(mod.Id, key, StringComparison.Ordinal))
                ?? _mods.FirstOrDefault(mod => string.Equals(mod.Name, key, StringComparison.OrdinalIgnoreCase))!;

            return found is not null;
        }
    }
}
