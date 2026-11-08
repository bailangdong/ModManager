using ModManager.Core.Safety;

namespace ModManager.Core.Services;

/// <summary>
/// The adapter that turns a declarative <see cref="GameDefinition"/> into a working game.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — the user's spec §3 and §20, plus Modrix's two-tier model. Modrix's position is
/// that most games need only a declarative <c>game.toml</c> and "games that need real logic can add a
/// sandboxed <c>game.lua</c>; most do not". This class is the C# equivalent of "most do not": it
/// implements the whole <see cref="IGameAdapter"/> contract from the definition alone.
/// </para>
/// <para>
/// That is what makes the spec's goal reachable — <i>"以后添加游戏应该尽量做到：增加 Game Definition /
/// Adapter，而不是修改整个核心程序"</i> — because a new game becomes a definition, and a definition can
/// be a JSON file the user drops in a folder. No new type, no new build.
/// </para>
/// <para>
/// A specialised adapter only exists where a definition genuinely cannot express the game: Palworld's
/// UE4SS layout, Skyrim's plugin activation, a game whose mod root moves with a patch. Those override
/// the handful of hooks below and inherit everything else.
/// </para>
/// </remarks>
public class GenericGameAdapter : IGameAdapter
{
    /// <summary>Creates an adapter for a definition.</summary>
    /// <param name="definition">Game definition.</param>
    public GenericGameAdapter(GameDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
    }

    /// <inheritdoc />
    public GameDefinition Definition { get; }

    /// <inheritdoc />
    public string GameId => Definition.Id;

    /// <inheritdoc />
    public virtual string ResolveModRoot(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        return DeploymentPlanner.ResolveDeploymentRoot(Definition, installPath);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The primary anchor comes from the definition. When the definition declares strip prefixes, an
    /// additional anchor is offered <i>without</i> those prefixes, which is how a mod shipped as
    /// <c>BepInEx/plugins/Mod/…</c> lands correctly in a game whose mod root is already
    /// <c>BepInEx/plugins</c>. Modrix documents this exact reconciliation: "Most mods ship their files
    /// bare and land there directly; the minority that ship the full
    /// <c>BepInEx/plugins/&lt;Mod&gt;/…</c> path have that redundant prefix stripped at staging, so both
    /// styles end up in the same place."
    /// </remarks>
    public virtual IReadOnlyList<ModRootAnchor> GetModRootAnchors(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        var anchors = new List<ModRootAnchor>
        {
            new(
                string.IsNullOrWhiteSpace(Definition.ModRoot) ? "Game install root" : Definition.ModRoot,
                Definition.ModRoot,
                IsPreferred: true,
                Definition.StripPrefixes),
        };

        // A game with a loader also accepts content placed relative to the loader's own container, so a
        // mod that ships the container explicitly still resolves.
        if (Definition.Loader is { PluginsDirectory: { Length: > 0 } plugins } loader
            && !string.Equals(loader.PluginsDirectory, Definition.ModRoot, StringComparison.OrdinalIgnoreCase))
        {
            var combined = string.IsNullOrWhiteSpace(Definition.ModRoot)
                ? loader.PluginsDirectory
                : Definition.ModRoot.TrimEnd('/') + "/" + loader.PluginsDirectory.TrimStart('/');

            anchors.Add(new ModRootAnchor(
                $"{loader.DisplayName} container",
                combined,
                IsPreferred: false,
                Definition.StripPrefixes));
        }

        return anchors;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The declarative check is <see cref="GameDefinition.RequiredFiles"/>: every listed file must
    /// exist relative to the candidate directory. Modrix relies on the same mechanism
    /// (<c>required_files</c>) and notes it exists "to rule out directories that look plausible but are
    /// not this game" — equivalent to Vortex's <c>requiredFiles</c>.
    /// </remarks>
    public virtual IReadOnlyList<string> ValidateInstall(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        var problems = new List<string>();

        if (!Directory.Exists(FileSystemOps.ToExtendedPath(installPath)))
        {
            problems.Add($"Directory does not exist: {installPath}");
            return problems;
        }

        var missing = FindMissingRequiredFiles(installPath);
        if (missing.Count > 0)
        {
            problems.Add(
                "Missing required file(s): " + string.Join(", ", missing)
                + ". This directory does not look like " + Definition.Name + ".");
        }

        return problems;
    }

    /// <summary>Returns which declared required files are absent.</summary>
    /// <param name="installPath">Candidate install directory.</param>
    public IReadOnlyList<string> FindMissingRequiredFiles(string installPath)
    {
        var missing = new List<string>();

        foreach (var required in Definition.RequiredFiles)
        {
            var candidate = installPath;
            foreach (var segment in required.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                candidate = Path.Combine(candidate, segment);
            }

            if (!FileSystemOps.ExistsNoFollow(candidate))
            {
                missing.Add(required);
            }
        }

        return missing;
    }

    /// <inheritdoc />
    public virtual IReadOnlyList<string> GetInstallPathHints()
    {
        var hints = new List<string>();

        if (string.IsNullOrWhiteSpace(Definition.Name))
        {
            return hints;
        }

        // Common DRM-free / portable locations worth a look when no store probe matched.
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"C:\Games",
            @"D:\Games",
        ];

        foreach (var root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                hints.Add(Path.Combine(root, Definition.Name));
            }
        }

        return hints;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns null, meaning "accept the generic decision". A specialised adapter overrides this when
    /// the generic scoring would pick wrong — the Palworld-class case where a lone <c>Binaries/</c> is
    /// content rather than a wrapper.
    /// </remarks>
    public virtual RootAnalysis? RefineRootAnalysis(
        IReadOnlyList<ModRootCandidate> candidates,
        IReadOnlyList<ArchiveEntry> archiveEntries)
    {
        _ = candidates;
        _ = archiveEntries;
        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Verbatim by default. A game that renames content overrides this; the FOMOD installer's
    /// <c>Data/</c>-prefix stripping is handled during staging, not here.
    /// </remarks>
    public virtual string? MapDeployPath(ArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var mapped = entry.Path;
        foreach (var prefix in Definition.StripPrefixes)
        {
            var normalized = prefix.Replace('\\', '/').Trim('/');
            if (normalized.Length == 0)
            {
                continue;
            }

            if (mapped.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase))
            {
                mapped = mapped[(normalized.Length + 1)..];
                break;
            }
        }

        return mapped;
    }

    /// <inheritdoc />
    public virtual IReadOnlyList<string> DetectPlugins(IReadOnlyList<string> deployedFileNames)
    {
        ArgumentNullException.ThrowIfNull(deployedFileNames);

        var loadOrder = Definition.LoadOrder;
        if (loadOrder is null || loadOrder.PluginExtensions.Count == 0)
        {
            return [];
        }

        var plugins = new List<string>();
        foreach (var name in deployedFileNames)
        {
            var extension = Path.GetExtension(name).TrimStart('.');
            if (loadOrder.PluginExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
            {
                plugins.Add(Path.GetFileName(name));
            }
        }

        return plugins;
    }

    /// <inheritdoc />
    public virtual OperationResult<IReadOnlyList<string>> ReadLoadOrder(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        var strategy = Definition.LoadOrder;
        if (strategy is null || strategy.Strategy == LoadOrderStrategy.None)
        {
            return OperationResult<IReadOnlyList<string>>.Fail($"{Definition.Name} has no load order.");
        }

        var path = ResolveLoadOrderFile(strategy);
        if (path is null)
        {
            return OperationResult<IReadOnlyList<string>>.Fail("The load-order file location could not be determined.");
        }

        if (!File.Exists(FileSystemOps.ToExtendedPath(path)))
        {
            return OperationResult<IReadOnlyList<string>>.Ok([]);
        }

        try
        {
            var plugins = File.ReadAllLines(FileSystemOps.ToExtendedPath(path))
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .Select(line => line.StartsWith('*') ? line[1..] : line)
                .ToList();

            return OperationResult<IReadOnlyList<string>>.Ok(plugins);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<IReadOnlyList<string>>.Fail($"Could not read the load order: {ex.Message}");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Written atomically through a sibling temp file, so a crash cannot leave a half-written
    /// <c>Plugins.txt</c> — which would make the game load an arbitrary subset of plugins. Modrix makes
    /// the same choice for its plugin file.
    /// </remarks>
    public virtual OperationResult<bool> WriteLoadOrder(string installPath, IReadOnlyList<string> order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentNullException.ThrowIfNull(order);

        var strategy = Definition.LoadOrder;
        if (strategy is null || strategy.Strategy != LoadOrderStrategy.PluginsTxt)
        {
            return OperationResult<bool>.Fail($"{Definition.Name} does not use a managed load-order file.");
        }

        var path = ResolveLoadOrderFile(strategy);
        if (path is null)
        {
            return OperationResult<bool>.Fail("The load-order file location could not be determined.");
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(FileSystemOps.ToExtendedPath(directory));
            }

            var lines = new List<string>
            {
                "# Managed by ModManager. Lines starting with '*' are enabled plugins.",
            };

            var vanilla = new HashSet<string>(strategy.VanillaPlugins, StringComparer.OrdinalIgnoreCase);

            foreach (var plugin in order)
            {
                if (string.IsNullOrWhiteSpace(plugin))
                {
                    continue;
                }

                // Vanilla plugins are listed but never have their activation state invented for them:
                // the game's own masters must stay enabled or the game will not start.
                lines.Add(vanilla.Contains(plugin) ? plugin : "*" + plugin);
            }

            FileSystemOps.WriteAtomicAsync(path, string.Join(Environment.NewLine, lines) + Environment.NewLine)
                .GetAwaiter()
                .GetResult();

            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<bool>.Fail($"Could not write the load order: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public virtual LoaderStatus CheckLoader(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        var loader = Definition.Loader;
        if (loader is null || loader.Kind == ModLoaderKind.Unknown)
        {
            return new LoaderStatus(false, true, null, [], "该游戏不需要 Mod 加载器。");
        }

        var missing = new List<string>();

        foreach (var marker in loader.MarkerFiles)
        {
            var candidate = installPath;
            foreach (var segment in marker.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                candidate = Path.Combine(candidate, segment);
            }

            if (!FileSystemOps.ExistsNoFollow(candidate))
            {
                missing.Add(marker);
            }
        }

        // A loader can also prove itself by shipping a recognisable binary at the install root.
        var hasRootBinary = false;
        if (loader.RootFilePrefixes.Count > 0 && Directory.Exists(FileSystemOps.ToExtendedPath(installPath)))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(FileSystemOps.ToExtendedPath(installPath)))
                {
                    var name = Path.GetFileName(file);
                    if (loader.RootFilePrefixes.Any(prefix =>
                        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        hasRootBinary = true;
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Treated as "not found"; the marker-file test still governs.
            }
        }

        var installed = missing.Count == 0 || hasRootBinary;
        var message = installed
            ? $"已检测到 {loader.DisplayName}。"
            : loader.MissingMessage.Length > 0
                ? loader.MissingMessage
                : $"需要 {loader.DisplayName}，但没有找到。缺少它 Mod 无法加载。";

        return new LoaderStatus(true, installed, null, installed ? [] : missing, message);
    }

    /// <inheritdoc />
    public virtual OperationResult<bool> Launch(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        if (string.IsNullOrWhiteSpace(Definition.Executable))
        {
            return OperationResult<bool>.Fail($"{Definition.Name} does not declare an executable to launch.");
        }

        var path = installPath;
        foreach (var segment in Definition.Executable.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = Path.Combine(path, segment);
        }

        if (!File.Exists(FileSystemOps.ToExtendedPath(path)))
        {
            return OperationResult<bool>.Fail($"Executable not found: {path}");
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = installPath,
                UseShellExecute = false,
            };

            if (!string.IsNullOrWhiteSpace(Definition.LaunchArguments))
            {
                startInfo.Arguments = Definition.LaunchArguments;
            }

            // Deliberately no elevation: a mod manager has no business launching a game as administrator.
            System.Diagnostics.Process.Start(startInfo);
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return OperationResult<bool>.Fail($"Could not launch {Definition.Name}: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public virtual IReadOnlyList<HealthIssue> RunHealthChecks(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        var issues = new List<HealthIssue>();

        var loader = CheckLoader(installPath);
        if (loader.IsBlocking)
        {
            issues.Add(new HealthIssue(
                IssueSeverity.Error,
                $"缺少 Mod 加载器：{Definition.Loader?.DisplayName}",
                loader.Message,
                Definition.Loader?.DownloadUrl is { Length: > 0 } url
                    ? $"可从 {url} 安装后重新扫描。"
                    : "安装加载器后重新扫描。",
                RelatedModId: null,
                IsBlocking: true));
        }

        if (!Directory.Exists(FileSystemOps.ToExtendedPath(installPath)))
        {
            issues.Add(new HealthIssue(
                IssueSeverity.Error,
                "游戏目录不存在",
                $"“{installPath}” 已不存在。",
                "重新添加游戏，并指向正确的目录。",
                RelatedModId: null,
                IsBlocking: true));
        }

        return issues;
    }

    /// <summary>Resolves the absolute path of the load-order file for a strategy.</summary>
    /// <param name="strategy">Load-order definition.</param>
    protected string? ResolveLoadOrderFile(LoadOrderDefinition strategy)
    {
        if (!string.IsNullOrWhiteSpace(strategy.ExplicitPath))
        {
            return strategy.ExplicitPath;
        }

        if (strategy.Strategy != LoadOrderStrategy.PluginsTxt)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(strategy.AppDataDirectory))
        {
            return null;
        }

        // Bethesda games read Plugins.txt from the local app data directory named after the game, which
        // is what Modrix's appdata_dir encodes. It is NOT in the game folder, which is why this cannot
        // be derived from installPath.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, strategy.AppDataDirectory, "Plugins.txt");
    }
}

/// <summary>
/// The fallback adapter for a game the user adds without any definition at all.
/// </summary>
/// <remarks>
/// The user's spec §20 says phase one implements <c>GenericModAdapter</c> and then validates against
/// a few structurally different games. This is that adapter in its most general form: a
/// <c>Mods/</c> folder at the install root, no loader, no load order, no plugins. It is deliberately
/// unambitious, because its job is to be correct for any game rather than excellent for one.
/// </remarks>
public sealed class GenericModAdapter : GenericGameAdapter
{
    /// <summary>Creates the fallback definition for an unknown game.</summary>
    /// <param name="gameId">Identifier for the game.</param>
    /// <param name="name">Display name.</param>
    /// <param name="installPath">Known install directory, when the user supplied one.</param>
    public GenericModAdapter(string gameId, string name, string? installPath = null)
        : base(BuildDefinition(gameId, name, installPath))
    {
    }

    /// <summary>Builds the minimal definition this adapter serves.</summary>
    /// <param name="gameId">Identifier for the game.</param>
    /// <param name="name">Display name.</param>
    /// <param name="installPath">Known install directory.</param>
    private static GameDefinition BuildDefinition(string gameId, string name, string? installPath) => new()
    {
        Id = string.IsNullOrWhiteSpace(gameId) ? "generic" : gameId,
        Name = string.IsNullOrWhiteSpace(name) ? "Generic Game" : name,
        Description = "A game with no specific definition. Mods deploy into a Mods folder at the install root.",
        ModRoot = "Mods",

        // No loader, no load order, no plugins: this game gets the plainest possible treatment, and the
        // UI hides every capability it does not have.
        Capabilities =
            GameCapability.HardlinkDeployment
            | GameCapability.SymlinkDeployment
            | GameCapability.Profiles
            | GameCapability.FileConflicts
            | GameCapability.Dependencies,

        DeploymentPreference = [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy],
        InstallProbes = installPath is null ? [] : [InstallProbeKind.PathHint],
        IsUserDefined = true,
        SourcePath = installPath,
    };
}
