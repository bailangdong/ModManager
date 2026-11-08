namespace ModManager.Core;

/// <summary>Where a declared package dependency was found.</summary>
public enum PackageDependencyState
{
    /// <summary>The dependency is already deployed for this game.</summary>
    Installed = 0,

    /// <summary>The dependency is in the local package library but not deployed.</summary>
    InLibrary = 1,

    /// <summary>Neither installed nor in the library. The user must acquire it explicitly.</summary>
    Missing = 2,

    /// <summary>An installed/library package exists but does not satisfy the declared version.</summary>
    Mismatch = 3,
}

/// <summary>How strongly the package declared a dependency.</summary>
public enum PackageDependencyKind
{
    /// <summary>A normal dependency; a missing one blocks installation.</summary>
    Required = 0,

    /// <summary>Declared with a leading '?' or “(optional)”; missing is allowed.</summary>
    Optional = 1,

    /// <summary>Recommended but not required.</summary>
    Recommended = 2,

    /// <summary>The declaration could not be understood; never auto-resolved.</summary>
    Unknown = 3,
}

/// <summary>One dependency declaration and what the local state can satisfy it with.</summary>
/// <param name="Declaration">The raw declaration from the package metadata.</param>
/// <param name="State">Installed, in the library, or missing.</param>
/// <param name="MatchedModId">Installed mod id, when <paramref name="State"/> is Installed.</param>
/// <param name="MatchedPackageId">Library package id, when <paramref name="State"/> is InLibrary.</param>
/// <param name="MatchedVersion">Version of the match, when known.</param>
public sealed record PackageDependencyStatus(
    string Declaration,
    PackageDependencyState State,
    string? MatchedModId = null,
    string? MatchedPackageId = null,
    string? MatchedVersion = null)
{
    /// <summary>Required, optional, recommended, or unparsable.</summary>
    public PackageDependencyKind Kind { get; init; } = PackageDependencyKind.Required;

    /// <summary>Version check result when a match was found and a constraint was declared.</summary>
    public bool? VersionSatisfied { get; init; }
}

/// <summary>
/// Read-only dependency view for a library package. Deliberately does not fetch anything: the
/// product rule is that a missing dependency is shown and the user decides, never silently pulled.
/// </summary>
/// <param name="PackageId">Package the declarations belong to.</param>
/// <param name="Dependencies">One row per declaration.</param>
public sealed record PackageDependencyReport(
    string PackageId,
    IReadOnlyList<PackageDependencyStatus> Dependencies)
{
    /// <summary>Number of declarations with no installed or library match.</summary>
    public int MissingCount => Dependencies.Count(dependency =>
        dependency.State is PackageDependencyState.Missing or PackageDependencyState.Mismatch);

    /// <summary>Missing/mismatched dependencies that actually block installation.</summary>
    public int RequiredMissingCount => Dependencies.Count(dependency =>
        dependency.Kind == PackageDependencyKind.Required
        && dependency.State is PackageDependencyState.Missing or PackageDependencyState.Mismatch);

    /// <summary>True when at least one dependency still has to be acquired.</summary>
    public bool HasMissing => MissingCount > 0;

    /// <summary>True when installation must be blocked until required dependencies are resolved.</summary>
    public bool HasBlockingMissing => RequiredMissingCount > 0;
}
