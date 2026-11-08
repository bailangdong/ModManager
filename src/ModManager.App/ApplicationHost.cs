using System.IO;
using ModManager.Core;
using ModManager.Core.Persistence;
using ModManager.Core.Safety;
using ModManager.Core.Services;

namespace ModManager.App;

/// <summary>
/// The composition root: builds every service once and hands them out.
/// </summary>
/// <remarks>
/// A hand-written container rather than a dependency-injection package, for the same reason the rest of
/// the project has no third-party dependencies: it must build with no package feed. With roughly twenty
/// services the wiring is short enough to read in one sitting, which is itself an argument for writing it
/// out — the dependency graph is visible instead of inferred from attributes.
/// </remarks>
public sealed class ApplicationHost
{
    private ApplicationHost(
        AppPaths paths,
        AppLogger logger,
        SecretStore secrets,
        LibraryRepository library,
        SettingsCarrier settings,
        GameAdapterRegistry adapters,
        PathSafetyService pathSafety,
        DeploymentPlanner planner,
        JournalService journals,
        DeploymentApplier applier,
        ConflictService conflicts,
        VerifyService verify,
        DependencyService dependencies,
        HealthService health,
        ExternalContentService external,
        ArchiveReader archives,
        ModAnalyzer analyzer,
        ModInstallService install,
        PackageLibrary packages,
        PackageInstallService packageInstall,
        LoaderInstaller loaders,
        PackageIngestService ingest,
        DownloadManager downloads,
        NexusProvider nexus,
        IImageCache images,
        ITranslationService translation,
        LocalArgosTranslationEngine localTranslation)
    {
        Paths = paths;
        Logger = logger;
        Secrets = secrets;
        Library = library;
        Settings = settings;
        Adapters = adapters;
        PathSafety = pathSafety;
        Planner = planner;
        Journals = journals;
        Applier = applier;
        Conflicts = conflicts;
        Verify = verify;
        Dependencies = dependencies;
        Health = health;
        External = external;
        Archives = archives;
        Analyzer = analyzer;
        Install = install;
        Packages = packages;
        PackageInstall = packageInstall;
        Loaders = loaders;
        Ingest = ingest;
        Downloads = downloads;
        Nexus = nexus;
        Images = images;
        Translation = translation;
        LocalTranslation = localTranslation;
    }

    /// <summary>Application data locations.</summary>
    public AppPaths Paths { get; }

    /// <summary>Logger with redaction.</summary>
    public AppLogger Logger { get; }

    /// <summary>Secret store for the Nexus credential.</summary>
    public SecretStore Secrets { get; }

    /// <summary>Library persistence.</summary>
    public LibraryRepository Library { get; }

    /// <summary>Live settings, shared with services that read them per operation.</summary>
    public SettingsCarrier Settings { get; }

    /// <summary>Game definitions and adapters.</summary>
    public GameAdapterRegistry Adapters { get; }

    /// <summary>Path safety gate.</summary>
    public PathSafetyService PathSafety { get; }

    /// <summary>Pure deployment planner.</summary>
    public DeploymentPlanner Planner { get; }

    /// <summary>Transaction journal.</summary>
    public JournalService Journals { get; }

    /// <summary>Transactional applier.</summary>
    public DeploymentApplier Applier { get; }

    /// <summary>Conflict detection.</summary>
    public ConflictService Conflicts { get; }

    /// <summary>Deployment verification.</summary>
    public VerifyService Verify { get; }

    /// <summary>Dependency checking.</summary>
    public DependencyService Dependencies { get; }

    /// <summary>Health checks.</summary>
    public HealthService Health { get; }

    /// <summary>External content scanner.</summary>
    public ExternalContentService External { get; }

    /// <summary>Archive reader.</summary>
    public ArchiveReader Archives { get; }

    /// <summary>Mod root analyzer.</summary>
    public ModAnalyzer Analyzer { get; }

    /// <summary>Install pipeline.</summary>
    public ModInstallService Install { get; }

    /// <summary>Local package library shared by every acquisition source.</summary>
    public PackageLibrary Packages { get; }

    /// <summary>Bridge from library packages to the install pipeline.</summary>
    public PackageInstallService PackageInstall { get; }

    /// <summary>BepInEx/MelonLoader installer.</summary>
    public LoaderInstaller Loaders { get; }

    /// <summary>Promotes completed downloads into the package library.</summary>
    public PackageIngestService Ingest { get; }

    /// <summary>Download queue.</summary>
    public DownloadManager Downloads { get; }

    /// <summary>Nexus Mods provider.</summary>
    public NexusProvider Nexus { get; }

    /// <summary>
    /// Local cache for remote images.
    /// </summary>
    /// <remarks>
    /// Covers are fetched and stored by the application rather than by WPF: binding an <c>Image</c> to a remote
    /// URL fails silently, which is why the interface used to fall back to a letter tile.
    /// </remarks>
    public IImageCache Images { get; }

    /// <summary>Translates mod titles and text into Chinese.</summary>
    public ITranslationService Translation { get; }

    /// <summary>
    /// The free local translation engine.
    /// </summary>
    /// <remarks>
    /// Exposed separately from <see cref="Translation"/> because the settings page has to drive its
    /// lifecycle — probe it, install it, and run a test translation — none of which belongs on the
    /// translate-this-mod interface the rest of the application consumes.
    /// </remarks>
    public LocalArgosTranslationEngine LocalTranslation { get; }

    /// <summary>Result of the startup recovery pass, shown once on the Dashboard.</summary>
    public JournalRecoveryReport Recovery { get; private set; } = JournalRecoveryReport.Empty;

    /// <summary>Builds the object graph and repairs any interrupted transaction.</summary>
    public static async Task<ApplicationHost> CreateAsync()
    {
        var paths = new AppPaths();
        paths.EnsureCreated();

        var settingsStore = new JsonDocumentStore(paths);
        var storedSettings = settingsStore.ReadDocument<AppSettings>("settings") ?? new AppSettings();

        // The logger is constructed first so that everything after it can report failures.
        var logger = new AppLogger(paths.Logs, ParseLevel(storedSettings.LogLevel));
        logger.Info("Startup", "ModManager 正在启动。", $"数据目录：{paths.Root}");

        var secrets = new SecretStore(paths.Root, logger);

        var library = new LibraryRepository(new JsonDocumentStore(paths), paths, logger);
        var settings = new SettingsCarrier { Settings = library.Load() };

        // Approved roots are restored from settings before anything can validate a path against them.
        var pathSafety = new PathSafetyService(paths, settings.Settings.Safety, logger);
        foreach (var game in library.AllGames())
        {
            try
            {
                pathSafety.ApproveGameRoot(game.GameId, game.InstallPath, game.ModRootPath);
            }
            catch (InvalidOperationException ex)
            {
                // A previously approved root that is now protected (or missing) must not be re-approved,
                // and must not stop the application from starting either. The game is flagged instead.
                logger.Warn("Safety", $"无法重新批准游戏 “{game.GameId}” 的写入目录。", ex.Message);
            }
        }

        var adapters = new GameAdapterRegistry(logger);
        GameCatalogue.RegisterAll(adapters, paths.Games, logger);

        var conflicts = new ConflictService();
        var planner = new DeploymentPlanner(pathSafety, logger);
        var journals = new JournalService(paths, logger);
        var verify = new VerifyService();
        var dependencies = new DependencyService();
        var external = new ExternalContentService();
        var archives = new ArchiveReader();
        var analyzer = new ModAnalyzer();

        var gate = new InstallationGate();
        var applier = new DeploymentApplier(pathSafety, journals, paths, library, gate);
        var install = new ModInstallService(
            paths,
            archives,
            analyzer,
            planner,
            applier,
            verify,
            dependencies,
            library,
            library,
            library,
            pathSafety,
            settings);

        var packages = new PackageLibrary(paths, logger);
        var installMethods = new InstallMethodDetector();
        var loaders = new LoaderInstaller(paths, archives, gate);
        var packageInstall = new PackageInstallService(
            packages,
            library,
            install,
            adapters,
            archives,
            installMethods,
            loaders,
            logger);
        var ingest = new PackageIngestService(packages, library, logger);

        var health = new HealthService(dependencies, verify, library);

        var downloads = new DownloadManager(library, paths, settings.Settings.Downloads, logger);
        var nexus = new NexusProvider(secrets, settings.Settings.Nexus, logger);
        var images = new ImageCacheService(paths, logger);

        // The local engine is constructed but deliberately not initialised here: installing a runtime and a
        // 67 MB language pack is the user's decision, made from the settings page, not a side effect of
        // launching the application.
        var localTranslation = new LocalArgosTranslationEngine(paths, logger);
        var translation = new TranslationService(
            secrets,
            paths,
            settings.Settings.Translation,
            logger,
            httpClient: null,
            localEngine: localTranslation);

        var host = new ApplicationHost(
            paths,
            logger,
            secrets,
            library,
            settings,
            adapters,
            pathSafety,
            planner,
            journals,
            applier,
            conflicts,
            verify,
            dependencies,
            health,
            external,
            archives,
            analyzer,
            install,
            packages,
            packageInstall,
            loaders,
            ingest,
            downloads,
            nexus,
            images,
            translation,
            localTranslation);

        // Recovery runs before the UI exists, so nothing can observe a half-applied state.
        host.Recovery = await journals.RecoverAsync(logger).ConfigureAwait(false);
        await downloads.RecoverAsync(logger).ConfigureAwait(false);

        ingest.Attach(downloads);
        try
        {
            await ingest.IngestCompletedAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn("Library", "启动时补入库失败，已保留下载文件。", ex.Message);
        }

        if (host.Recovery.HadWork)
        {
            foreach (var message in host.Recovery.Messages)
            {
                logger.Info("Recovery", message);
            }
        }

        return host;
    }

    /// <summary>Persists current settings.</summary>
    public void SaveSettings()
    {
        Library.Save(Settings.Settings);

        // The download manager and Nexus provider were built from the previous snapshot, so their own
        // options are refreshed here rather than requiring a restart.
        Logger.Info("Settings", "设置已保存。");
    }

    /// <summary>Parses a log level name, defaulting to Information.</summary>
    /// <param name="value">Level name.</param>
    private static LogLevel ParseLevel(string? value) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level : LogLevel.Information;
}
