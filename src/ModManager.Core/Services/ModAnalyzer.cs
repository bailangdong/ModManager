using ModManager.Core.Safety;

namespace ModManager.Core.Services;

/// <summary>
/// Decides where an archive's mod content really begins, and how confident it is.
/// </summary>
/// <remarks>
/// <para>
/// This is the user's spec §6, and the spec states the constraint that shapes the whole design:
/// <b>"不能只根据目录名字猜"</b> — directory names alone are not enough. It also gives the two
/// canonical cases:
/// </para>
/// <code>
/// CoolMod.zip            Mod.zip
/// └── CoolMod/           └── Binaries/
///     └── Binaries/          └── Win64/
///         └── Win64/             └── xxx.dll
///             └── xxx.dll
/// </code>
/// <para>
/// The first must deploy <c>Binaries/Win64/xxx.dll</c> (the <c>CoolMod</c> wrapper is stripped); the
/// second must deploy <c>Binaries/Win64/xxx.dll</c> as-is (<c>Binaries</c> is content, not a
/// wrapper). Directory names alone cannot distinguish these, so the analyzer combines several
/// independent signals and reports a confidence:
/// </para>
/// <list type="number">
/// <item><b>Declared content directories</b> from the game definition — the strongest signal, and the
/// one that resolves the case above. Modrix's comment on Subnautica explains exactly why this
/// matters: "A lone <c>BepInEx/</c> tree at an archive root is content, not a packaging wrapper -
/// hoisting it would strip the container and deploy the plugin where the loader never finds it."</item>
/// <item><b>Mod file extensions</b> the game declares (<c>.pak</c>, <c>.dll</c>, <c>.esp</c>).</item>
/// <item><b>Known loader layouts</b> (<c>BepInEx/plugins</c>, <c>Data/</c>, <c>Mods/</c>).</item>
/// <item><b>Recognised mod metadata</b> (<c>manifest.json</c>, <c>modinfo.json</c>, <c>info.json</c>).</item>
/// <item><b>Readme hints</b> — weak evidence, but it breaks ties.</item>
/// <item><b>Redundant prefix</b>: an archive that already carries the game's own mod-root path.</item>
/// </list>
/// <para>
/// When confidence falls below <see cref="RootAnalysis.Threshold"/> the UI must show the installation
/// preview and ask, which is the spec's "如果置信度不足：显示安装预览，让用户确认".
/// </para>
/// </remarks>
public sealed class ModAnalyzer : IModAnalyzer
{
    /// <summary>
    /// Directory names that are content rather than packaging, used when a game declares none.
    /// </summary>
    /// <remarks>
    /// Modrix falls back to an 18-entry default list for <c>api_version = 1</c> definitions. We keep a
    /// comparable list for the same reason: a game definition that predates content directories, or a
    /// user-supplied one that omits them, should still behave sensibly rather than treating every mod
    /// as a wrapped archive.
    /// </remarks>
    public static readonly IReadOnlyList<string> DefaultContentDirectories =
    [
        "data", "meshes", "textures", "scripts", "interface", "sound", "music", "strings",
        "seq", "grass", "shaders", "lodsettings", "skse", "fomod", "source", "mcm",
        "binaries", "content", "mods", "plugins", "pak", "paks", "bepinex", "qmods",
        "melonloader", "userlibs", "ue4ss", "logicmods", "cookedpc", "assets", "config",
    ];

    /// <summary>Directory names that are almost always packaging noise rather than mod content.</summary>
    /// <remarks>
    /// Deliberately does <b>not</b> include any name from <see cref="DefaultContentDirectories"/>, and
    /// is only consulted when no positive signal fired for a candidate.
    /// </remarks>
    public static readonly IReadOnlyList<string> PackagingNoiseDirectories =
    [
        "fomod", "docs", "doc", "documentation", "screenshots", "screenshot", "images",
        "preview", "previews", "__macosx", "optional", "extras", "tools", "source", "src",
    ];

    /// <summary>Recognised mod metadata file names that mark a directory as a mod root.</summary>
    public static readonly IReadOnlyList<string> ModMetadataFiles =
    [
        "manifest.json", "modinfo.json", "info.json", "mod.json", "meta.ini", "fomod",
        "moduleconfig.xml", "mod.config", "about.xml", "description.json",
    ];

    /// <summary>Extension patterns used to recognise a readme.</summary>
    private static readonly string[] ReadmePrefixes = ["readme", "read_me", "read-me", "说明", "安装说明"];

    /// <summary>Signal weights. Kept as named constants so a test can reason about them.</summary>
    private static class Weight
    {
        public const double ContentDirectoryMatch = 0.55d;
        public const double ModFileExtensionMatch = 0.25d;
        public const double KnownLoaderLayout = 0.45d;
        public const double ModMetadataPresent = 0.35d;
        public const double ReadmeHint = 0.10d;
        public const double StripPrefixMatch = 0.30d;
        public const double SingleTopLevelDirectoryBonus = 0.15d;
        public const double PackagingEntriesIgnored = 0.05d;
        public const double NoisePenalty = 0.40d;
        public const double NoContentPenalty = 0.50d;
    }

    /// <inheritdoc />
    public RootAnalysis Analyze(IGameAdapter adapter, ArchiveIndex index, ArchiveLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(index);

        var effectiveLimits = limits ?? ArchiveLimits.Default;
        var definition = adapter.Definition;

        // Entry paths from the archive are untrusted, so unsafe ones are dropped here rather than
        // carried into scoring, where they would distort the file counts.
        var safeEntries = new List<ArchiveEntry>();
        var skipped = new List<RootSignalHit>();

        foreach (var entry in index.Entries)
        {
            if (!ArchiveReader.IsSafeEntryName(entry.Path, out var reason))
            {
                skipped.Add(new RootSignalHit(
                    RootDetectionSignal.RejectedNoiseDirectory,
                    0d,
                    $"Skipped '{entry.Path}': {reason}"));
                continue;
            }

            safeEntries.Add(entry);
        }

        var contentDirectories = BuildContentDirectorySet(definition);
        var modExtensions = BuildExtensionSet(definition);
        var topLevel = EnumerateTopLevelDirectories(safeEntries);
        var isFomod = DetectFomod(safeEntries);
        var loader = DetectLoader(definition, safeEntries);
        var readmes = FindReadmes(safeEntries);

        var candidates = new List<ModRootCandidate>
        {
            ScoreCandidate(string.Empty, safeEntries, contentDirectories, modExtensions, definition, loader, readmes, topLevel.Count),
        };

        foreach (var directory in topLevel)
        {
            var scoped = safeEntries
                .Where(e => IsUnder(e.Path, directory))
                .ToList();

            candidates.Add(ScoreCandidate(
                directory,
                scoped,
                contentDirectories,
                modExtensions,
                definition,
                loader,
                readmes,
                topLevel.Count));
        }

        candidates.Sort((left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0
                ? byScore
                // Shorter paths first on a tie, so the archive root and shallow wrappers win over deep
                // coincidences. Ordinal to keep the order reproducible across machines.
                : string.CompareOrdinal(left.RelativePath, right.RelativePath);
        });

        var winner = candidates[0];
        var confidence = DeriveConfidence(candidates, winner, isFomod, loader);

        var notes = new List<string>();
        if (winner.RelativePath.Length == 0)
        {
            notes.Add("Content is at the archive root; nothing will be hoisted.");
        }
        else
        {
            notes.Add(
                $"Detected wrapper directory '{winner.RelativePath}'. Files inside it will be deployed "
                + "relative to the mod root, so the wrapper itself is not copied.");
        }

        if (isFomod)
        {
            notes.Add("This archive contains a FOMOD installer configuration.");
        }

        if (loader != ModLoaderKind.Unknown)
        {
            notes.Add($"Layout matches the {loader} mod loader.");
        }

        if (definition.ModFileExtensions.Count == 0)
        {
            notes.Add("The game definition declares no mod file extensions, so scoring relies on content directories only.");
        }

        var deployable = CountDeployable(safeEntries, winner.RelativePath, contentDirectories);

        return new RootAnalysis
        {
            SelectedRoot = winner.RelativePath,
            Confidence = confidence,
            Candidates = candidates,
            Signals = winner.Signals,
            Format = DetermineFormat(index.ArchivePath),
            TotalSize = safeEntries.Aggregate(0L, (total, entry) => SaturatingAdd(total, entry.Size)),
            FileCount = safeEntries.Count,
            SkippedEntries = skipped,
            IsFomod = isFomod,
            DetectedLoader = loader,
            ReadmePaths = readmes,
            Notes = notes,
            DeployableFileCount = deployable,
        };
    }

    /// <inheritdoc />
    public RootAnalysis AnalyzeDirectory(IGameAdapter adapter, string directory)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var entries = new List<ArchiveEntry>();

        if (Directory.Exists(FileSystemOps.ToExtendedPath(directory)))
        {
            foreach (var file in FileSystemOps.EnumerateFilesSafe(directory))
            {
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                long size;
                try
                {
                    size = new FileInfo(FileSystemOps.ToExtendedPath(file)).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    size = 0;
                }

                entries.Add(new ArchiveEntry(relative, false, size));
            }
        }

        return Analyze(adapter, new ArchiveIndex(directory, entries));
    }

    /// <summary>Scores one candidate root.</summary>
    /// <param name="relativePath">Candidate root, empty for the archive root.</param>
    /// <param name="entries">Entries under this candidate.</param>
    /// <param name="contentDirectories">Content directory names the game declares.</param>
    /// <param name="modExtensions">Mod file extensions the game declares.</param>
    /// <param name="definition">Game definition.</param>
    /// <param name="loader">Detected loader kind.</param>
    /// <param name="readmes">Readme paths found anywhere in the archive.</param>
    /// <param name="topLevelCount">Number of top-level directories in the archive.</param>
    private static ModRootCandidate ScoreCandidate(
        string relativePath,
        IReadOnlyList<ArchiveEntry> entries,
        IReadOnlySet<string> contentDirectories,
        IReadOnlySet<string> modExtensions,
        GameDefinition definition,
        ModLoaderKind loader,
        IReadOnlyList<string> readmes,
        int topLevelCount)
    {
        var hits = new List<RootSignalHit>();
        var score = 0d;
        long totalSize = 0;
        var fileCount = 0;

        foreach (var entry in entries)
        {
            if (!entry.IsDirectory)
            {
                fileCount++;
                totalSize = SaturatingAdd(totalSize, entry.Size);
            }
        }

        if (fileCount == 0)
        {
            return new ModRootCandidate(
                relativePath,
                0d,
                [new RootSignalHit(RootDetectionSignal.RejectedNoContent, 0d, "No files under this path.")],
                0,
                0);
        }

        // --- signal 1: declared content directories, including the strip-prefix case ---------------
        // Children are computed relative to the candidate root, because scoped entries keep their full
        // archive paths — scoring the archive root on the wrapper's name (or a nested candidate on the
        // wrapper's name) would invert the very case this signal exists to resolve.
        var relativePrefix = relativePath.Length == 0 ? string.Empty : relativePath + "/";
        var immediateChildren = entries
            .Where(entry => entry.Path.StartsWith(relativePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Path[relativePrefix.Length..])
            .Select(path => path.Contains('/') ? path[..path.IndexOf('/')] : null)
            .Where(name => name is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var contentMatches = immediateChildren.Where(contentDirectories.Contains).ToList();
        var modRootSegments = SplitPath(definition.ModRoot);

        // The game's own mod-root container ("Mods", "BepInEx", ...) sitting directly under this
        // candidate means the candidate IS the mod root — the container must not be treated as a
        // wrapper, and its prefix is stripped at staging.
        var modRootAsked = modRootSegments.Count > 0 && immediateChildren.Contains(modRootSegments[0]);

        if (contentMatches.Count > 0)
        {
            score += Weight.ContentDirectoryMatch;
            hits.Add(new RootSignalHit(
                RootDetectionSignal.ContentDirectoryMatch,
                Weight.ContentDirectoryMatch,
                $"Contains the game's content director{(contentMatches.Count == 1 ? "y" : "ies")} "
                + $"{string.Join(", ", contentMatches)}."));
        }

        if (modRootAsked)
        {
            score += Weight.StripPrefixMatch;
            hits.Add(new RootSignalHit(
                RootDetectionSignal.StripPrefixMatch,
                Weight.StripPrefixMatch,
                $"Matches the game's own mod root path ('{definition.ModRoot}'), so it is the container, not a wrapper."));
        }

        // --- signal 2: mod file extensions --------------------------------------------------------
        var extensionMatches = entries
            .Where(e => !e.IsDirectory && modExtensions.Contains(Path.GetExtension(e.Path).TrimStart('.')))
            .Select(e => Path.GetExtension(e.Path).TrimStart('.').ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToList();

        if (extensionMatches.Count > 0)
        {
            score += Weight.ModFileExtensionMatch;
            hits.Add(new RootSignalHit(
                RootDetectionSignal.ModFileExtensionMatch,
                Weight.ModFileExtensionMatch,
                $"Contains the game's mod file type{(extensionMatches.Count == 1 ? "" : "s")} "
                + $"{string.Join(", ", extensionMatches.Select(e => "." + e))}."));
        }

        // --- signal 3: known loader layout --------------------------------------------------------
        if (loader != ModLoaderKind.Unknown)
        {
            score += Weight.KnownLoaderLayout;
            hits.Add(new RootSignalHit(
                RootDetectionSignal.KnownLoaderLayout,
                Weight.KnownLoaderLayout,
                $"Matches the {loader} layout."));
        }

        // --- signal 4: recognised mod metadata ---------------------------------------------------
        var metadata = entries
            .Where(e => !e.IsDirectory && ModMetadataFiles.Contains(Path.GetFileName(e.Path).ToLowerInvariant()))
            .Select(e => Path.GetFileName(e.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        if (metadata.Count > 0)
        {
            score += Weight.ModMetadataPresent;
            hits.Add(new RootSignalHit(
                RootDetectionSignal.ModMetadataPresent,
                Weight.ModMetadataPresent,
                $"Contains mod metadata: {string.Join(", ", metadata)}."));
        }

        // --- signal 5: readme hints ---------------------------------------------------------------
        var localReadmes = readmes.Where(r => relativePath.Length == 0 || IsUnder(r, relativePath)).ToList();
        if (localReadmes.Count > 0)
        {
            score += Weight.ReadmeHint;
            hits.Add(new RootSignalHit(
                RootDetectionSignal.ReadmeHint,
                Weight.ReadmeHint,
                $"Contains {localReadmes.Count} readme file(s), which usually sit beside the real content."));
        }

        // --- signal 6: a lone top-level directory is usually packaging -----------------------------
        if (relativePath.Length == 0 && topLevelCount == 1)
        {
            score += Weight.SingleTopLevelDirectoryBonus;
            hits.Add(new RootSignalHit(
                RootDetectionSignal.SingleTopLevelDirectory,
                Weight.SingleTopLevelDirectoryBonus,
                "The archive root holds exactly one directory and no loose content."));
        }

        // --- penalty: this candidate looks like packaging noise -----------------------------------
        var noise = immediateChildren.Where(PackagingNoiseDirectories.Contains).ToList();
        var hasPositive = hits.Any(h => h.Weight > 0
            && h.Signal is not RootDetectionSignal.ReadmeHint
            && h.Signal is not RootDetectionSignal.SingleTopLevelDirectory);

        if (noise.Count > 0 && !hasPositive)
        {
            score -= Weight.NoisePenalty;
            hits.Add(new RootSignalHit(
                RootDetectionSignal.RejectedNoiseDirectory,
                -Weight.NoisePenalty,
                $"Looks like packaging: {string.Join(", ", noise)}."));
        }
        else if (noise.Count > 0)
        {
            score += Weight.PackagingEntriesIgnored;
            hits.Add(new RootSignalHit(
                RootDetectionSignal.PackagingEntriesIgnored,
                Weight.PackagingEntriesIgnored,
                $"Contains packaging folders that will be ignored: {string.Join(", ", noise)}."));
        }

        // --- penalty: nothing recognisable at all -------------------------------------------------
        if (!hasPositive && noise.Count == 0)
        {
            score -= Weight.NoContentPenalty;
            hits.Add(new RootSignalHit(
                RootDetectionSignal.RejectedNoContent,
                -Weight.NoContentPenalty,
                "No recognised mod content."));
        }

        return new ModRootCandidate(relativePath, score, hits, fileCount, totalSize);
    }

    /// <summary>
    /// Derives a 0..1 confidence from the winner's lead over the runners-up.
    /// </summary>
    /// <param name="candidates">All scored candidates, best first.</param>
    /// <param name="winner">Best candidate.</param>
    /// <param name="isFomod">Whether a FOMOD configuration was found.</param>
    /// <param name="loader">Detected loader.</param>
    /// <remarks>
    /// The lead matters more than the absolute score, and that is deliberate. An archive whose root
    /// clearly wins by a wide margin is unambiguous even if its absolute score is modest; an archive
    /// where two candidates score almost identically is ambiguous even if both score well. So the
    /// runner-up's score is subtracted, which is exactly the "is this a close call?" question the UI
    /// needs answered.
    /// </remarks>
    private static double DeriveConfidence(
        IReadOnlyList<ModRootCandidate> candidates,
        ModRootCandidate winner,
        bool isFomod,
        ModLoaderKind loader)
    {
        var runnerUpScore = candidates.Count > 1 ? Math.Max(0d, candidates[1].Score) : 0d;
        var lead = Math.Max(0d, winner.Score) - runnerUpScore;

        // Weight the absolute score and the lead together: 60% absolute, 40% lead.
        var confidence = (Math.Clamp(winner.Score, 0d, 1d) * 0.6d) + (Math.Clamp(lead, 0d, 1d) * 0.4d);

        // A FOMOD installer defines its own layout, so the root question is mostly moot and the wizard
        // takes over. Treat it as confident so the plain-copy path is not offered.
        if (isFomod)
        {
            confidence = Math.Max(confidence, 0.8d);
        }

        // A detected loader container means the mod root is known from the layout itself.
        if (loader is ModLoaderKind.BepInEx or ModLoaderKind.MelonLoader or ModLoaderKind.Ue4ss)
        {
            confidence = Math.Max(confidence, 0.85d);
        }

        return Math.Clamp(confidence, 0d, 1d);
    }

    /// <summary>Builds the content-directory set for a definition, falling back to the defaults.</summary>
    /// <param name="definition">Game definition.</param>
    private static HashSet<string> BuildContentDirectorySet(GameDefinition definition)
    {
        var names = definition.ContentDirectories.Count > 0
            ? definition.ContentDirectories
            : DefaultContentDirectories;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var trimmed = name.Trim().Trim('/').ToLowerInvariant();
            if (trimmed.Length > 0)
            {
                set.Add(trimmed);
            }
        }

        // The game's own mod-root segments are containers, never wrappers.
        foreach (var segment in SplitPath(definition.ModRoot))
        {
            set.Add(segment);
        }

        foreach (var prefix in definition.StripPrefixes)
        {
            foreach (var segment in SplitPath(prefix))
            {
                set.Add(segment);
            }
        }

        return set;
    }

    /// <summary>Builds the mod-extension set for a definition.</summary>
    /// <param name="definition">Game definition.</param>
    private static HashSet<string> BuildExtensionSet(GameDefinition definition)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in definition.ModFileExtensions)
        {
            var trimmed = extension.Trim().TrimStart('.').ToLowerInvariant();
            if (trimmed.Length > 0)
            {
                set.Add(trimmed);
            }
        }

        return set;
    }

    /// <summary>Splits a relative path into lower-cased segments.</summary>
    /// <param name="path">Path to split.</param>
    private static List<string> SplitPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? []
            : path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment.ToLowerInvariant())
                .ToList();

    /// <summary>Returns the distinct top-level directory names in an archive.</summary>
    /// <param name="entries">Archive entries.</param>
    private static List<string> EnumerateTopLevelDirectories(IReadOnlyList<ArchiveEntry> entries)
    {
        var directories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var separator = entry.Path.IndexOf('/', StringComparison.Ordinal);
            if (separator > 0)
            {
                directories.Add(entry.Path[..separator]);
            }
        }

        return directories.ToList();
    }

    /// <summary>True when an entry path is inside a candidate root.</summary>
    /// <param name="entryPath">Entry path.</param>
    /// <param name="root">Candidate root.</param>
    private static bool IsUnder(string entryPath, string root) =>
        root.Length == 0 || entryPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds readme-like files anywhere in the archive.</summary>
    /// <param name="entries">Archive entries.</param>
    private static List<string> FindReadmes(IReadOnlyList<ArchiveEntry> entries)
    {
        var readmes = new List<string>();

        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(entry.Path).ToLowerInvariant();
            if (ReadmePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                readmes.Add(entry.Path);
            }
        }

        return readmes;
    }

    /// <summary>True when the archive carries a FOMOD installer configuration.</summary>
    /// <param name="entries">Archive entries.</param>
    /// <remarks>
    /// The check is case-insensitive because FOMOD configs in the wild are inconsistent about it —
    /// Modrix notes its parser has to be case-insensitive for exactly this reason.
    /// </remarks>
    private static bool DetectFomod(IReadOnlyList<ArchiveEntry> entries) =>
        entries.Any(entry =>
            string.Equals(Path.GetFileName(entry.Path), "ModuleConfig.xml", StringComparison.OrdinalIgnoreCase)
            || entry.Path.Split('/').Any(segment => string.Equals(segment, "fomod", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Infers a mod-loader kind from the archive layout and the definition.</summary>
    /// <param name="definition">Game definition.</param>
    /// <param name="entries">Archive entries.</param>
    private static ModLoaderKind DetectLoader(GameDefinition definition, IReadOnlyList<ArchiveEntry> entries)
    {
        if (definition.Loader is { Kind: not ModLoaderKind.Unknown } declared)
        {
            return declared.Kind;
        }

        foreach (var entry in entries)
        {
            var lower = entry.Path.ToLowerInvariant();
            if (lower.StartsWith("bepinex/", StringComparison.Ordinal) || lower.Contains("/bepinex/", StringComparison.Ordinal))
            {
                return ModLoaderKind.BepInEx;
            }

            if (lower.StartsWith("ue4ss/", StringComparison.Ordinal) || lower.Contains("ue4ss", StringComparison.Ordinal))
            {
                return ModLoaderKind.Ue4ss;
            }

            if (lower.StartsWith("melonloader/", StringComparison.Ordinal))
            {
                return ModLoaderKind.MelonLoader;
            }

            if (lower.StartsWith("userlibs/", StringComparison.Ordinal))
            {
                return ModLoaderKind.BepInEx;
            }

            if (lower.StartsWith("skse/", StringComparison.Ordinal))
            {
                return ModLoaderKind.ScriptExtender;
            }
        }

        return ModLoaderKind.Unknown;
    }

    /// <summary>Counts the files that will actually be deployed from a chosen root.</summary>
    /// <param name="entries">All entries.</param>
    /// <param name="root">Chosen root.</param>
    /// <param name="contentDirectories">Content directory names.</param>
    private static int CountDeployable(
        IReadOnlyList<ArchiveEntry> entries,
        string root,
        IReadOnlySet<string> contentDirectories)
    {
        _ = contentDirectories;
        var count = 0;

        foreach (var entry in entries)
        {
            if (entry.IsDirectory || !IsUnder(entry.Path, root))
            {
                continue;
            }

            var relative = root.Length == 0 ? entry.Path : entry.Path[(root.Length + 1)..];

            // Mirror the planner's exclusion rule so the preview's file count matches what will
            // really be deployed.
            var excluded = relative
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment.StartsWith('.') || segment.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase));

            if (!excluded)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Maps a path to an archive format, by extension.</summary>
    /// <param name="archivePath">Archive path.</param>
    private static ArchiveFormat DetermineFormat(string archivePath) =>
        Path.GetExtension(archivePath).ToLowerInvariant() switch
        {
            ".7z" => ArchiveFormat.SevenZip,
            ".rar" => ArchiveFormat.Rar,
            ".tar" => ArchiveFormat.Tar,
            ".gz" or ".tgz" => ArchiveFormat.Gzip,
            ".fomod" or ".omod" => ArchiveFormat.Fomod,
            _ => ArchiveFormat.Zip,
        };

    /// <summary>Adds two sizes, saturating rather than overflowing.</summary>
    /// <param name="left">First value.</param>
    /// <param name="right">Second value.</param>
    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
