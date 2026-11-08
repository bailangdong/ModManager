using System.IO;
using System.IO.Compression;
using System.Text;
using ModManager.Core;
using ModManager.Core.Persistence;
using ModManager.Core.Safety;
using ModManager.Core.Services;

namespace ModManager.Tests;

/// <summary>
/// Shared test support: a synthetic game definition, a fake adapter, archive builders, and the
/// fully wired engine pipeline (the same object graph <c>ApplicationHost</c> builds, minus WPF).
/// </summary>
/// <remarks>
/// The engine must stay game-agnostic, so the tests never use a real game: <see cref="TestGameAdapter"/>
/// is the fake implementation the generic pipeline is validated against.
/// </remarks>
public static class TestGame
{
    /// <summary>The synthetic definition every adapter-level test uses.</summary>
    public static GameDefinition Definition() => new()
    {
        Id = "testgame",
        Name = "Test Game",
        Description = "A synthetic game used to validate the generic adapter pipeline.",
        ModRoot = "Mods",
        Capabilities =
            GameCapability.HardlinkDeployment
            | GameCapability.SymlinkDeployment
            | GameCapability.Profiles
            | GameCapability.FileConflicts
            | GameCapability.Dependencies,
        DeploymentPreference = [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy],
        ContentDirectories = ["mods", "plugins", "data", "config"],
        ModFileExtensions = ["dll", "ini", "json", "txt", "pak", "lua"],
        StripPrefixes = ["Mods"],
    };
}

/// <summary>
/// The fake adapter used by tests: the generic adapter over a synthetic definition. Proves the
/// two-tier model — a definition plus the generic adapter is a complete game implementation.
/// </summary>
public sealed class TestGameAdapter : GenericGameAdapter
{
    /// <summary>Creates the adapter for the synthetic test game.</summary>
    public TestGameAdapter()
        : base(TestGame.Definition())
    {
    }
}

/// <summary>Builds minimal, fully controlled test archives locally.</summary>
/// <remarks>
/// No fixture is downloaded from the network. Zip archives are produced here with the BCL. 7z
/// fixtures are generated locally with the py7zr tool and checked into <c>Fixtures/</c>, so their
/// contents are exactly known; an encrypted 7z fixture is included for the refusal path. RAR has no
/// local writer (SharpCompress reads RAR but cannot create it), so RAR coverage is limited to
/// signature detection and the corrupt-archive error path — stated honestly rather than faked.
/// </remarks>
public static class TestArchives
{
    /// <summary>The directory holding pre-generated 7z fixtures, copied to the test output.</summary>
    public static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>Creates a zip at <c>tree.Root/name</c> with the given entries.</summary>
    public static string CreateZip(TemporaryTree tree, string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(tree.Root, name);
        using (var stream = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (entryPath, content) in entries)
            {
                var entry = zip.CreateEntry(entryPath);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return path;
    }

    /// <summary>Creates a file carrying a RAR signature followed by garbage, for detection and error tests.</summary>
    public static string CreateCorruptRar(TemporaryTree tree, string name)
    {
        var path = Path.Combine(tree.Root, name);
        var bytes = new byte[64];
        bytes[0] = 0x52; // R
        bytes[1] = 0x61; // a
        bytes[2] = 0x72; // r
        bytes[3] = 0x21; // !
        bytes[4] = 0x1A;
        bytes[5] = 0x07;
        bytes[6] = 0x00;
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

/// <summary>
/// Builds the whole engine pipeline wired like <c>ApplicationHost.CreateAsync</c>, over a temporary
/// tree, so the generic flow can be exercised end to end without a message pump.
/// </summary>
public sealed class TestHost : IDisposable
{
    private readonly bool _ownsTree;

    private TestHost(TemporaryTree tree, AppPaths paths, LibraryRepository library, bool ownsTree)
    {
        Tree = tree;
        Paths = paths;
        Logger = new AppLogger();
        Library = library;
        Settings = new SettingsCarrier { Settings = new AppSettings() };
        _ownsTree = ownsTree;

        PathSafety = new PathSafetyService(paths, Settings.Settings.Safety, Logger);
        Conflicts = new ConflictService();
        Planner = new DeploymentPlanner(PathSafety, Logger);
        Journals = new JournalService(paths, Logger);
        Verify = new VerifyService();
        Dependencies = new DependencyService();
        Archives = new ArchiveReader();
        Analyzer = new ModAnalyzer();
        Applier = new DeploymentApplier(PathSafety, Journals, paths, Library);

        Install = new ModInstallService(
            paths,
            Archives,
            Analyzer,
            Planner,
            Applier,
            Verify,
            Dependencies,
            Library,
            Library,
            Library,
            PathSafety,
            Settings);
    }

    /// <summary>The throwaway tree everything lives under.</summary>
    public TemporaryTree Tree { get; }

    /// <summary>Application data locations.</summary>
    public AppPaths Paths { get; }

    /// <summary>Logger.</summary>
    public AppLogger Logger { get; }

    /// <summary>Library persistence.</summary>
    public LibraryRepository Library { get; }

    /// <summary>Live settings carrier.</summary>
    public SettingsCarrier Settings { get; }

    /// <summary>Path safety gate.</summary>
    public PathSafetyService PathSafety { get; }

    /// <summary>Pure planner.</summary>
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

    /// <summary>Archive reader.</summary>
    public ArchiveReader Archives { get; }

    /// <summary>Root analyzer.</summary>
    public ModAnalyzer Analyzer { get; }

    /// <summary>Install pipeline.</summary>
    public ModInstallService Install { get; }

    /// <summary>Builds a fresh host, each over its own tree.</summary>
    public static TestHost Create()
    {
        var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();
        var library = new LibraryRepository(new JsonDocumentStore(paths), paths);
        return new TestHost(tree, paths, library, ownsTree: true);
    }

    /// <summary>
    /// Builds a host over an existing tree. Reusing the same tree across hosts simulates an
    /// application restart: a brand-new object graph reading the same persisted state.
    /// </summary>
    /// <param name="tree">Tree to attach to. Ownership stays with the caller.</param>
    public static TestHost Create(TemporaryTree tree)
    {
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();
        var library = new LibraryRepository(new JsonDocumentStore(paths), paths);
        return new TestHost(tree, paths, library, ownsTree: false);
    }

    /// <summary>Creates the fake game install directory and returns its absolute path.</summary>
    public string CreateGameRoot() => Tree.Dir("game");

    /// <inheritdoc />
    public void Dispose()
    {
        // An attached host borrows its tree from the test; only a host that created its own tree
        // may tear it down, or a "restart" test would lose its persisted state halfway through.
        if (_ownsTree)
        {
            Tree.Dispose();
        }
    }
}
