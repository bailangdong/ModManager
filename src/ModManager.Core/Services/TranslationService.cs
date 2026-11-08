using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModManager.Core.Services;

/// <summary>Which engine translates, when more than one is usable.</summary>
public enum TranslationEngineKind
{
    /// <summary>
    /// Prefer the free local engine and fall back to the online endpoint when it is not installed. This is
    /// the default, so a settings file written before the local engine existed keeps working unchanged and
    /// starts using the local engine the moment it is initialised — no key, no migration step.
    /// </summary>
    Auto,

    /// <summary>Only the local Argos engine. Needs no API key and no network.</summary>
    Local,

    /// <summary>Only the configured online endpoint.</summary>
    Online,
}

/// <summary>
/// Configuration for the translation service.
/// </summary>
/// <remarks>
/// Any OpenAI-compatible chat endpoint works, so the user is not tied to one vendor. The default is DeepSeek,
/// which is cheap and handles Chinese well. The key itself never lives here: it goes to the secret store.
/// </remarks>
public sealed record TranslationSettings
{
    /// <summary>True when the user wants titles and text translated.</summary>
    public bool Enabled { get; init; }

    /// <summary>Chat-completions endpoint.</summary>
    public string Endpoint { get; init; } = "https://api.deepseek.com/chat/completions";

    /// <summary>Model name to request.</summary>
    public string Model { get; init; } = "deepseek-chat";

    /// <summary>True when a key is stored, so the interface can say so without reading it.</summary>
    public bool HasKey { get; init; }

    /// <summary>When the service was last reached successfully.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>
    /// Which engine translates. Defaults to <see cref="TranslationEngineKind.Auto"/>, which is also what an
    /// older settings file deserialises to, so nothing has to be migrated and the online path is untouched.
    /// </summary>
    public TranslationEngineKind Engine { get; init; } = TranslationEngineKind.Auto;
}

/// <summary>A translated mod, as shown in the interface.</summary>
/// <param name="Title">Translated title.</param>
/// <param name="Summary">Translated one-line summary.</param>
/// <param name="Description">Translated description.</param>
/// <param name="Model">Model that produced it.</param>
/// <param name="TranslatedAt">When it was produced.</param>
/// <param name="FromCache">True when it came from the local cache rather than a fresh request.</param>
public sealed record ModTranslation(
    string Title,
    string? Summary,
    string? Description,
    string Model,
    DateTimeOffset TranslatedAt,
    bool FromCache = false);

/// <summary>Translates mod metadata into Chinese.</summary>
public interface ITranslationService
{
    /// <summary>True when an endpoint and a key are both available.</summary>
    /// <remarks>
    /// Kept with its original meaning — the <i>online</i> credential is present — because callers and tests
    /// already read it that way. Use <see cref="IsAvailable"/> to ask the question the interface actually
    /// cares about: "can anything translate right now?".
    /// </remarks>
    bool IsConfigured { get; }

    /// <summary>True when either engine can translate: a configured key, or an installed local engine.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Translates a mod's title, summary and description, reusing a cached result when the source is unchanged.
    /// </summary>
    /// <param name="key">Stable cache key, e.g. "domain/modId".</param>
    /// <param name="title">Original title.</param>
    /// <param name="summary">Original summary.</param>
    /// <param name="description">Original description.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationResult<ModTranslation>> TranslateAsync(
        string key,
        string title,
        string? summary,
        string? description,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// Translations are cached per mod and invalidated by a hash of the source text, so re-opening a mod costs
/// nothing and an updated description is re-translated instead of showing a stale one.
/// </remarks>
public sealed class TranslationService : ITranslationService
{
    private const int MaxDescriptionCharacters = 4000;

    private readonly ISecretStore _secrets;
    private readonly AppPaths _paths;
    private readonly TranslationSettings _settings;
    private readonly IAppLogger? _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>
    /// The optional free local engine. Null means "online only", which is exactly the behaviour this
    /// service had before the local engine existed — that is what keeps the existing online tests honest.
    /// </summary>
    private readonly LocalArgosTranslationEngine? _localEngine;

    /// <summary>Creates the service.</summary>
    /// <param name="secrets">Secret store holding the key.</param>
    /// <param name="paths">Application paths.</param>
    /// <param name="settings">Translation settings.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="httpClient">Optional client, so tests can supply a stub.</param>
    /// <param name="localEngine">
    /// The free local engine, when the host has one. Optional and last so every existing call site keeps
    /// compiling and keeps its previous behaviour.
    /// </param>
    public TranslationService(
        ISecretStore secrets,
        AppPaths paths,
        TranslationSettings? settings = null,
        IAppLogger? logger = null,
        HttpClient? httpClient = null,
        LocalArgosTranslationEngine? localEngine = null)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? new TranslationSettings();
        _logger = logger;
        _localEngine = localEngine;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(NexusProvider.Identity);
    }

    /// <inheritdoc />
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.Endpoint) && _secrets.Contains(ISecretStore.TranslationApiKey);

    /// <inheritdoc />
    public bool IsAvailable =>
        (_localEngine is not null && _settings.Engine != TranslationEngineKind.Online && _localEngine.Probe().CanTranslate)
        || (_settings.Engine != TranslationEngineKind.Local && IsConfigured);

    /// <summary>Directory holding cached translations.</summary>
    public string CacheDirectory => Path.Combine(_paths.Root, "Cache", "Translations");

    /// <inheritdoc />
    public async Task<OperationResult<ModTranslation>> TranslateAsync(
        string key,
        string title,
        string? summary,
        string? description,
        CancellationToken cancellationToken = default)
    {
        // The local engine goes first when it is usable: it is free, needs no key and never touches the
        // network, which is the entire reason it exists. Everything below this line is the online path,
        // unchanged.
        if (_localEngine is not null
            && _settings.Engine != TranslationEngineKind.Online
            && _localEngine.Probe().CanTranslate)
        {
            return await TranslateLocalAsync(key, title, summary, description, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!IsConfigured)
        {
            return OperationResult<ModTranslation>.Fail(
                _settings.Engine == TranslationEngineKind.Local
                    ? "本地翻译引擎尚不可用：请在“设置 → 翻译”点击“初始化本地引擎”。"
                    : "尚未配置翻译服务：请在“设置 → 翻译”初始化本地引擎，"
                        + "或填入 API 密钥（支持 DeepSeek 等 OpenAI 兼容接口）。");
        }

        var source = ComposeSource(title, summary, description);
        var fingerprint = Fingerprint(source, _settings.Model);
        var cachePath = Path.Combine(CacheDirectory, Sanitize(key) + ".json");

        if (TryReadCache(cachePath, fingerprint, out var cached))
        {
            return OperationResult<ModTranslation>.Ok(cached! with { FromCache = true });
        }

        var payload = JsonSerializer.Serialize(new
        {
            model = _settings.Model,
            temperature = 0.2,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "你是游戏 Mod 资料的中文翻译。把用户给出的 JSON 字段翻译成简体中文："
                        + "保留 Mod 名称、专有名词、缩写（如 SKSE、RED4ext）和文件名的原文，必要时在括号里给出中文说明；"
                        + "不要添加解释、不要省略内容。以下内容必须原样保留、绝对不要翻译或改写："
                        + "文件路径与目录（如 {game_dir}/mods、D:\\Games\\x）、URL、代码与代码块、命令行、"
                        + "版本号、Mod ID / 文件 ID / AppID、Markdown 语法标记（如 **、##、-）。"
                        + "只输出 JSON，结构为 {\"title\":\"\",\"summary\":\"\",\"description\":\"\"}。",
                },
                new { role = "user", content = source },
            },
        });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            // The key travels in a header and is never logged.
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                "Bearer " + _secrets.Get(ISecretStore.TranslationApiKey));

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var detail = body.Length > 200 ? body[..200] : body;
                var error = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => NexusError.Auth("翻译服务的密钥无效或已过期。"),
                    System.Net.HttpStatusCode.TooManyRequests => NexusError.RateLimited("翻译服务提示请求过于频繁，请稍后再试。"),
                    _ => NexusError.Server($"翻译服务返回了 {(int)response.StatusCode}。", (int)response.StatusCode),
                };

                _logger?.Warn("Translate", $"翻译请求失败：HTTP {(int)response.StatusCode}。", detail);

                return OperationResult<ModTranslation>.Fail(error.Message);
            }

            using var document = JsonDocument.Parse(body);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
            {
                return OperationResult<ModTranslation>.Fail("翻译服务返回了空内容。");
            }

            var translated = ParseTranslation(content, title);
            if (translated is null)
            {
                return OperationResult<ModTranslation>.Fail("翻译结果无法解析为预期的 JSON 结构。");
            }

            var result = new ModTranslation(
                translated.Value.Title,
                translated.Value.Summary,
                translated.Value.Description,
                _settings.Model,
                DateTimeOffset.UtcNow);

            WriteCache(cachePath, fingerprint, result);
            _logger?.Info("Translate", $"已翻译：{key}（{_settings.Model}）。");

            return OperationResult<ModTranslation>.Ok(result);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            _logger?.Warn("Translate", "翻译请求失败。", ex.Message);

            return OperationResult<ModTranslation>.Fail($"无法连接翻译服务：{ex.Message}");
        }
    }

    /// <summary>
    /// Translates through the local engine, reusing the same cache file format as the online path.
    /// </summary>
    /// <param name="key">Stable cache key.</param>
    /// <param name="title">Original title.</param>
    /// <param name="summary">Original summary.</param>
    /// <param name="description">Original description.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The fingerprint folds in <see cref="LocalArgosTranslationEngine.EngineId"/> rather than a model name, so
    /// a translation produced by the local engine is never served to a request that will be answered online,
    /// and vice versa. Changing the language pack version changes that id, which re-translates everything
    /// rather than mixing two engines' output in one cache.
    /// </remarks>
    private async Task<OperationResult<ModTranslation>> TranslateLocalAsync(
        string key,
        string title,
        string? summary,
        string? description,
        CancellationToken cancellationToken)
    {
        var engine = _localEngine!;
        var status = engine.Probe();

        // A long description is fine locally: the engine splits it on paragraph and sentence boundaries,
        // so the online path's character cap would only throw content away.
        var source = ComposeSource(title, summary, description, int.MaxValue);
        var fingerprint = Fingerprint(source, LocalArgosTranslationEngine.EngineId);
        var cachePath = Path.Combine(CacheDirectory, Sanitize(key) + ".json");

        if (TryReadCache(cachePath, fingerprint, out var cached))
        {
            return OperationResult<ModTranslation>.Ok(cached! with { FromCache = true });
        }

        var label = status.PackageVersion is { Length: > 0 } version
            ? $"本地 Argos en→zh {version}"
            : "本地 Argos en→zh";

        var titleResult = await engine
            .TranslateTextAsync(title ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (!titleResult.Succeeded || titleResult.Value is null)
        {
            return OperationResult<ModTranslation>.Fail(titleResult.Error ?? "本地翻译失败。");
        }

        string? translatedSummary = null;
        if (!string.IsNullOrWhiteSpace(summary))
        {
            var summaryResult = await engine
                .TranslateTextAsync(summary, cancellationToken)
                .ConfigureAwait(false);

            if (!summaryResult.Succeeded)
            {
                return OperationResult<ModTranslation>.Fail(summaryResult.Error!);
            }

            translatedSummary = summaryResult.Value;
        }

        string? translatedDescription = null;
        if (!string.IsNullOrWhiteSpace(description))
        {
            var descriptionResult = await engine
                .TranslateDescriptionAsync(description, cancellationToken)
                .ConfigureAwait(false);

            if (!descriptionResult.Succeeded)
            {
                return OperationResult<ModTranslation>.Fail(descriptionResult.Error!);
            }

            translatedDescription = descriptionResult.Value;
        }

        var result = new ModTranslation(
            titleResult.Value,
            translatedSummary,
            translatedDescription,
            label,
            DateTimeOffset.UtcNow);

        WriteCache(cachePath, fingerprint, result);
        _logger?.Info("Translate", $"已用本地引擎翻译：{key}（{label}）。");

        return OperationResult<ModTranslation>.Ok(result);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>Builds the JSON the model is asked to translate.</summary>
    /// <param name="title">Title.</param>
    /// <param name="summary">Summary.</param>
    /// <param name="description">Description.</param>
    /// <param name="maxDescriptionCharacters">
    /// Cap on the description. The online path keeps the original limit because a chat request is billed by
    /// length; the local engine passes <see cref="int.MaxValue"/> because it segments the text instead.
    /// </param>
    private static string ComposeSource(
        string title,
        string? summary,
        string? description,
        int maxDescriptionCharacters = MaxDescriptionCharacters)
    {
        var trimmed = description is { Length: > 0 } text && text.Length > maxDescriptionCharacters
            ? text[..maxDescriptionCharacters]
            : description;

        return JsonSerializer.Serialize(new
        {
            title,
            summary = summary ?? string.Empty,
            description = trimmed ?? string.Empty,
        });
    }

    /// <summary>Parses the model's JSON answer, tolerating a code fence around it.</summary>
    /// <param name="content">Raw answer.</param>
    /// <param name="fallbackTitle">Title to keep when the answer has none.</param>
    private static (string Title, string? Summary, string? Description)? ParseTranslation(
        string content,
        string fallbackTitle)
    {
        var text = content.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var start = text.IndexOf('\n');
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start)
            {
                text = text[(start + 1)..end].Trim();
            }
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var title = root.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
            var summary = root.TryGetProperty("summary", out var summaryValue) ? summaryValue.GetString() : null;
            var description = root.TryGetProperty("description", out var descriptionValue) ? descriptionValue.GetString() : null;

            return string.IsNullOrWhiteSpace(title)
                ? (fallbackTitle, summary, description)
                : (title!, summary, description);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a cached translation whose source has not changed.</summary>
    /// <param name="path">Cache file.</param>
    /// <param name="fingerprint">Source fingerprint.</param>
    /// <param name="translation">Cached value.</param>
    private bool TryReadCache(string path, string fingerprint, out ModTranslation? translation)
    {
        translation = null;

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path));
            if (entry is null || !string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            translation = new ModTranslation(
                entry.Title,
                entry.Summary,
                entry.Description,
                entry.Model,
                entry.TranslatedAt);

            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Stores a translation; a write failure is not worth failing the caller for.</summary>
    /// <param name="path">Cache file.</param>
    /// <param name="fingerprint">Source fingerprint.</param>
    /// <param name="translation">Translation to store.</param>
    private void WriteCache(string path, string fingerprint, ModTranslation translation)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(new CacheEntry(
                    fingerprint,
                    translation.Title,
                    translation.Summary,
                    translation.Description,
                    translation.Model,
                    translation.TranslatedAt)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn("Translate", "翻译缓存写入失败。", ex.Message);
        }
    }

    /// <summary>Hash of the source text plus model, used to detect staleness.</summary>
    /// <param name="source">Source JSON.</param>
    /// <param name="model">Model name.</param>
    private static string Fingerprint(string source, string model) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model + "\n" + source)))[..32];

    /// <summary>Turns a cache key into a safe file name.</summary>
    /// <param name="key">Cache key.</param>
    private static string Sanitize(string key)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(key.Length);

        foreach (var character in key)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }

    /// <summary>Stored translation.</summary>
    /// <param name="Fingerprint">Source fingerprint.</param>
    /// <param name="Title">Translated title.</param>
    /// <param name="Summary">Translated summary.</param>
    /// <param name="Description">Translated description.</param>
    /// <param name="Model">Model used.</param>
    /// <param name="TranslatedAt">When it was produced.</param>
    private sealed record CacheEntry(
        [property: JsonPropertyName("fingerprint")] string Fingerprint,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("translatedAt")] DateTimeOffset TranslatedAt);
}
