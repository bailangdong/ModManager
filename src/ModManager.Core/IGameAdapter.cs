namespace ModManager.Core;

/// <summary>
/// A game adapter: the per-game logic a declarative definition cannot express.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — the user's spec §3, plus Modrix and Vortex. Modrix's position is that most
/// games need only a declarative <c>game.toml</c> and "games that need real logic can add a
/// sandboxed <c>game.lua</c>; most do not." Vortex expresses the code path as <c>IGame</c> with
/// <c>queryModPath</c>, <c>queryPath</c>, <c>getGameVersion</c> and friends.
/// </para>
/// <para>
/// We adopt the same two-tier split, but with the tiers reversed from Vortex for ergonomics: a
/// <see cref="GameDefinition"/> carries the data, and an adapter is an optional <i>strategy</i>
/// that refines it. <see cref="GenericGameAdapter"/> implements the whole interface from the
/// definition alone, so a brand-new game is a JSON or code definition with no adapter at all.
/// A specialised adapter (Palworld, Skyrim, a BepInEx title) only overrides the handful of hooks
/// where the declarative model is genuinely insufficient.
/// </para>
/// <para>
/// <b>Dependency rule:</b> nothing in the engine may ask "which game is this?" via a type test.
/// All game-specific behaviour arrives through this interface or through the definition, which is
/// what keeps "add a game without modifying the core program" true.
/// </para>
/// </remarks>
public interface IGameAdapter
{
    /// <summary>The declarative definition this adapter serves.</summary>
    GameDefinition Definition { get; }

    /// <summary>Stable game id, always <c>Definition.Id</c>.</summary>
    string GameId { get; }

    /// <summary>
    /// Absolute mod root for a concrete install. Default implementation combines
    /// <see cref="GameDefinition.ModRoot"/> with the install directory or, when
    /// <see cref="GameDefinition.ModRootInUserProfile"/> is set, with the user profile.
    /// </summary>
    /// <param name="installPath">Absolute game install directory.</param>
    string ResolveModRoot(string installPath);

    /// <summary>
    /// The bases an archive may be anchored to, in preference order. Most games have exactly one;
    /// Bethesda-style games additionally accept <c>Data/</c> prefixes, and loader games accept the
    /// loader container.
    /// </summary>
    IReadOnlyList<ModRootAnchor> GetModRootAnchors(string installPath);

    /// <summary>
    /// Extra evidence that a candidate directory really is this game's install. Runs after the
    /// declarative <c>required_files</c> check.
    /// </summary>
    /// <param name="installPath">Candidate directory.</param>
    IReadOnlyList<string> ValidateInstall(string installPath);

    /// <summary>
    /// Root paths to search for a game installed outside the usual store locations. Empty for most
    /// games; a real hint list for DRM-free titles.
    /// </summary>
    IReadOnlyList<string> GetInstallPathHints();

    /// <summary>
    /// Refines the analyzer's root decision for this game. Returning null means "accept the
    /// generic decision". This is the hook where a game can say "a lone <c>Binaries/</c> is
    /// content, never a wrapper".
    /// </summary>
    /// <param name="candidates">Scored candidates from the generic analyzer.</param>
    /// <param name="archiveEntries">The archive's entry list.</param>
    RootAnalysis? RefineRootAnalysis(IReadOnlyList<ModRootCandidate> candidates, IReadOnlyList<ArchiveEntry> archiveEntries);

    /// <summary>
    /// Maps an archive entry to its deployed target, relative to the mod root. Returning null means
    /// "deploy verbatim". Used for loader-prefix stripping and for games that rename content.
    /// </summary>
    /// <param name="entry">Archive entry, relative to the selected root.</param>
    string? MapDeployPath(ArchiveEntry entry);

    /// <summary>
    /// Files whose presence in the archive implies a load order or plugin that must be registered
    /// after deploy (e.g. <c>*.esp</c> for Bethesda games). Empty when the game has none.
    /// </summary>
    /// <param name="deployedFileNames">File names just deployed.</param>
    IReadOnlyList<string> DetectPlugins(IReadOnlyList<string> deployedFileNames);

    /// <summary>
    /// Reads the game's current load order, when it has one.
    /// </summary>
    /// <param name="installPath">Absolute install directory.</param>
    OperationResult<IReadOnlyList<string>> ReadLoadOrder(string installPath);

    /// <summary>
    /// Writes a load order. Implementations must write atomically and must preserve plugins they do
    /// not own.
    /// </summary>
    /// <param name="installPath">Absolute install directory.</param>
    /// <param name="order">Full plugin order, enabled plugins prefixed per the strategy.</param>
    OperationResult<bool> WriteLoadOrder(string installPath, IReadOnlyList<string> order);

    /// <summary>
    /// Checks whether this game's required mod loader is installed, and at what version.
    /// </summary>
    /// <param name="installPath">Absolute install directory.</param>
    LoaderStatus CheckLoader(string installPath);

    /// <summary>
    /// Launches the game, when it has an executable. Implementations must never launch with
    /// elevated rights and must refuse when the executable is missing.
    /// </summary>
    /// <param name="installPath">Absolute install directory.</param>
    OperationResult<bool> Launch(string installPath);

    /// <summary>
    /// Game-specific health checks beyond the declarative ones (missing loader, a two-part mod
    /// installed incompletely).
    /// </summary>
    /// <param name="installPath">Absolute install directory.</param>
    IReadOnlyList<HealthIssue> RunHealthChecks(string installPath);
}

/// <summary>
/// One place an archive might be anchored to, so the analyzer can try more than one layout.
/// </summary>
/// <param name="Name">Human-readable label, e.g. "BepInEx plugin container".</param>
/// <param name="RelativePath">Path relative to the install directory.</param>
/// <param name="IsPreferred">True for the game's primary mod root.</param>
/// <param name="AcceptedPrefixes">Archive prefixes that may be stripped when targeting this anchor.</param>
public sealed record ModRootAnchor(
    string Name,
    string RelativePath,
    bool IsPreferred,
    IReadOnlyList<string> AcceptedPrefixes)
{
    /// <summary>True when this anchor deploys straight into the install root.</summary>
    public bool IsInstallRoot => string.IsNullOrEmpty(RelativePath);
}

/// <summary>Whether a game's mod loader is present.</summary>
/// <param name="Required">True when the definition declares a loader.</param>
/// <param name="Installed">True when the loader was found.</param>
/// <param name="Version">Detected version, when readable.</param>
/// <param name="MissingFiles">Marker files that were not found.</param>
/// <param name="Message">User-facing explanation.</param>
public sealed record LoaderStatus(
    bool Required,
    bool Installed,
    string? Version,
    IReadOnlyList<string> MissingFiles,
    string Message)
{
    /// <summary>True when mods will silently do nothing because the loader is absent.</summary>
    public bool IsBlocking => Required && !Installed;
}

/// <summary>The set of adapters the engine knows about.</summary>
public interface IGameAdapterRegistry
{
    /// <summary>Every registered adapter, ordered by display name.</summary>
    IReadOnlyList<IGameAdapter> All();

    /// <summary>Looks up an adapter by game id.</summary>
    IGameAdapter? Get(string gameId);

    /// <summary>Registers an adapter, replacing any adapter with the same id.</summary>
    void Register(IGameAdapter adapter);

    /// <summary>Registers or replaces a purely declarative definition.</summary>
    void RegisterDefinition(GameDefinition definition);

    /// <summary>True when an adapter with this id exists.</summary>
    bool Contains(string gameId);

    /// <summary>
    /// Loads user-supplied JSON definitions from the app's Games directory and registers them.
    /// This is the "add a game without shipping a build" path.
    /// </summary>
    /// <param name="directory">Directory to scan.</param>
    /// <param name="logger">Logger for per-file failures.</param>
    IReadOnlyList<string> LoadUserDefinitions(string directory, IAppLogger logger);
}

/// <summary>Game install detection across the supported store probes.</summary>
public interface IGameDetector
{
    /// <summary>
    /// Probes for one definition. Never throws: a probe failure becomes a
    /// <see cref="GameDetectionResult"/> with <c>Found = false</c> and evidence explaining why.
    /// </summary>
    /// <param name="definition">Definition to look for.</param>
    GameDetectionResult Detect(GameDefinition definition);

    /// <summary>Probes every registered definition, for the "scan for games" action.</summary>
    /// <param name="registry">Adapter source.</param>
    IReadOnlyList<GameDetectionResult> DetectAll(IGameAdapterRegistry registry);

    /// <summary>
    /// Validates a directory the user picked by hand, using the definition's required files plus
    /// the adapter's own evidence.
    /// </summary>
    /// <param name="definition">Definition being satisfied.</param>
    /// <param name="installPath">Directory the user selected.</param>
    GameDetectionResult ValidateManualPath(GameDefinition definition, string installPath);
}
