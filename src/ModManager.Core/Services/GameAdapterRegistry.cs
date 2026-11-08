using System.Text.Json;
using ModManager.Core.Serialization;

namespace ModManager.Core.Services;

/// <summary>
/// The set of adapters the engine knows about, including user-supplied definitions.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix's <c>defcat::discover_defs</c>, which merges definitions from three
/// sources with "later wins on the same id": definitions compiled into the binary, then
/// <c>&lt;config&gt;/games/</c>, then installed plugins. Ours mirrors that layering so a user can
/// override a built-in game without editing the application, and a definition distributed later can
/// supersede one shipped today.
/// <para>
/// One deliberate difference: Modrix silently discards a definition that fails to parse, on the
/// grounds that "a broken plugin must not take the whole application down". We keep the resilience
/// but not the silence — a rejected definition produces a log entry naming the file and the reason,
/// because a game that quietly fails to appear is one of the more frustrating things to debug.
/// </para>
/// </remarks>
public sealed class GameAdapterRegistry : IGameAdapterRegistry
{
    /// <summary>Maximum number of definition files scanned, so a huge directory cannot stall startup.</summary>
    public const int MaxDefinitionFiles = 256;

    private readonly Dictionary<string, IGameAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAppLogger? _logger;

    /// <summary>Creates an empty registry.</summary>
    /// <param name="logger">Optional logger for definition-loading diagnostics.</param>
    public GameAdapterRegistry(IAppLogger? logger = null) => _logger = logger;

    /// <inheritdoc />
    public IReadOnlyList<IGameAdapter> All() =>
        _adapters.Values.OrderBy(adapter => adapter.Definition.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <inheritdoc />
    public IGameAdapter? Get(string gameId) =>
        !string.IsNullOrWhiteSpace(gameId) && _adapters.TryGetValue(gameId, out var adapter) ? adapter : null;

    /// <inheritdoc />
    public void Register(IGameAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapters[adapter.GameId] = adapter;
    }

    /// <inheritdoc />
    public void RegisterDefinition(GameDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _adapters[definition.Id] = new GenericGameAdapter(definition);
        _logger?.Debug(
            "Games",
            $"已注册游戏定义 “{definition.Id}”。",
            $"Mod 根目录：“{definition.ModRoot}”，能力：{definition.Capabilities}");
    }

    /// <inheritdoc />
    public bool Contains(string gameId) => !string.IsNullOrWhiteSpace(gameId) && _adapters.ContainsKey(gameId);

    /// <inheritdoc />
    public IReadOnlyList<string> LoadUserDefinitions(string directory, IAppLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(logger);

        var loaded = new List<string>();

        if (!Directory.Exists(Safety.FileSystemOps.ToExtendedPath(directory)))
        {
            return loaded;
        }

        var files = Directory.EnumerateFiles(Safety.FileSystemOps.ToExtendedPath(directory), "*.json", SearchOption.TopDirectoryOnly)
            .Take(MaxDefinitionFiles)
            .ToList();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);

            try
            {
                var definition = JsonSerializer.Deserialize<GameDefinition>(
                    File.ReadAllBytes(file),
                    ManifestJson.Options);

                if (definition is null)
                {
                    logger.Warn("Games", $"游戏定义 “{name}” 无法读取，已跳过。");
                    continue;
                }

                if (Validate(definition, name, logger))
                {
                    RegisterDefinition(definition with { IsUserDefined = true, SourcePath = file });
                    loaded.Add(definition.Id);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                logger.Warn("Games", $"游戏定义 “{name}” 未通过校验，已跳过。", ex.Message);
            }
        }

        if (loaded.Count > 0)
        {
            logger.Info("Games", $"已加载 {loaded.Count} 个自定义游戏定义：{string.Join("、", loaded)}");
        }

        return loaded;
    }

    /// <summary>
    /// Writes a definition to the user games directory so it survives a restart, and registers it.
    /// </summary>
    /// <param name="definition">Definition to persist.</param>
    /// <param name="directory">Target directory (the app's Games folder).</param>
    /// <returns>The absolute file path written.</returns>
    /// <remarks>
    /// This is how a game the user added in generic mode still resolves to an adapter after a restart. The
    /// write goes through a temporary file and a move so a crash mid-write cannot leave a half-written
    /// definition that would be skipped on the next load.
    /// </remarks>
    public string SaveUserDefinition(GameDefinition definition, string directory)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);

        var safeId = string.Concat(definition.Id.Where(character => !Path.GetInvalidFileNameChars().Contains(character)));
        if (safeId.Length == 0)
        {
            throw new ArgumentException("The definition id contains no usable file-name characters.", nameof(definition));
        }

        Directory.CreateDirectory(Safety.FileSystemOps.ToExtendedPath(directory));

        var path = Path.Combine(directory, safeId + ".json");
        var stored = definition with { IsUserDefined = true, SourcePath = path };
        var temporary = path + ".tmp";

        File.WriteAllText(
            Safety.FileSystemOps.ToExtendedPath(temporary),
            JsonSerializer.Serialize(stored, ManifestJson.Options));

        File.Move(Safety.FileSystemOps.ToExtendedPath(temporary), Safety.FileSystemOps.ToExtendedPath(path), overwrite: true);

        RegisterDefinition(stored);
        return path;
    }

    /// <summary>Validates a user-supplied definition, reporting every problem it can find.</summary>    /// <param name="definition">Definition to validate.</param>
    /// <param name="sourceName">File name, for messages.</param>
    /// <param name="logger">Logger.</param>
    /// <remarks>
    /// Modrix validates <c>api_version</c>, non-empty <c>id</c>/<c>name</c>, the load-order strategy,
    /// <c>external_scan</c> kinds, <c>mod_base</c> and registry hives. The spirit we keep is that a
    /// definition is rejected with a specific complaint rather than half-honoured.
    /// </remarks>
    public static bool Validate(GameDefinition definition, string sourceName, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(logger);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            problems.Add("'id' must not be empty");
        }
        else if (definition.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            problems.Add("'id' contains characters that are not valid in a file name");
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            problems.Add("'name' must not be empty");
        }

        // api_version is a schema gate: refuse a definition we do not understand rather than
        // misreading it. Modrix does the same, and states why — a definition written for a newer
        // engine could otherwise have its capabilities silently dropped.
        if (definition.ApiVersion is < 1 or > 2)
        {
            problems.Add($"unsupported api_version {definition.ApiVersion} (this build supports 1..2)");
        }

        if (definition.LoadOrder is { } loadOrder
            && loadOrder.Strategy == LoadOrderStrategy.PluginsTxt
            && string.IsNullOrWhiteSpace(loadOrder.AppDataDirectory)
            && string.IsNullOrWhiteSpace(loadOrder.ExplicitPath))
        {
            problems.Add("load_order with the plugins_txt strategy needs 'appDataDirectory' or 'explicitPath'");
        }

        if (definition.ModRootInUserProfile && string.IsNullOrWhiteSpace(definition.ModRoot))
        {
            problems.Add("'modRootInUserProfile' is set but 'modRoot' is empty, so the mod root would be the whole Documents folder");
        }

        foreach (var scan in definition.ExternalScans)
        {
            if (scan.Kind is not ("file" or "folder"))
            {
                problems.Add($"external_scan kind '{scan.Kind}' must be 'file' or 'folder'");
            }

            if (scan.Kind == "file" && scan.Extensions.Count == 0)
            {
                problems.Add("an external_scan of kind 'file' needs at least one extension");
            }
        }

        if (definition.DeploymentPreference.Count == 0)
        {
            problems.Add("'deploymentPreference' is empty, so there is no way to place a file");
        }

        if (problems.Count == 0)
        {
            return true;
        }

        logger.Warn(
            "Games",
            $"游戏定义 “{sourceName}” 未通过校验。",
            string.Join("; ", problems));
        return false;
    }
}
