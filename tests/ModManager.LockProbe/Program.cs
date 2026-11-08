using System.Security.Cryptography;
using System.Text;
using ModManager.Core;
using ModManager.Core.Persistence;
using ModManager.Core.Safety;
using ModManager.Core.Services;

// Test-only child process. It either holds the cross-process lock for a directory or writes a
// journal + filesystem state and exits abruptly, so the parent can test real process-exit recovery.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: ModManager.LockProbe <hold|crash-partial|crash-delete|crash-overwrite> ...");
    return 2;
}

switch (args[0])
{
    case "hold" when args.Length >= 3:
    {
        var path = args[1];
        var holdMs = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        var gate = new InstallationGate(crossProcess: true, crossProcessTimeout: TimeSpan.FromSeconds(10));
        using var lease = await gate.AcquireAsync(path, "lock-probe", CancellationToken.None);
        Console.WriteLine("locked");
        Console.Out.Flush();
        await Task.Delay(holdMs);
        return 0;
    }

    case "crash-partial" when args.Length >= 3:
        CrashPartial(args[1], args[2]);
        return 42;

    case "crash-delete" when args.Length >= 3:
        CrashDelete(args[1], args[2]);
        return 42;

    case "crash-overwrite" when args.Length >= 3:
        CrashOverwrite(args[1], args[2]);
        return 42;

    case "apply" when args.Length >= 4:
        await ApplyUntilCheckpoint(args[1], args[2], args[3]);
        return 0;

    default:
        Console.Error.WriteLine("unknown command");
        return 2;
}

static void CrashPartial(string appData, string gameRoot)
{
    var paths = OpenPaths(appData);
    var journals = new JournalService(paths, new AppLogger());
    var target = Path.Combine(gameRoot, "Mods", "plugins", "partial.dll");
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

    var journal = journals.Open("testgame", "probe", "probe-partial", ["probe-mod"]);
    journals.RecordUndo(journal, new RemovePlacedFileUndo(target, Hash("PART"), "probe-mod"));
    journals.Transition(journal, JournalState.Applying);

    File.WriteAllText(target, "PART");
    Environment.Exit(42);
}

static void CrashDelete(string appData, string gameRoot)
{
    var paths = OpenPaths(appData);
    var journals = new JournalService(paths, new AppLogger());
    var source = Path.Combine(gameRoot, "Mods", "plugins", "source.dll");
    var target = Path.Combine(gameRoot, "Mods", "plugins", "deleted.dll");
    Directory.CreateDirectory(Path.GetDirectoryName(source)!);
    File.WriteAllText(source, "SOURCE");

    var journal = journals.Open("testgame", "probe", "probe-delete", ["probe-mod"]);
    journals.RecordUndo(journal, new RestoreDeletedFileUndo(target, source));
    journals.Transition(journal, JournalState.Applying);

    File.Delete(target);
    Environment.Exit(42);
}

static void CrashOverwrite(string appData, string gameRoot)
{
    var paths = OpenPaths(appData);
    var journals = new JournalService(paths, new AppLogger());
    var target = Path.Combine(gameRoot, "Mods", "plugins", "overwritten.dll");
    var backup = Path.Combine(paths.Backups, "testgame", "probe-backup", "overwritten.dll");
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
    File.WriteAllText(backup, "ORIGINAL");
    File.WriteAllText(target, "MODDED");

    var journal = journals.Open("testgame", "probe", "probe-overwrite", ["probe-mod"]);
    journals.RecordUndo(journal, new RestoreOverwrittenFileUndo(target, backup, Hash("ORIGINAL")));
    journals.Transition(journal, JournalState.Applying);

    Environment.Exit(42);
}

/// <summary>
/// Runs the real <see cref="DeploymentApplier"/> over a multi-target BepInEx-like mod and blocks at
/// one checkpoint. The parent test kills this process while it is blocked; no finally/Dispose runs.
/// </summary>
static async Task ApplyUntilCheckpoint(string appData, string gameRoot, string checkpointName)
{
    var paths = OpenPaths(appData);
    var logger = new AppLogger();
    var settings = new AppSettings();
    var safety = new PathSafetyService(paths, settings.Safety, logger);
    var library = new LibraryRepository(new JsonDocumentStore(paths), paths);
    var journals = new JournalService(paths, logger);
    var planner = new DeploymentPlanner(safety, logger);
    var applier = new DeploymentApplier(safety, journals, paths, library, new InstallationGate(crossProcess: false));

    var modRoot = Path.Combine(gameRoot, "BepInEx");
    Directory.CreateDirectory(Path.Combine(modRoot, "plugins"));
    Directory.CreateDirectory(Path.Combine(modRoot, "config"));
    File.WriteAllText(Path.Combine(modRoot, "plugins", "Example.dll"), "ORIGINAL");
    safety.ApproveGameRoot("probe-game", gameRoot, modRoot);

    var staging = Path.Combine(appData, "staging");
    Directory.CreateDirectory(Path.Combine(staging, "plugins"));
    Directory.CreateDirectory(Path.Combine(staging, "config"));
    File.WriteAllText(Path.Combine(staging, "plugins", "Example.dll"), "MODDED");
    File.WriteAllText(Path.Combine(staging, "config", "Example.json"), "{}");

    var definition = new GameDefinition
    {
        Id = "probe-game",
        Name = "Probe",
        ModRoot = "BepInEx",
        InstallTargets =
        [
            new("plugins", "BepInEx/plugins"),
            new("config", "BepInEx/config"),
        ],
    };
    var mod = new ModMetadata
    {
        Id = "probe-mod",
        Name = "Probe Mod",
        GameId = "probe-game",
        StagingPath = staging,
    };
    var profile = new Profile
    {
        Id = "probe-profile",
        Name = "Probe",
        GameId = "probe-game",
        ModOrder = ["probe-mod"],
    };
    var plan = planner.BuildPlan(definition, gameRoot, profile, [new ModSetEntry(mod, null, staging)], []);
    var wanted = Enum.Parse<DeploymentCheckpoint>(checkpointName, ignoreCase: true);

    await applier.ApplyAsync(
        plan,
        definition,
        [new ModSetEntry(mod, null, staging)],
        settings.Deployment,
        logger,
        progress: null,
        cancellationToken: CancellationToken.None,
        checkpoints: new BlockingCheckpoint(wanted));

    Console.Error.WriteLine($"checkpoint '{checkpointName}' was never reached");
    Environment.Exit(3);
}

static AppPaths OpenPaths(string appData)
{
    var paths = new AppPaths(appData);
    paths.EnsureCreated();
    return paths;
}

static string Hash(string content) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

sealed class BlockingCheckpoint(DeploymentCheckpoint wanted) : IDeploymentCheckpointSink
{
    public void Reached(DeploymentCheckpoint checkpoint, string? path)
    {
        if (checkpoint != wanted)
        {
            return;
        }

        Console.WriteLine($"CHECKPOINT:{checkpoint}");
        Console.Out.Flush();
        Thread.Sleep(TimeSpan.FromSeconds(60));
    }
}
