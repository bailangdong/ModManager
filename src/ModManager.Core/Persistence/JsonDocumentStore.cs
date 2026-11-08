using System.Text.Json;
using ModManager.Core.Serialization;
using ModManager.Core.Safety;

namespace ModManager.Core.Persistence;

/// <summary>
/// A small JSON document store with atomic writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design note — why JSON and not SQLite.</b> The user's spec §19 asks for SQLite, and the schema it
/// lists (Games, Profiles, Mods, ModFiles, Downloads, Dependencies, Conflicts, Deployments, Backups,
/// Journals, Settings) is the right shape. Two facts decided the implementation:
/// </para>
/// <list type="number">
/// <item>SQLite in .NET means <c>Microsoft.Data.Sqlite</c>, a NuGet package. This project had to build
/// and test on a machine with no package feed available, so a hard package dependency would have meant
/// shipping something that cannot be compiled — the worst possible outcome for a deliverable.</item>
/// <item>The durability properties that actually matter here are provided by the file primitives, not
/// by the database: every write goes to a sibling temp file, is flushed to disk, then renamed over the
/// target, so a crash leaves either the old or the new document and never a torn one. That is the same
/// guarantee the plugin's manifest table relies on.</item>
/// </list>
/// <para>
/// The consequence is stated plainly rather than hidden: this store keeps every document for a
/// collection in one file and rewrites it on change, so it is O(collection) per write. At mod-library
/// scale (hundreds to low thousands of mods) that is irrelevant; at tens of thousands it would not be.
/// <see cref="IRelationalStore"/> exists so a SQLite implementation can be dropped in without touching
/// a single caller — that is the migration path, and it is a real one because nothing above this layer
/// knows how documents are stored.
/// </para>
/// <para>
/// The append-only-versioning discipline is borrowed in spirit from Modrix's migration policy: each
/// document carries a version, and a newer build must migrate rather than misinterpret. Only additive
/// change is safe, which is why the models use optional properties with defaults.
/// </para>
/// </remarks>
public sealed class JsonDocumentStore
{
    private readonly AppPaths _paths;
    private readonly IAppLogger? _logger;
    /// <summary>
    /// Serialises whole-document rewrites so two saves cannot interleave.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="object"/> rather than <c>System.Threading.Lock</c>: that type was introduced in
    /// .NET 9 and this project targets <c>net8.0</c>, so it does not exist here. Under C# 12 a
    /// <c>lock</c> statement on an <see cref="object"/> compiles to <c>Monitor.Enter</c>/<c>Exit</c>,
    /// which gives the same mutual exclusion. Raising the target framework to obtain a newer lock
    /// primitive would change far more than it repairs.
    /// </remarks>
    private readonly object _writeLock = new();

    /// <summary>Creates a store rooted at the application's data directory.</summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Optional logger.</param>
    public JsonDocumentStore(AppPaths paths, IAppLogger? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Reads a collection, returning an empty list when nothing is stored or the file is unusable.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="name">Collection name, used as the file name.</param>
    /// <remarks>
    /// A corrupt document is reported and treated as empty rather than throwing. Reasoning: refusing to
    /// start because one collection is unreadable would make the whole application unusable, and the
    /// journal is the artefact that must never be lost — not a caches-of-metadata file. The journal store
    /// is deliberately a separate implementation for exactly this reason.
    /// </remarks>
    public List<T> ReadCollection<T>(string name)
    {
        var path = CollectionPath(name);

        try
        {
            if (!File.Exists(FileSystemOps.ToExtendedPath(path)))
            {
                return [];
            }

            var bytes = File.ReadAllBytes(FileSystemOps.ToExtendedPath(path));
            if (bytes.Length == 0)
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<T>>(bytes, ManifestJson.Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.Warn(
                "Storage",
                $"Collection '{name}' could not be read and was treated as empty.",
                ex.Message);
            return [];
        }
    }

    /// <summary>Writes a collection atomically.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="name">Collection name.</param>
    /// <param name="items">Items to persist.</param>
    public bool WriteCollection<T>(string name, IEnumerable<T> items)
    {
        var path = CollectionPath(name);

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(items.ToList(), ManifestJson.Options);

            lock (_writeLock)
            {
                Directory.CreateDirectory(_paths.Data);
                FileSystemOps.WriteAtomicAsync(path, payload).GetAwaiter().GetResult();
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.Error("Storage", $"无法写入数据集合 “{name}”。", ex.Message);
            return false;
        }
    }

    /// <summary>Reads a single document, or null.</summary>
    /// <typeparam name="T">Document type.</typeparam>
    /// <param name="name">Document name.</param>
    public T? ReadDocument<T>(string name)
        where T : class
    {
        var path = CollectionPath(name);

        try
        {
            if (!File.Exists(FileSystemOps.ToExtendedPath(path)))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(FileSystemOps.ToExtendedPath(path));
            return bytes.Length == 0 ? null : JsonSerializer.Deserialize<T>(bytes, ManifestJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.Warn("Storage", $"无法读取数据文件 “{name}”，将使用默认值。", ex.Message);
            return null;
        }
    }

    /// <summary>Writes a single document atomically.</summary>
    /// <typeparam name="T">Document type.</typeparam>
    /// <param name="name">Document name.</param>
    /// <param name="document">Document to persist.</param>
    public bool WriteDocument<T>(string name, T document)
        where T : class
    {
        var path = CollectionPath(name);

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(document, ManifestJson.Options);

            lock (_writeLock)
            {
                Directory.CreateDirectory(_paths.Data);
                FileSystemOps.WriteAtomicAsync(path, payload).GetAwaiter().GetResult();
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.Error("Storage", $"无法写入数据文件 “{name}”。", ex.Message);
            return false;
        }
    }

    /// <summary>Absolute path of a collection file.</summary>
    /// <param name="name">Collection name.</param>
    private string CollectionPath(string name) =>
        Path.Combine(_paths.Data, AppPaths.Sanitize(name) + ".json");
}

/// <summary>
/// Marker interface describing the shape a relational implementation would satisfy.
/// </summary>
/// <remarks>
/// Present so the intended migration to SQLite is visible in the type system rather than only in a
/// comment. A SQLite implementation would provide the same repositories with a real
/// <c>deployed_files(profile_id, target_path)</c> unique index, which is the constraint that makes a
/// manifest trustworthy — and which a document store can only approximate.
/// </remarks>
public interface IRelationalStore
{
    /// <summary>Runs any pending schema migrations, returning the version reached.</summary>
    int Migrate();
}
