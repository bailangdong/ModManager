using System.Security.Cryptography;
using System.Text;
using ModManager.Core;
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

static AppPaths OpenPaths(string appData)
{
    var paths = new AppPaths(appData);
    paths.EnsureCreated();
    return paths;
}

static string Hash(string content) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
