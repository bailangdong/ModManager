namespace ModManager.Core;

/// <summary>
/// Where the application keeps its own data. Never inside a game folder.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix follows platform standard directories and exposes them via
/// <c>modrix paths</c>: a data dir (database + staged library), a config dir, and a cache dir.
/// Separating staging from the game folder is what makes "no destructive edit until deploy"
/// possible, and it is why <see cref="PathSafetyService"/> can treat the whole app data tree as a
/// legitimate root while refusing everything else.
/// </remarks>
public sealed class AppPaths
{
    /// <summary>
    /// Creates the path set for a given root. The default is a portable data directory next to the
    /// executable (<c>&lt;exe dir&gt;\Data</c>), so the application never writes outside its own
    /// folder — no <c>%APPDATA%</c>, no registry, nothing elsewhere on the machine.
    /// </summary>
    /// <param name="rootOverride">Explicit root (used by tests and embedding hosts).</param>
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(AppContext.BaseDirectory, "Data");

        Mods = Path.Combine(Root, "Mods");
        Downloads = Path.Combine(Root, "Downloads");
        Library = Path.Combine(Root, "Library");
        Backups = Path.Combine(Root, "Backups");
        Journals = Path.Combine(Root, "Journals");
        Manifests = Path.Combine(Root, "Manifests");
        Data = Path.Combine(Root, "Data");
        Cache = Path.Combine(Root, "Cache");
        Logs = Path.Combine(Root, "Logs");
        Games = Path.Combine(Root, "Games");
        ConfigFile = Path.Combine(Root, "settings.json");
    }

    /// <summary>Application data root.</summary>
    public string Root { get; }

    /// <summary>Staging root: one directory per mod version.</summary>
    public string Mods { get; }

    /// <summary>Download cache. Archives land here, never in the game folder.</summary>
    public string Downloads { get; }

    /// <summary>Content-addressed package library shared by every acquisition source.</summary>
    public string Library { get; }

    /// <summary>Backups of game files we overwrote.</summary>
    public string Backups { get; }

    /// <summary>Transaction journals, one file per transaction.</summary>
    public string Journals { get; }

    /// <summary>Committed manifests, one file per mod.</summary>
    public string Manifests { get; }

    /// <summary>JSON databases (games, profiles, downloads, conflicts, backups, settings).</summary>
    public string Data { get; }

    /// <summary>Cover art and other disposable caches.</summary>
    public string Cache { get; }

    /// <summary>Log files.</summary>
    public string Logs { get; }

    /// <summary>User-supplied game definitions.</summary>
    public string Games { get; }

    /// <summary>Settings file path.</summary>
    public string ConfigFile { get; }

    /// <summary>Every directory the app owns, for creation and safety allow-listing.</summary>
    public IReadOnlyList<string> AllDirectories =>
        [Root, Mods, Downloads, Library, Backups, Journals, Manifests, Data, Cache, Logs, Games];

    /// <summary>Creates the whole tree if missing. Idempotent.</summary>
    public void EnsureCreated()
    {
        foreach (var directory in AllDirectories)
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>Absolute path to a journal file.</summary>
    public string JournalFile(string transactionId) => Path.Combine(Journals, transactionId + ".json");

    /// <summary>Absolute path to a mod's manifest file.</summary>
    public string ManifestFile(string gameId, string modId) =>
        Path.Combine(Manifests, gameId, modId + ".json");

    /// <summary>Absolute path to a download's partial file.</summary>
    public string PartialFile(string downloadId) => Path.Combine(Downloads, downloadId + ".part");

    /// <summary>
    /// Backup directory for one mod's overwrite of one game, timestamped so a whole set can be
    /// discarded at once and so two runs never collide.
    /// </summary>
    public string BackupDirectory(string gameId, string modId, DateTimeOffset stamp) =>
        Path.Combine(Backups, gameId, $"{stamp:yyyyMMdd-HHmmss}-{Sanitize(modId)}");

    /// <summary>Strips characters that are illegal in a file name.</summary>
    public static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            buffer[length++] = Array.IndexOf(invalid, ch) >= 0 ? '_' : ch;
        }

        var result = new string(buffer, 0, length).Trim();
        return result.Length == 0 ? "unnamed" : result;
    }
}
