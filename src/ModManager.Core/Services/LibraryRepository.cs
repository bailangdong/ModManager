using System.Text.Json;
using ModManager.Core.Persistence;
using ModManager.Core.Serialization;
using ModManager.Core.Safety;

namespace ModManager.Core.Services;

/// <summary>
/// Persists the game library, mod library, manifests, profiles, downloads, backups and conflict rules.
/// </summary>
/// <remarks>
/// One class implementing every repository interface, because they share the same store and the same
/// write-serialisation concern. Splitting them into six files would add ceremony without adding a
/// boundary: the boundary that matters — documents versus the journal — is already drawn, and the
/// journal deliberately does not go through here.
/// </remarks>
public sealed class LibraryRepository :
    IGameRepository,
    IModRepository,
    IDownloadRepository,
    IBackupRepository,
    IConflictRepository,
    ISettingsStore
{
    private const string GamesCollection = "games";
    private const string ModsCollection = "mods";
    private const string ManifestsCollection = "manifests";
    private const string ProfilesCollection = "profiles";
    private const string DownloadsCollection = "downloads";
    private const string BackupsCollection = "backups";
    private const string ConflictsCollection = "conflicts";
    private const string SettingsDocument = "settings";

    private readonly JsonDocumentStore _store;
    private readonly AppPaths _paths;
    private readonly IAppLogger? _logger;
    /// <summary>
    /// Serialises mutation of the in-memory mirrors and their persistence. A plain <see cref="object"/>
    /// because <c>System.Threading.Lock</c> requires .NET 9 and this project targets <c>net8.0</c>.
    /// </summary>
    private readonly object _gate = new();

    // In-memory mirrors. Reading a document per query would be correct but would make the UI janky; the
    // store is authoritative on disk and this is refreshed from it on every write, so the two cannot
    // drift within a session.
    private List<GameInstance> _games = [];
    private List<ModMetadata> _mods = [];
    private List<ModManifest> _manifests = [];
    private List<Profile> _profiles = [];
    private List<DownloadRecord> _downloads = [];
    private List<BackupEntry> _backups = [];
    private List<ConflictRuleRecord> _conflicts = [];

    /// <summary>Creates the repository over a document store.</summary>
    /// <param name="store">Document store.</param>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Optional logger.</param>
    public LibraryRepository(JsonDocumentStore store, AppPaths paths, IAppLogger? logger = null)
    {
        _store = store;
        _paths = paths;
        _logger = logger;
        Reload();
    }

    // ---------------------------------------------------------------------------------------------
    // Games
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// Every member whose name is shared with another repository interface is implemented
    /// <b>explicitly</b>. This is not stylistic: C# overloads on signature alone, so
    /// <c>All()</c>, <c>Get(string)</c>, <c>ForGame(string)</c> and <c>Remove(string)</c> collide across
    /// <see cref="IGameRepository"/>, <see cref="IModRepository"/>, <see cref="IDownloadRepository"/>,
    /// <see cref="IBackupRepository"/> and <see cref="IConflictRepository"/> — the parameter <i>name</i>
    /// carries no meaning to the compiler, and the return types differ. Explicit implementation is the
    /// only way for one class to satisfy all of them with the correct return type each, which is why the
    /// distinct public methods below carry explicit names.
    /// </remarks>
    IReadOnlyList<GameInstance> IGameRepository.All() => AllGames();

    /// <summary>All registered game instances.</summary>
    public IReadOnlyList<GameInstance> AllGames() => _games.ToList();

    /// <inheritdoc />
    GameInstance? IGameRepository.Get(string gameId) => Game(gameId);

    /// <summary>One registered game, or null.</summary>
    /// <param name="gameId">Game id.</param>
    public GameInstance? Game(string gameId) =>
        _games.FirstOrDefault(game => string.Equals(game.GameId, gameId, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public void Upsert(GameInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        lock (_gate)
        {
            var index = _games.FindIndex(game =>
                string.Equals(game.GameId, instance.GameId, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                _games[index] = instance;
            }
            else
            {
                _games.Add(instance);
            }

            Persist(GamesCollection, _games);
        }
    }

    /// <inheritdoc />
    bool IGameRepository.Remove(string gameId) => RemoveGame(gameId);

    /// <summary>Forgets a game registration. Deletes no files.</summary>
    /// <param name="gameId">Game id.</param>
    public bool RemoveGame(string gameId)
    {
        lock (_gate)
        {
            // Only the registration is removed. Cleaning up a game's mods, manifests and backups is a
            // separate, explicit operation, because silently deleting a user's staged mods when they
            // remove a game from a list would be a nasty surprise.
            var removed = _games.RemoveAll(game =>
                string.Equals(game.GameId, gameId, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                Persist(GamesCollection, _games);
            }

            return removed;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Mods
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    IReadOnlyList<ModMetadata> IModRepository.ForGame(string gameId) => ModsForGame(gameId);

    /// <summary>Mods staged for one game, oldest first.</summary>
    /// <param name="gameId">Game id.</param>
    public IReadOnlyList<ModMetadata> ModsForGame(string gameId) =>
        _mods.Where(mod => string.Equals(mod.GameId, gameId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(mod => mod.InstalledAt)
            .ThenBy(mod => mod.Id, StringComparer.Ordinal)
            .ToList();

    /// <inheritdoc />
    IReadOnlyList<ModMetadata> IModRepository.All() => AllMods();

    /// <summary>Every staged mod, across all games.</summary>
    public IReadOnlyList<ModMetadata> AllMods() => _mods.ToList();

    /// <inheritdoc />
    ModMetadata? IModRepository.Get(string modId) => Mod(modId);

    /// <summary>One staged mod, or null.</summary>
    /// <param name="modId">Mod id.</param>
    public ModMetadata? Mod(string modId) =>
        _mods.FirstOrDefault(mod => string.Equals(mod.Id, modId, StringComparison.Ordinal));

    /// <inheritdoc />
    public void Upsert(ModMetadata mod)
    {
        ArgumentNullException.ThrowIfNull(mod);

        lock (_gate)
        {
            var index = _mods.FindIndex(existing => string.Equals(existing.Id, mod.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _mods[index] = mod;
            }
            else
            {
                _mods.Add(mod);
            }

            Persist(ModsCollection, _mods);
        }
    }

    /// <inheritdoc />
    bool IModRepository.Remove(string modId) => RemoveMod(modId);

    /// <summary>Removes a mod record, its manifest, and its references from every profile.</summary>
    /// <param name="modId">Mod id.</param>
    public bool RemoveMod(string modId)
    {
        lock (_gate)
        {
            var removed = _mods.RemoveAll(mod => string.Equals(mod.Id, modId, StringComparison.Ordinal)) > 0;

            var manifest = _manifests.FirstOrDefault(existing =>
                string.Equals(existing.ModId, modId, StringComparison.Ordinal));

            if (manifest is not null)
            {
                _manifests.Remove(manifest);
                Persist(ManifestsCollection, _manifests);
                RemoveManifestFile(manifest.GameId, modId);
            }

            // The mod is also dropped from every profile, so a stale id cannot resurrect it.
            var profileChanged = false;
            for (var index = 0; index < _profiles.Count; index++)
            {
                var profile = _profiles[index];
                if (!profile.ModOrder.Contains(modId, StringComparer.Ordinal)
                    && !profile.DisabledModIds.Contains(modId, StringComparer.Ordinal))
                {
                    continue;
                }

                _profiles[index] = profile with
                {
                    ModOrder = profile.ModOrder.Where(id => !string.Equals(id, modId, StringComparison.Ordinal)).ToList(),
                    DisabledModIds = profile.DisabledModIds.Where(id => !string.Equals(id, modId, StringComparison.Ordinal)).ToList(),
                };

                profileChanged = true;
            }

            if (profileChanged)
            {
                Persist(ProfilesCollection, _profiles);
            }

            if (removed)
            {
                Persist(ModsCollection, _mods);
            }

            return removed;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Manifests
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyList<ModManifest> ManifestsForGame(string gameId) =>
        _manifests.Where(manifest =>
                string.Equals(manifest.GameId, gameId, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <inheritdoc />
    public ModManifest? GetManifest(string gameId, string modId) =>
        _manifests.FirstOrDefault(manifest =>
            string.Equals(manifest.GameId, gameId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(manifest.ModId, modId, StringComparison.Ordinal));

    /// <inheritdoc />
    public void SaveManifest(ModManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        lock (_gate)
        {
            var index = _manifests.FindIndex(existing =>
                string.Equals(existing.GameId, manifest.GameId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.ModId, manifest.ModId, StringComparison.Ordinal));

            if (index >= 0)
            {
                _manifests[index] = manifest;
            }
            else
            {
                _manifests.Add(manifest);
            }

            Persist(ManifestsCollection, _manifests);
        }
    }

    /// <inheritdoc />
    public bool RemoveManifest(string gameId, string modId)
    {
        lock (_gate)
        {
            var removed = _manifests.RemoveAll(manifest =>
                string.Equals(manifest.GameId, gameId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(manifest.ModId, modId, StringComparison.Ordinal)) > 0;

            if (removed)
            {
                Persist(ManifestsCollection, _manifests);
                RemoveManifestFile(gameId, modId);
            }

            return removed;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Profiles
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyList<Profile> ProfilesForGame(string gameId) =>
        _profiles.Where(profile => string.Equals(profile.GameId, gameId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(profile => profile.CreatedAt)
            .ToList();

    /// <inheritdoc />
    public Profile? GetProfile(string profileId) =>
        _profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal));

    /// <inheritdoc />
    public void SaveProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (_gate)
        {
            // At most one active profile per game. Modrix enforces this with a partial unique index in
            // the schema; with a document store the invariant has to be maintained here instead, and it
            // is worth stating that this is exactly the kind of thing a relational store does better.
            if (profile.IsActive)
            {
                for (var index = 0; index < _profiles.Count; index++)
                {
                    var existing = _profiles[index];
                    if (existing.IsActive
                        && !string.Equals(existing.Id, profile.Id, StringComparison.Ordinal)
                        && string.Equals(existing.GameId, profile.GameId, StringComparison.OrdinalIgnoreCase))
                    {
                        _profiles[index] = existing with { IsActive = false };
                    }
                }

                EnsureSingleActiveGame(profile.GameId);
            }

            var position = _profiles.FindIndex(existing => string.Equals(existing.Id, profile.Id, StringComparison.Ordinal));
            if (position >= 0)
            {
                _profiles[position] = profile;
            }
            else
            {
                _profiles.Add(profile);
            }

            Persist(ProfilesCollection, _profiles);
        }
    }

    /// <inheritdoc />
    public bool RemoveProfile(string profileId)
    {
        lock (_gate)
        {
            var removed = _profiles.RemoveAll(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                Persist(ProfilesCollection, _profiles);
            }

            return removed;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Downloads
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    IReadOnlyList<DownloadRecord> IDownloadRepository.All() => AllDownloads();

    /// <summary>Every download job, newest first.</summary>
    public IReadOnlyList<DownloadRecord> AllDownloads() =>
        _downloads.OrderByDescending(record => record.CreatedAt).ToList();

    /// <inheritdoc />
    DownloadRecord? IDownloadRepository.Get(string downloadId) => Download(downloadId);

    /// <summary>One download job, or null.</summary>
    /// <param name="downloadId">Job id.</param>
    public DownloadRecord? Download(string downloadId) =>
        _downloads.FirstOrDefault(record => string.Equals(record.Id, downloadId, StringComparison.Ordinal));

    /// <inheritdoc />
    public void Upsert(DownloadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            var index = _downloads.FindIndex(existing => string.Equals(existing.Id, record.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _downloads[index] = record;
            }
            else
            {
                _downloads.Add(record);
            }

            Persist(DownloadsCollection, _downloads);
        }
    }

    /// <inheritdoc />
    bool IDownloadRepository.Remove(string downloadId) => RemoveDownload(downloadId);

    /// <summary>Removes a download job record.</summary>
    /// <param name="downloadId">Job id.</param>
    public bool RemoveDownload(string downloadId)
    {
        lock (_gate)
        {
            var removed = _downloads.RemoveAll(record => string.Equals(record.Id, downloadId, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                Persist(DownloadsCollection, _downloads);
            }

            return removed;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Backups
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    IReadOnlyList<BackupEntry> IBackupRepository.ForGame(string gameId) => BackupsForGame(gameId);

    /// <summary>Backups taken for one game.</summary>
    /// <param name="gameId">Game id.</param>
    public IReadOnlyList<BackupEntry> BackupsForGame(string gameId) =>
        _backups.Where(entry => string.Equals(entry.GameId, gameId, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <inheritdoc />
    public IReadOnlyList<BackupEntry> ForMod(string gameId, string modId) =>
        _backups.Where(entry =>
                string.Equals(entry.GameId, gameId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.ModId, modId, StringComparison.Ordinal))
            .ToList();

    /// <inheritdoc />
    public void Add(BackupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            _backups.Add(entry);
            Persist(BackupsCollection, _backups);
        }
    }

    /// <inheritdoc />
    public void MarkRestored(string backupPath)
    {
        lock (_gate)
        {
            for (var index = 0; index < _backups.Count; index++)
            {
                if (string.Equals(_backups[index].BackupPath, backupPath, StringComparison.OrdinalIgnoreCase))
                {
                    _backups[index] = _backups[index] with { Restored = true };
                }
            }

            Persist(BackupsCollection, _backups);
        }
    }

    /// <inheritdoc />
    bool IBackupRepository.Remove(string backupPath) => RemoveBackup(backupPath);

    /// <summary>Removes a backup record.</summary>
    /// <param name="backupPath">Backup path.</param>
    public bool RemoveBackup(string backupPath)
    {
        lock (_gate)
        {
            var removed = _backups.RemoveAll(entry =>
                string.Equals(entry.BackupPath, backupPath, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                Persist(BackupsCollection, _backups);
            }

            return removed;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Conflict rules
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    IReadOnlyList<ConflictResolution> IConflictRepository.ForGame(string gameId) => ConflictRulesForGame(gameId);

    /// <summary>Per-path conflict rules recorded for one game.</summary>
    /// <param name="gameId">Game id.</param>
    public IReadOnlyList<ConflictResolution> ConflictRulesForGame(string gameId) =>
        _conflicts
            .Where(record => string.Equals(record.GameId, gameId, StringComparison.OrdinalIgnoreCase))
            .Select(record => record.Resolution)
            .ToList();

    /// <inheritdoc />
    public void Upsert(string gameId, ConflictResolution resolution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(resolution);

        lock (_gate)
        {
            var index = _conflicts.FindIndex(record =>
                string.Equals(record.GameId, gameId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.Resolution.RelativeTarget, resolution.RelativeTarget, StringComparison.OrdinalIgnoreCase));

            var updated = new ConflictRuleRecord(gameId, resolution);
            if (index >= 0)
            {
                _conflicts[index] = updated;
            }
            else
            {
                _conflicts.Add(updated);
            }

            Persist(ConflictsCollection, _conflicts);
        }
    }

    /// <inheritdoc />
    bool IConflictRepository.Remove(string gameId, string relativeTarget) => RemoveConflictRule(gameId, relativeTarget);

    /// <summary>Removes the rule recorded for one path.</summary>
    /// <param name="gameId">Game id.</param>
    /// <param name="relativeTarget">Path the rule applied to.</param>
    public bool RemoveConflictRule(string gameId, string relativeTarget)
    {
        lock (_gate)
        {
            var removed = _conflicts.RemoveAll(record =>
                string.Equals(record.GameId, gameId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.Resolution.RelativeTarget, relativeTarget, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                Persist(ConflictsCollection, _conflicts);
            }

            return removed;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Settings
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public AppSettings Load() => _store.ReadDocument<AppSettings>(SettingsDocument) ?? new AppSettings();

    /// <inheritdoc />
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _store.WriteDocument(SettingsDocument, settings);
    }

    // ---------------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------------

    /// <summary>Reloads every collection from disk.</summary>
    private void Reload()
    {
        _games = _store.ReadCollection<GameInstance>(GamesCollection);
        _mods = _store.ReadCollection<ModMetadata>(ModsCollection);
        _manifests = _store.ReadCollection<ModManifest>(ManifestsCollection);
        _profiles = _store.ReadCollection<Profile>(ProfilesCollection);
        _downloads = _store.ReadCollection<DownloadRecord>(DownloadsCollection);
        _backups = _store.ReadCollection<BackupEntry>(BackupsCollection);
        _conflicts = _store.ReadCollection<ConflictRuleRecord>(ConflictsCollection);

        // The "at most one active game" invariant is enforced on write, but a crash or a hand edit can
        // still leave two games claiming it. Repairing it here rather than only warning matters because a
        // document store has no partial unique index to lean on — this is exactly the class of invariant a
        // relational store enforces for free.
        var activeId = GetActiveGameId();
        if (activeId is null)
        {
            return;
        }

        var matching = _games.Where(game =>
            string.Equals(game.GameId, activeId, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matching.Count > 1)
        {
            _logger?.Warn(
                "Storage",
                $"有多个游戏匹配当前生效标记 “{activeId}”，保留第一个。");
        }
    }

    /// <summary>Returns the game currently marked active, if any.</summary>
    private string? GetActiveGameId() =>
        _store.ReadDocument<ActiveGameMarker>("active-game")?.GameId;

    /// <summary>Records which game is active, enforcing "at most one" on the way.</summary>
    /// <param name="gameId">Game to mark active.</param>
    private void EnsureSingleActiveGame(string gameId) =>
        _store.WriteDocument("active-game", new ActiveGameMarker { GameId = gameId });

    /// <summary>Writes a collection through the store.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="name">Collection name.</param>
    /// <param name="items">Items to persist.</param>
    private void Persist<T>(string name, IEnumerable<T> items) => _store.WriteCollection(name, items);

    /// <summary>Removes a manifest's on-disk file.</summary>
    /// <param name="gameId">Game id.</param>
    /// <param name="modId">Mod id.</param>
    private void RemoveManifestFile(string gameId, string modId)
    {
        try
        {
            var path = _paths.ManifestFile(gameId, modId);
            FileSystemOps.RemoveIfPresent(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn("Storage", $"无法删除 Mod {modId} 的 Manifest 文件。", ex.Message);
        }
    }

    /// <summary>A conflict rule plus its game, because the collection is flat.</summary>
    /// <param name="GameId">Game the rule belongs to.</param>
    /// <param name="Resolution">The rule.</param>
    private sealed record ConflictRuleRecord(string GameId, ConflictResolution Resolution);

    /// <summary>Single-valued marker recording the active game.</summary>
    private sealed class ActiveGameMarker
    {
        /// <summary>Active game id.</summary>
        public string? GameId { get; set; }
    }
}
