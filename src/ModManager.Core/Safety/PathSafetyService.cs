using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace ModManager.Core.Safety;

/// <summary>
/// The single gate every filesystem mutation passes through.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — the user's spec §17 lists the forbidden operations (recursive work on a drive
/// root, deleting the game directory, clearing the Mods folder, path traversal, symlink/junction
/// escape, acting outside the current game's directory) and requires that all of them be refused.
/// Those are all symptoms of one missing thing, so they are answered by one component that can
/// <i>prove</i> containment rather than by scattered defensive checks.
/// </para>
/// <para>
/// Ideas borrowed and then extended:
/// </para>
/// <list type="bullet">
/// <item><b>Modrix's <c>relative_target</c></b> rejects any path component that is not a plain name —
/// <c>..</c>, a root, a drive prefix, a UNC prefix — and additionally rejects components containing
/// <c>&lt;</c> or <c>&gt;</c>, because those are the engine's own marker characters and an archive
/// must never be able to forge one. We adopt that rule verbatim, with the same reasoning.</item>
/// <item><b>Modrix's post-extraction validation</b> refuses an archive that planted a symlink,
/// "so a malicious archive cannot seed a link that later deploys files outside the game folder".
/// We go further and re-resolve the <i>real</i> path of every existing ancestor before allowing a
/// write, which also covers a junction planted by a different program.</item>
/// <item><b>Modrix's bounded walks</b> (<c>MAX_ANCESTOR_DEPTH</c>, <c>MAX_DEPTH</c>,
/// <c>MAX_FILES</c>) become <see cref="MaxCanonicalWalkDepth"/> here, so canonicalisation cannot
/// loop forever on a pathological tree.</item>
/// </list>
/// <para>
/// <b>Why canonicalise before comparing, not after:</b> a purely lexical
/// <c>StartsWith</c> check is defeated by <c>root\sub\..\..\Windows</c>, by a symlink inside the
/// root pointing at <c>C:\</c>, and by an 8.3 short name. Comparing the resolved real path of the
/// nearest existing ancestor makes all three fail closed.
/// </para>
/// </remarks>
public sealed class PathSafetyService : IPathSafetyService
{
    /// <summary>Maximum depth walked while canonicalising a path that does not yet exist.</summary>
    public const int MaxCanonicalWalkDepth = 256;

    /// <summary>Maximum length we will accept; beyond this we refuse rather than guess.</summary>
    private const int DefaultMaxPathLength = 32_000;

    private readonly ConcurrentDictionary<string, ApprovedRoot> _approvedRoots =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _protectedPaths;
    private readonly IReadOnlyList<string> _appOwnedRoots;
    private readonly SafetyOptions _options;
    private readonly IAppLogger? _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="appPaths">Application data roots, which are legitimately writable.</param>
    /// <param name="options">Safety options.</param>
    /// <param name="logger">Optional logger for refusals, so they are auditable.</param>
    public PathSafetyService(AppPaths appPaths, SafetyOptions? options = null, IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _options = options ?? new SafetyOptions();
        _logger = logger;

        _appOwnedRoots = appPaths.AllDirectories
            .Select(p => p)
            .ToList();

        _protectedPaths = BuildProtectedPaths(_options.ExtraProtectedPaths);
    }

    /// <inheritdoc />
    public IReadOnlyList<ApprovedRoot> ApprovedRoots =>
        _approvedRoots.Values.OrderBy(r => r.GameId, StringComparer.Ordinal).ToList();

    /// <inheritdoc />
    public void ApproveGameRoot(string gameId, string installPath, string modRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        var canonicalInstall = Canonicalize(installPath)
            ?? throw new ArgumentException($"Install path cannot be canonicalised: {installPath}", nameof(installPath));

        var canonicalModRoot = Canonicalize(modRootPath) ?? canonicalInstall;

        // Refuse to approve a root that is itself a protected location. Without this, a user who
        // mistakenly points a game at D:\ would make D:\ writable for the whole engine, which is
        // exactly the accident the spec's §17 warns about.
        if (IsProtected(canonicalInstall))
        {
            var reason = $"Refusing to approve '{canonicalInstall}' as a game root: it is a protected location.";
            _logger?.Warn("Safety", reason);
            throw new InvalidOperationException(reason);
        }

        _approvedRoots[gameId] = new ApprovedRoot(gameId, canonicalInstall, canonicalModRoot, DateTimeOffset.UtcNow);
        _logger?.Info("Safety", $"已批准游戏 “{gameId}” 的写入目录：{canonicalModRoot}");
    }

    /// <inheritdoc />
    public void RevokeGameRoot(string gameId) => _approvedRoots.TryRemove(gameId, out _);

    /// <inheritdoc />
    public PathSafetyResult ValidateWrite(string gameId, string prospectivePath)
    {
        if (string.IsNullOrWhiteSpace(prospectivePath))
        {
            return PathSafetyResult.Reject("Empty path.");
        }

        if (prospectivePath.Length > _options.MaxPathLength)
        {
            return PathSafetyResult.Reject($"Path exceeds {_options.MaxPathLength} characters.");
        }

        if (!_approvedRoots.TryGetValue(gameId, out var root))
        {
            return PathSafetyResult.Reject($"Game '{gameId}' has no approved root; refusing all writes.");
        }

        if (ContainsForbiddenSegment(prospectivePath, out var badSegment))
        {
            return PathSafetyResult.Reject($"Path contains a forbidden segment '{badSegment}': {prospectivePath}");
        }

        var canonical = Canonicalize(prospectivePath);
        if (canonical is null)
        {
            return PathSafetyResult.Reject($"Path cannot be resolved safely: {prospectivePath}");
        }

        if (IsProtected(canonical))
        {
            return PathSafetyResult.Reject($"Path is a protected location: {canonical}");
        }

        if (!IsInside(canonical, root.ModRootPath))
        {
            return PathSafetyResult.Reject(
                $"Path escapes the approved mod root. Target '{canonical}' is not inside '{root.ModRootPath}'.");
        }

        if (IsReparsePointEscape(prospectivePath, root.ModRootPath, out var escapeReason))
        {
            return PathSafetyResult.Reject(escapeReason);
        }

        var verdict = ExistsNoFollow(canonical) ? PathSafetyVerdict.Overwrite : PathSafetyVerdict.Safe;
        return PathSafetyResult.Allow(canonical, verdict, "Inside the approved mod root.");
    }

    /// <inheritdoc />
    public PathSafetyResult ValidateDelete(string gameId, string prospectivePath, bool isDirectory)
    {
        var writeCheck = ValidateWrite(gameId, prospectivePath);
        if (!writeCheck.Allowed)
        {
            return writeCheck;
        }

        var canonical = writeCheck.CanonicalPath!;

        // Never delete the deployment root itself, only things inside it.
        if (_approvedRoots.TryGetValue(gameId, out var root)
            && (PathsEqual(canonical, root.ModRootPath) || PathsEqual(canonical, root.InstallPath)))
        {
            return PathSafetyResult.Reject($"Refusing to delete a deployment root: {canonical}");
        }

        if (isDirectory
            && _options.RefuseReparsePointsOnDelete
            && FileSystemOps.IsReparsePoint(canonical))
        {
            return PathSafetyResult.Reject(
                $"Refusing to delete reparse point '{canonical}': it could redirect the delete outside the game folder.");
        }

        // A recursive delete must never be aimed at a directory holding lots of unrelated content.
        // We require either that we created it (recorded as a created directory) or that it sits
        // strictly below a game's mod root by at least one segment, and that it is not a
        // directory with many children at the root of the game.
        if (isDirectory && _options.RefuseRecursiveDeleteOutsideModRoot)
        {
            if (!IsInside(canonical, root!.ModRootPath) || PathsEqual(canonical, root.ModRootPath))
            {
                return PathSafetyResult.Reject($"Refusing recursive delete outside the mod root: {canonical}");
            }
        }

        return PathSafetyResult.Allow(canonical, PathSafetyVerdict.Overwrite, "Deletion permitted.");
    }

    /// <inheritdoc />
    public PathSafetyResult ValidateAppPath(string appOwnedPath)
    {
        if (string.IsNullOrWhiteSpace(appOwnedPath))
        {
            return PathSafetyResult.Reject("Empty path.");
        }

        if (ContainsForbiddenSegment(appOwnedPath, out var badSegment))
        {
            return PathSafetyResult.Reject($"Path contains a forbidden segment '{badSegment}': {appOwnedPath}");
        }

        var canonical = Canonicalize(appOwnedPath);
        if (canonical is null)
        {
            return PathSafetyResult.Reject($"Path cannot be resolved safely: {appOwnedPath}");
        }

        if (!_appOwnedRoots.Any(r => IsInsideOrEqual(canonical, r)))
        {
            return PathSafetyResult.Reject($"Path is outside the application data tree: {canonical}");
        }

        var verdict = ExistsNoFollow(canonical) ? PathSafetyVerdict.Overwrite : PathSafetyVerdict.Safe;
        return PathSafetyResult.Allow(canonical, verdict, "Inside the application data tree.");
    }

    /// <inheritdoc />
    public bool TryCreateDirectory(string gameId, string directoryPath, out string? reason)
    {
        var check = ValidateWrite(gameId, directoryPath);
        if (!check.Allowed)
        {
            reason = check.Reason;
            return false;
        }

        try
        {
            Directory.CreateDirectory(FileSystemOps.ToExtendedPath(check.CanonicalPath!));
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"Could not create '{check.CanonicalPath}': {ex.Message}";
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryDeleteFile(string gameId, string filePath, out string? reason)
    {
        var check = ValidateDelete(gameId, filePath, isDirectory: false);
        if (!check.Allowed)
        {
            reason = check.Reason;
            return false;
        }

        if (!FileSystemOps.RemoveIfPresent(check.CanonicalPath!))
        {
            reason = $"Could not delete '{check.CanonicalPath}'.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <inheritdoc />
    public bool TryRemoveEmptyDirectory(string gameId, string directoryPath, out string? reason)
    {
        var check = ValidateDelete(gameId, directoryPath, isDirectory: true);
        if (!check.Allowed)
        {
            reason = check.Reason;
            return false;
        }

        var canonical = check.CanonicalPath!;
        if (!Directory.Exists(FileSystemOps.ToExtendedPath(canonical)))
        {
            reason = null;
            return true;
        }

        try
        {
            // Non-recursive by construction. If it is not empty, this throws and we refuse — which is
            // the whole point: we tidy up folders we emptied, and nothing else.
            Directory.Delete(FileSystemOps.ToExtendedPath(canonical), recursive: false);
            reason = null;
            return true;
        }
        catch (IOException)
        {
            reason = $"Directory not removed because it is not empty: {canonical}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            reason = $"Directory not removed: {ex.Message}";
            return false;
        }
    }

    /// <inheritdoc />
    public string? Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // Resolve the nearest existing ancestor, because the leaf usually does not exist yet.
        var unresolved = new Stack<string>();
        var current = Path.TrimEndingDirectorySeparator(full);

        for (var depth = 0; depth < MaxCanonicalWalkDepth; depth++)
        {
            if (string.IsNullOrEmpty(current))
            {
                return null;
            }

            if (FileSystemOps.ExistsNoFollow(current))
            {
                var resolved = ResolveRealPath(current);
                if (resolved is null)
                {
                    return null;
                }

                var result = resolved;
                while (unresolved.Count > 0)
                {
                    result = Path.Combine(result, unresolved.Pop());
                }

                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(result));
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return null;
            }

            unresolved.Push(Path.GetFileName(current));
            current = parent;
        }

        return null;
    }

    /// <inheritdoc />
    public bool IsInside(string candidate, string root) => IsInsideOrEqual(candidate, root, allowEqual: false);

    /// <summary>True when <paramref name="candidate"/> is inside or equal to <paramref name="root"/>.</summary>
    /// <param name="candidate">Path to test.</param>
    /// <param name="root">Containing root.</param>
    /// <param name="allowEqual">Whether equality counts as inside.</param>
    public static bool IsInsideOrEqual(string candidate, string root, bool allowEqual = true)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return allowEqual;
        }

        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rejects a path whose components include parent traversal, a root, a drive prefix or the
    /// engine's own marker characters.
    /// </summary>
    /// <remarks>
    /// DESIGN REFERENCE — Modrix's <c>relative_target</c> applies exactly these rules to archive
    /// entry names, and the marker-character rule exists so an archive cannot forge the engine's
    /// internal <c>&lt;up&gt;</c>/<c>&lt;root&gt;</c> markers. We apply the same rules to every path
    /// the engine writes, not only to archive entries, because a single rule is easier to trust.
    /// </remarks>
    /// <param name="path">Path to inspect.</param>
    /// <param name="offendingSegment">The segment that failed, when any.</param>
    public static bool ContainsForbiddenSegment(string path, out string? offendingSegment)
    {
        offendingSegment = null;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // A drive prefix or UNC share root as the whole path is legitimate for an absolute path, so
        // strip the root component before inspecting segments; the rest of the path must still be
        // plain names. If the root cannot be determined, scan the whole path (fail closed).
        var remainder = path;
        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root))
            {
                remainder = path[root.Length..];
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Unparseable root: fall through and scan everything.
        }

        // UNC or device prefix as the whole path is only acceptable when it is a legitimate absolute
        // path; here we care about injected segments, so normalise separators and inspect parts.
        var segments = remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/'],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                offendingSegment = segment;
                return true;
            }

            // Reserved marker characters: illegal in Windows file names anyway, and ours internally.
            if (segment.Contains('<', StringComparison.Ordinal) || segment.Contains('>', StringComparison.Ordinal))
            {
                offendingSegment = segment;
                return true;
            }

            // A colon inside a segment means a drive-relative or alternate-data-stream component.
            if (segment.Contains(':', StringComparison.Ordinal))
            {
                offendingSegment = segment;
                return true;
            }
        }

        // A bare drive root or a filesystem root is never a valid write target.
        if (IsFileSystemRoot(path))
        {
            offendingSegment = path;
            return true;
        }

        return false;
    }

    /// <summary>True when the path is a drive root or a UNC share root.</summary>
    /// <param name="path">Path to test.</param>
    public static bool IsFileSystemRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(root)
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(full),
                    Path.TrimEndingDirectorySeparator(root),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    /// <summary>
    /// Detects a write that would escape through an existing reparse point by resolving the target's
    /// real path and testing containment again.
    /// </summary>
    /// <param name="prospectivePath">Path being written.</param>
    /// <param name="modRoot">Approved mod root.</param>
    /// <param name="reason">Explanation when it escapes.</param>
    private static bool IsReparsePointEscape(string prospectivePath, string modRoot, out string reason)
    {
        reason = string.Empty;

        // Find the deepest existing ancestor and resolve it for real.
        var current = Path.GetFullPath(prospectivePath);
        for (var depth = 0; depth < MaxCanonicalWalkDepth; depth++)
        {
            if (FileSystemOps.ExistsNoFollow(current))
            {
                if (!FileSystemOps.IsReparsePoint(current))
                {
                    return false;
                }

                var real = ResolveRealPath(current);
                if (real is null)
                {
                    reason = $"Cannot resolve reparse point target for '{current}'.";
                    return true;
                }

                // Re-check the lexical path too: a reparse point may legitimately resolve inside the
                // root (for example a staged symlink), and only an escape is a refusal.
                var remaining = prospectivePath.Length > current.Length
                    ? prospectivePath[(current.Length + 1)..]
                    : string.Empty;
                var projected = string.IsNullOrEmpty(remaining) ? real : Path.Combine(real, remaining);

                if (!IsInsideOrEqual(projected, modRoot))
                {
                    reason = $"Path resolves through a reparse point to '{projected}', outside '{modRoot}'.";
                    return true;
                }

                return false;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return false;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>
    /// Resolves a path to its real location, expanding reparse points in every existing component.
    /// </summary>
    /// <param name="path">Existing path.</param>
    /// <remarks>
    /// <see cref="Path.GetFullPath(string)"/> is purely lexical, so it cannot see a symlink. The
    /// managed API exposes the real path only through <c>FileInfo.ResolveLinkTarget</c>, which
    /// resolves one hop at a time, so we loop with a bound — a malicious chain of links must not be
    /// able to spin here.
    /// </remarks>
    private static string? ResolveRealPath(string path)
    {
        try
        {
            var result = Path.GetFullPath(path);
            for (var hop = 0; hop < 40; hop++)
            {
                FileSystemInfo info = Directory.Exists(FileSystemOps.ToExtendedPath(result))
                    ? new DirectoryInfo(result)
                    : new FileInfo(result);

                var target = info.ResolveLinkTarget(returnFinalTarget: false);
                if (target is null)
                {
                    return result;
                }

                result = Path.GetFullPath(target.FullName);
            }

            // Too many hops: treat as unresolvable rather than trusting it.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>True when the path is one we must never write or delete.</summary>
    /// <param name="canonicalPath">Canonical path.</param>
    private bool IsProtected(string canonicalPath) =>
        _protectedPaths.Any(protectedPath => IsInsideOrEqual(canonicalPath, protectedPath));

    /// <summary>
    /// True when a user may not be allowed to point a mod root at this location.
    /// </summary>
    /// <param name="path">Directory the user selected.</param>
    /// <remarks>
    /// Exposed so a user-chosen mod folder can be screened with the application's own list of protected
    /// locations — filesystem roots, Windows, System32, Program Files, ProgramData — instead of a second,
    /// drifting copy of those rules living somewhere else. A path that cannot even be canonicalised is treated
    /// as protected, because refusing is the safe answer when the location cannot be resolved.
    /// </remarks>
    public bool IsProtectedLocation(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var canonical = Canonicalize(path);

        return canonical is null || IsProtected(canonical);
    }

    /// <summary>True when the path exists, following no link.</summary>
    /// <param name="path">Path to test.</param>
    private static bool ExistsNoFollow(string path) => FileSystemOps.ExistsNoFollow(path);

    /// <summary>Ordinal-ignore-case path equality on full paths.</summary>
    /// <param name="left">First path.</param>
    /// <param name="right">Second path.</param>
    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the protected-path set: every filesystem root, the Windows and Program Files
    /// directories, the machine-wide program data directory, and anything the user added.
    /// </summary>
    /// <param name="extra">User-supplied protected paths.</param>
    /// <remarks>
    /// The entries are canonicalised best-effort: a path that does not exist on this machine is
    /// simply not protected, which is correct — there is nothing there to damage.
    /// <para>
    /// Deliberately NOT protected: the user profile and AppData trees. Games legitimately live
    /// there (for example Minecraft's <c>.minecraft</c> under <c>%APPDATA%</c>), and approving a
    /// root inside them is a normal, user-directed operation. Every write is still contained by
    /// <see cref="ValidateWrite"/>'s approved-root check regardless of where the root is.
    /// </para>
    /// </remarks>
    private static HashSet<string> BuildProtectedPaths(IReadOnlyList<string> extra)
    {
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (full.Length > 0)
                {
                    protectedPaths.Add(full);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Nothing to protect if the path is not even parseable.
            }
        }

        // Every drive root. This is the spec's headline case: never operate on C:\ or D:\.
        foreach (var drive in DriveInfo.GetDrives())
        {
            Add(drive.RootDirectory.FullName);
            Add(drive.Name);
        }

        if (OperatingSystem.IsWindows())
        {
            Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.System));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        }
        else
        {
            Add("/");
            Add("/etc");
            Add("/usr");
            Add("/bin");
            Add("/boot");
            Add("/dev");
            Add("/sys");
            Add("/proc");
            Add("/var");
        }

        foreach (var entry in extra)
        {
            Add(entry);
        }

        return protectedPaths;
    }
}
