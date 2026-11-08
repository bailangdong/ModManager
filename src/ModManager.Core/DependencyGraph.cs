namespace ModManager.Core;

/// <summary>How one node of the dependency graph turned out.</summary>
public enum DependencyNodeState
{
    /// <summary>Installed and satisfying the declared version.</summary>
    Installed = 0,

    /// <summary>Installed, but older than the declared minimum version.</summary>
    InstalledOutdated = 1,

    /// <summary>Not installed at all.</summary>
    Missing = 2,

    /// <summary>Not installed, but declared optional — worth showing, not a problem.</summary>
    OptionalMissing = 3,

    /// <summary>Declared in a form that cannot be evaluated automatically.</summary>
    Unsupported = 4,

    /// <summary>The dependency names itself, which would never resolve.</summary>
    SelfReference = 5,

    /// <summary>A cycle was detected; the branch stops here instead of recursing forever.</summary>
    Cyclic = 6,

    /// <summary>Installed and satisfying the requirement.</summary>
    Satisfied = 7,
}

/// <summary>
/// One node of a resolved dependency tree.
/// </summary>
/// <param name="Name">Display name of the dependency.</param>
/// <param name="LocalModId">Installed mod id, when the dependency resolves to one of ours.</param>
/// <param name="NexusModId">Nexus mod id, when the declaration named one.</param>
/// <param name="State">How the node resolved.</param>
/// <param name="RequiredVersion">Minimum version the declaration asked for.</param>
/// <param name="InstalledVersion">Version actually installed.</param>
/// <param name="Note">Explanation shown next to the node.</param>
/// <param name="Children">Further dependencies of this node.</param>
public sealed record DependencyNode(
    string Name,
    string? LocalModId,
    int? NexusModId,
    DependencyNodeState State,
    string? RequiredVersion,
    string? InstalledVersion,
    string? Note,
    IReadOnlyList<DependencyNode> Children)
{
    /// <summary>True when this node stops the mod from working.</summary>
    public bool IsBlocking => State is DependencyNodeState.Missing or DependencyNodeState.InstalledOutdated;

    /// <summary>True when the node can be acted on by installing something.</summary>
    public bool NeedsInstall => State is DependencyNodeState.Missing;

    /// <summary>Total nodes below this one, so the interface can say how deep a branch goes.</summary>
    public int DescendantCount => Children.Sum(child => 1 + child.DescendantCount);
}

/// <summary>The resolved dependency tree for one mod.</summary>
/// <param name="RootName">Mod the tree was resolved for.</param>
/// <param name="Roots">Top-level dependencies.</param>
public sealed record DependencyReport(string RootName, IReadOnlyList<DependencyNode> Roots)
{
    /// <summary>Total nodes in the tree, including nested ones.</summary>
    public int TotalCount => Count(Roots);

    /// <summary>Missing dependencies that are not optional.</summary>
    public int MissingCount => Count(Roots, DependencyNodeState.Missing);

    /// <summary>Dependencies installed at too low a version.</summary>
    public int OutdatedCount => Count(Roots, DependencyNodeState.InstalledOutdated);

    /// <summary>Declarations that could not be evaluated.</summary>
    public int UnsupportedCount => Count(Roots, DependencyNodeState.Unsupported);

    /// <summary>True when anything blocks the mod from working.</summary>
    public bool HasBlocking => MissingCount > 0 || OutdatedCount > 0;

    /// <summary>One-line summary for the interface.</summary>
    public string Summary => TotalCount switch
    {
        0 => "无需依赖。",
        _ when !HasBlocking => $"依赖已满足（{TotalCount} 项）。",
        _ => $"缺少 {MissingCount} 项依赖，{OutdatedCount} 项版本过低"
             + (UnsupportedCount > 0 ? $"，{UnsupportedCount} 项无法自动解析" : "。"),
    };

    /// <summary>Counts nodes matching a state.</summary>
    /// <param name="nodes">Nodes to walk.</param>
    /// <param name="state">State to count.</param>
    private static int Count(IReadOnlyList<DependencyNode> nodes, DependencyNodeState state)
    {
        var total = 0;
        foreach (var node in nodes)
        {
            if (node.State == state)
            {
                total++;
            }

            total += Count(node.Children, state);
        }

        return total;
    }

    /// <summary>Counts every node.</summary>
    /// <param name="nodes">Nodes to walk.</param>
    private static int Count(IReadOnlyList<DependencyNode> nodes)
    {
        var total = 0;
        foreach (var node in nodes)
        {
            total += 1 + Count(node.Children);
        }

        return total;
    }
}
