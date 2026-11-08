using System.Diagnostics;
using System.Text.Json;
using ModManager.Core;
using ModManager.Core.Persistence;
using ModManager.Core.Safety;
using ModManager.Core.Services;

// Live Nexus Mods API acceptance probe.
//
// SAFETY: never prints the API key, an Authorization header, or a signed download URL. It reads the real
// SecretStore for the credential, but every artifact it produces (downloads, staging, journals, backups)
// goes to a probe-local data root under artifacts\test-tmp, and the install target is artifacts\test-game.
// Requests are few and sequential: this is an acceptance check, not a load test.

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var appData = args.FirstOrDefault(argument => !argument.StartsWith('-'))
    ?? Path.Combine(repoRoot, "src", "ModManager.App", "bin", "Debug", "net8.0-windows", "Data");
var domainArg = args.Length > 1 ? args[1] : null;
var keywordArg = args.Length > 2 ? args[2] : "patch";

var probeRoot = Path.Combine(repoRoot, "artifacts", "test-tmp", "nexus-probe");
var gameRoot = Path.Combine(repoRoot, "artifacts", "test-game");

Console.WriteLine($"应用数据目录：{appData}");
Console.WriteLine($"探针数据目录：{probeRoot}");
Console.WriteLine($"安装目标目录：{gameRoot}");

var appPaths = new AppPaths(appData);
var logger = new AppLogger(appPaths.Logs, LogLevel.Warning);
var secrets = new SecretStore(appPaths.Root, logger);

var hasKey = secrets.Contains(ISecretStore.NexusApiKey);
Console.WriteLine($"API Key：{(hasKey ? "已配置" : "未配置")}");
Console.WriteLine($"翻译 API Key：{(secrets.Contains(ISecretStore.TranslationApiKey) ? "已配置" : "未配置")}");

if (!hasKey)
{
    Console.WriteLine("未配置 API Key，真实业务验收停止。");
    return 1;
}

var settings = new NexusSettings { MinimumRequestInterval = TimeSpan.FromSeconds(1) };
using var provider = new NexusProvider(secrets, settings, logger);
Console.WriteLine($"认证模式：{provider.AuthMode}");
Console.WriteLine();

// ---- 0. Endpoint contract experiment (only with --raw) -------------------------------------------
if (args.Contains("--raw"))
{
    Console.WriteLine("=== 0. 端点契约实测（每个候选各 1 次请求，仅状态码与短响应体）===");
    var apiKey = secrets.Get(ISecretStore.NexusApiKey)!;
    using var raw = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    raw.DefaultRequestHeaders.UserAgent.ParseAdd(NexusProvider.Identity);
    raw.DefaultRequestHeaders.Add("apikey", apiKey);
    raw.DefaultRequestHeaders.Accept.ParseAdd("application/json");

    var candidates = new[]
    {
        "https://api.nexusmods.com/v1/games/skyrimspecialedition/mods/trending.json?count=20&offset=0",
        "https://api.nexusmods.com/v1/games/skyrimspecialedition/mods/trending.json?count=20&offset=10",
        "https://api.nexusmods.com/v1/games/skyrimspecialedition/mods/downloads.json?count=20&offset=0",
        "https://api.nexusmods.com/v1/games/skyrimspecialedition/mods/trending.json?count=5&sort=downloads&sort_direction=desc",
    };

    foreach (var candidate in candidates)
    {
        var label = candidate.Replace("https://api.nexusmods.com/v1/", string.Empty, StringComparison.Ordinal);
        try
        {
            using var response = await raw.GetAsync(candidate);
            var body = await response.Content.ReadAsStringAsync();
            var summary = body.Length > 100 ? body[..100] + "…" : body;

            // For arrays, the item count and the first name say whether count/offset/sort were honoured.
            var shape = string.Empty;
            try
            {
                using var parsed = JsonDocument.Parse(body);
                if (parsed.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var items = parsed.RootElement.EnumerateArray().ToList();
                    var firstName = items.Count > 0 && items[0].TryGetProperty("name", out var name) ? name.GetString() : "(无名)";
                    var firstDownloads = items.Count > 0 && items[0].TryGetProperty("mod_downloads", out var downloadsValue) ? downloadsValue.GetInt64() : 0;
                    shape = $"  条目={items.Count}  首项=\"{firstName}\"  首项下载={firstDownloads}";
                }
            }
            catch (JsonException)
            {
                shape = "  (非 JSON 数组)";
            }

            Console.WriteLine($"  [{(int)response.StatusCode}] {label}{shape}");
            if (shape.Length == 0)
            {
                Console.WriteLine($"        {summary.Replace('\r', ' ').Replace('\n', ' ')}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"  [异常] {label} → {ex.Message}");
        }

        await Task.Delay(1200);
    }

    // v1 ignores count/offset/sort on its listing endpoints and has no search endpoint, so real paging and
    // sorting can only come from somewhere else. The v2 GraphQL endpoint is the candidate.
    Console.WriteLine("  v2 GraphQL 探测：");
    try
    {
        const string introspection = """
            {"query":"{ f: __type(name:\"ModsFilter\"){ inputFields{ name type{ kind name ofType{ kind name } } } } s: __type(name:\"ModsSort\"){ inputFields{ name type{ kind name ofType{ kind name } } } } }"}
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql")
        {
            Content = new StringContent(introspection, System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await raw.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"    [{(int)response.StatusCode}] {(body.Length > 2000 ? body[^2000..] : body)}");
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
    {
        Console.WriteLine($"    异常：{ex.Message}");
    }

    await Task.Delay(1200);

    Console.WriteLine("  时间字段实测（trending 首项 + 首项文件）：");
    try
    {
        using var listing = await raw.GetAsync("https://api.nexusmods.com/v1/games/skyrimspecialedition/mods/trending.json?count=1");
        using var document = JsonDocument.Parse(await listing.Content.ReadAsStringAsync());
        var mod = document.RootElement.EnumerateArray().First();

        foreach (var property in mod.EnumerateObject())
        {
            if (property.Name.Contains("time", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("date", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"      mod.{property.Name} = {property.Value}");
            }
        }

        var modId = mod.GetProperty("mod_id").GetInt32();
        await Task.Delay(1200);

        using var filesResponse = await raw.GetAsync($"https://api.nexusmods.com/v1/games/skyrimspecialedition/mods/{modId}/files.json");
        using var filesDocument = JsonDocument.Parse(await filesResponse.Content.ReadAsStringAsync());
        var file = filesDocument.RootElement.GetProperty("files").EnumerateArray().First();

        foreach (var property in file.EnumerateObject())
        {
            if (property.Name.Contains("time", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("date", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("uploaded", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"      file.{property.Name} = {property.Value}");
            }
        }
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
    {
        Console.WriteLine($"      诊断失败：{ex.Message}");
    }

    Console.WriteLine();
}

// ---- 0b. v2 GraphQL query experiment (only with --gql) -------------------------------------------
if (args.Contains("--gql"))
{
    Console.WriteLine("=== 0b. v2 GraphQL 真实查询实测 ===");
    var apiKey2 = secrets.Get(ISecretStore.NexusApiKey)!;
    using var gql = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    gql.DefaultRequestHeaders.UserAgent.ParseAdd(NexusProvider.Identity);
    gql.DefaultRequestHeaders.Add("apikey", apiKey2);
    gql.DefaultRequestHeaders.Accept.ParseAdd("application/json");

    var variants = new (string Label, string Document)[]
    {
        ("gameArtwork 参数与返回类型", """{"query":"{ q: __type(name:\"Query\"){ fields{ name args{ name type{ name kind ofType{ name } } } type{ name kind ofType{ name } } } } }"}"""),
        ("GameArtwork 字段", """{"query":"{ artwork: __type(name:\"GameArtwork\"){ fields{ name type{ name kind ofType{ name } } } } game: __type(name:\"Game\"){ fields{ name } } }"}"""),
    };

    foreach (var (label, document) in variants)
    {
        try
        {
            using var content = new StringContent(document, System.Text.Encoding.UTF8, "application/json");
            using var response = await gql.PostAsync("https://api.nexusmods.com/v2/graphql", content);
            var text = await response.Content.ReadAsStringAsync();

            if (label.StartsWith("gameArtwork", StringComparison.Ordinal))
            {
                using var parsed = JsonDocument.Parse(text);
                var fields = parsed.RootElement.GetProperty("data").GetProperty("q").GetProperty("fields");

                foreach (var field in fields.EnumerateArray())
                {
                    if (field.GetProperty("name").GetString() != "gameArtwork")
                    {
                        continue;
                    }

                    var argList = string.Join(
                        ", ",
                        field.GetProperty("args").EnumerateArray().Select(argument =>
                            argument.GetProperty("name").GetString() + ": "
                            + argument.GetProperty("type").GetProperty("name").GetString()));

                    Console.WriteLine($"  gameArtwork({argList})");
                }
            }
            else
            {
                Console.WriteLine($"  {label}: {(text.Length > 1200 ? text[..1200] : text)}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            Console.WriteLine($"  [{label}] 异常 {ex.Message}");
        }

        await Task.Delay(1200);
    }

    Console.WriteLine();
}

// ---- 0c. gameArtwork experiment (only with --art) ------------------------------------------------
if (args.Contains("--art"))
{
    Console.WriteLine("=== 0c. gameArtwork 真实查询 ===");
    var key3 = secrets.Get(ISecretStore.NexusApiKey)!;
    using var client3 = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    client3.DefaultRequestHeaders.UserAgent.ParseAdd(NexusProvider.Identity);
    client3.DefaultRequestHeaders.Add("apikey", key3);
    client3.DefaultRequestHeaders.Accept.ParseAdd("application/json");

    var probes = new (string Label, string Query)[]
    {
        ("game.gameArtwork", "{ game(domainName: \"cyberpunk2077\") { id name gameArtwork { schemaV2 { tile thumbnail hero } } } }"),
        ("game.artwork", "{ game(domainName: \"cyberpunk2077\") { id name artworkSchema artwork { schemaV2 { tile } } } }"),
        ("media", "{ game(domainName: \"cyberpunk2077\") { id mediaCount } gameArtwork { schemaV1 { tile tileBlurred } } }"),
    };

    foreach (var (probeLabel, probeQuery) in probes)
    {
        var probeBody = JsonSerializer.Serialize(new { query = probeQuery });
        using var probeContent = new StringContent(probeBody, System.Text.Encoding.UTF8, "application/json");

        try
        {
            using var probeResponse = await client3.PostAsync("https://api.nexusmods.com/v2/graphql", probeContent);
            var probeText = await probeResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"  [{probeLabel}] HTTP {(int)probeResponse.StatusCode}: {(probeText.Length > 900 ? probeText[..900] : probeText)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"  [{probeLabel}] 异常 {ex.Message}");
        }

        await Task.Delay(1200);
    }

    Console.WriteLine();
}

// ---- 1. Authentication ---------------------------------------------------------------------------
Console.WriteLine("=== 1. 鉴权（1 次请求）===");
var watch = Stopwatch.StartNew();
var validation = await provider.ValidateCredentialsAsync(new NexusCredentials
{
    ApiKey = secrets.Get(ISecretStore.NexusApiKey),
});
watch.Stop();

if (!validation.Succeeded || validation.Value is null)
{
    Console.WriteLine($"鉴权失败：分类={provider.LastError?.Kind} HTTP={provider.LastError?.StatusCode} 耗时 {watch.ElapsedMilliseconds} ms");
    Console.WriteLine($"消息：{validation.Error}");
    return 1;
}

Console.WriteLine($"鉴权成功：账号 {validation.Value.UserName}（高级会员：{validation.Value.IsPremium}），HTTP 200，耗时 {watch.ElapsedMilliseconds} ms");

// ---- 2. Game domain -----------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 2. 游戏映射（1 次请求）===");
watch.Restart();
var games = await provider.GetGamesAsync();
watch.Stop();

if (!games.Succeeded || games.Value is null)
{
    Console.WriteLine($"失败：{games.Error}");
    return 1;
}

Console.WriteLine($"游戏列表：{games.Value.Count} 个域名，耗时 {watch.ElapsedMilliseconds} ms");

var game = domainArg is not null
    ? games.Value.FirstOrDefault(candidate => string.Equals(candidate.DomainName, domainArg, StringComparison.OrdinalIgnoreCase))
    : games.Value.FirstOrDefault(candidate => candidate.DomainName == "skyrimspecialedition");

game ??= games.Value.FirstOrDefault(candidate => candidate.ModCount > 500);

if (game is null)
{
    Console.WriteLine("没有找到可用域名。");
    return 1;
}

Console.WriteLine($"使用域名：{game.DomainName}（{game.Name}，已发布 {game.ModCount} 个 Mod）");

// ---- 3. Search ----------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"=== 3. 搜索 keyword=\"{keywordArg}\"（1 次请求）===");
watch.Restart();
var search = await provider.SearchModsAsync(new NexusSearchQuery
{
    GameDomain = game.DomainName,
    SearchText = keywordArg,
    Page = 1,
    PageSize = 5,
});
watch.Stop();

if (!search.Succeeded || search.Value is null)
{
    Console.WriteLine($"搜索失败：{search.Error}");
    return 1;
}

Console.WriteLine($"搜索成功：返回 {search.Value.Items.Count} 项，耗时 {watch.ElapsedMilliseconds} ms");

var candidateMods = search.Value.Items.Where(item => !item.ContainsAdultContent).ToList();
if (candidateMods.Count == 0)
{
    Console.WriteLine("搜索结果中没有非年龄限制内容，改用受控游戏（Palworld）重试一次。");

    watch.Restart();
    var retry = await provider.SearchModsAsync(new NexusSearchQuery
    {
        GameDomain = "palworld",
        SearchText = "mod",
        Page = 1,
        PageSize = 5,
    });
    watch.Stop();

    if (!retry.Succeeded || retry.Value is null || retry.Value.Items.Count == 0)
    {
        Console.WriteLine($"重试仍无结果：{retry.Error ?? "空列表"}（这本身也是真实结果）");
        return 1;
    }

    Console.WriteLine($"重试成功：返回 {retry.Value.Items.Count} 项，耗时 {watch.ElapsedMilliseconds} ms");
    candidateMods = retry.Value.Items.Where(item => !item.ContainsAdultContent).ToList();
    game = games.Value.First(candidate => candidate.DomainName == "palworld");
}

var first = candidateMods[0];

// Cover image: check the URL Nexus returned is actually retrievable (this hits the CDN, not the API).
if (!string.IsNullOrWhiteSpace(first.PictureUrl))
{
    try
    {
        using var imageClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        imageClient.DefaultRequestHeaders.UserAgent.ParseAdd(NexusProvider.Identity);
        var imageResponse = await imageClient.GetAsync(first.PictureUrl);
        var bytes = await imageResponse.Content.ReadAsByteArrayAsync();
        var contentType = imageResponse.Content.Headers.ContentType?.MediaType ?? "(无)";
        Console.WriteLine($"封面实测：HTTP {(int)imageResponse.StatusCode}  类型 {contentType}  {bytes.Length / 1024.0:0.#} KB  → {(imageResponse.IsSuccessStatusCode && bytes.Length > 0 ? "可加载" : "不可用（将回退字母占位）")}");
        Console.WriteLine($"封面 URL 扩展名：{Path.GetExtension(new Uri(first.PictureUrl).AbsolutePath)}");

        // WPF cannot decode WebP, so it matters whether the CDN will hand back a format WPF understands
        // when asked for one. Request JPEG explicitly and report what comes back.
        using var negotiate = new HttpRequestMessage(HttpMethod.Get, first.PictureUrl);
        negotiate.Headers.Accept.ParseAdd("image/jpeg,image/png,image/gif,image/bmp");
        using var negotiated = await imageClient.SendAsync(negotiate);
        var negotiatedType = negotiated.Content.Headers.ContentType?.MediaType ?? "(无)";
        var negotiatedBytes = await negotiated.Content.ReadAsByteArrayAsync();
        Console.WriteLine($"封面协商（Accept: jpeg/png/gif/bmp）：HTTP {(int)negotiated.StatusCode}  类型 {negotiatedType}  {negotiatedBytes.Length / 1024.0:0.#} KB  可被 WPF 解码={negotiatedType is "image/jpeg" or "image/png" or "image/gif" or "image/bmp"}");

        // The API hands out a thumbnail URL. Nexys also serves the full image at the same path without the
        // thumbnails segment; if that one comes back as JPEG, the interface can show it without a WebP codec.
        var fullUrl = first.PictureUrl.Replace("/thumbnails/", "/", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var full = await imageClient.GetAsync(fullUrl);
            var fullType = full.Content.Headers.ContentType?.MediaType ?? "(无)";
            var fullBytes = await full.Content.ReadAsByteArrayAsync();
            Console.WriteLine($"全尺寸图（去掉 thumbnails/）：HTTP {(int)full.StatusCode}  类型 {fullType}  {fullBytes.Length / 1024.0:0.#} KB");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"全尺寸图不可用：{ex.GetType().Name}");
        }

        var coverDir = Path.Combine(repoRoot, "artifacts", "test-tmp", "covers");
        Directory.CreateDirectory(coverDir);
        var coverFile = Path.Combine(coverDir, $"cover{Path.GetExtension(new Uri(first.PictureUrl).AbsolutePath)}");
        await File.WriteAllBytesAsync(coverFile, bytes);
        await File.WriteAllTextAsync(Path.Combine(coverDir, "cover.url.txt"), first.PictureUrl);
        Console.WriteLine($"已保存封面样本：{coverFile}（{bytes.Length} 字节）");
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.WriteLine($"封面实测：网络失败（{ex.GetType().Name}）→ UI 将回退字母占位");
    }
}
else
{
    Console.WriteLine("封面实测：API 未提供图片 URL → UI 使用字母占位");
}

foreach (var item in candidateMods)
{
    Console.WriteLine(
        $"  #{item.ModId} “{item.Name}” 作者 {item.Author ?? "(无)"} 版本 {item.Version ?? "(无)"} "
        + $"更新 {item.UpdatedAt?.ToString("yyyy-MM-dd") ?? "(无)"} 下载 {item.DownloadCount} 分类 {item.Category ?? "(无)"} "
        + $"图片 {(string.IsNullOrWhiteSpace(item.PictureUrl) ? "(无)" : "有")} 成人内容 {item.ContainsAdultContent}");
}

// ---- 4. Detail + files --------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"=== 4. 详情 + 文件（2 次请求）===");
watch.Restart();
var detail = await provider.GetModDetailAsync(game.DomainName, first.ModId);
watch.Stop();

if (!detail.Succeeded || detail.Value is null)
{
    Console.WriteLine($"详情失败：{detail.Error}");
    return 1;
}

Console.WriteLine($"详情成功：耗时 {watch.ElapsedMilliseconds} ms");
Console.WriteLine($"  ModID {detail.Value.Summary.ModId}  名称 {detail.Value.Summary.Name}  作者 {detail.Value.Summary.Author ?? "(无)"}  版本 {detail.Value.Summary.Version ?? "(无)"}");
Console.WriteLine($"  域名 {detail.Value.Summary.GameDomain ?? "(空!)"}  页面 {(string.IsNullOrWhiteSpace(detail.Value.Summary.PageUrl) ? "(无)" : "有")}");
Console.WriteLine($"  描述 {(string.IsNullOrWhiteSpace(detail.Value.Description) ? "(无)" : $"{detail.Value.Description.Length} 字符")}  标签 {detail.Value.Tags.Count} 个");

var files = detail.Value.Files;
Console.WriteLine($"  文件 {files.Count} 个：");
foreach (var file in files.Take(6))
{
    Console.WriteLine(
        $"    #{file.FileId} {file.Name}  版本 {file.Version ?? "(无)"}  {file.SizeBytes / 1024} KB  "
        + $"{file.Category ?? "(无分类)"}  {(file.IsPrimary ? "主文件" : "附加")}  {file.UploadedAt?.ToString("yyyy-MM-dd") ?? "(无日期)"}");
}

// ---- 5. Dependencies ----------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 5. 依赖（复用详情）===");
Console.WriteLine($"  直接依赖 {detail.Value.Requirements.Count}  被依赖 {detail.Value.RequiredBy.Count}  冲突 {detail.Value.Conflicts.Count}  推荐 {detail.Value.Recommended.Count}");

var report = new DependencyResolver().ResolveNexusReferences(
    detail.Value.Summary.Name,
    [.. detail.Value.Requirements, .. detail.Value.Recommended],
    []);

Console.WriteLine($"  解析结果：节点 {report.TotalCount}  缺失 {report.MissingCount}  不可解析 {report.UnsupportedCount}");
Console.WriteLine($"  摘要：{report.Summary}");

foreach (var node in report.Roots.Take(3))
{
    Console.WriteLine($"    #{node.NexusModId} “{node.Name}” → {node.State}");
}

if (report.TotalCount == 0)
{
    Console.WriteLine("  真实结果：该 Mod 未声明依赖。");
}

// ---- 6. Download link ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 6. 下载能力（1 次请求，不下载）===");
var target = files.Where(file => !string.Equals(file.Category, "OPTIONAL", StringComparison.OrdinalIgnoreCase))
    .OrderByDescending(file => file.IsPrimary)
    .ThenBy(file => file.SizeBytes)
    .FirstOrDefault();

if (target is null)
{
    Console.WriteLine("该 Mod 没有文件，链路测试停止。");
    return 1;
}

watch.Restart();
var link = await provider.GetDownloadLinkAsync(game.DomainName, first.ModId, target.FileId);
watch.Stop();

if (!link.Succeeded || link.Value is null)
{
    Console.WriteLine($"无法通过管理器下载：分类={provider.LastError?.Kind} HTTP={provider.LastError?.StatusCode}");
    Console.WriteLine($"原因：{link.Error}");

    // Distinguish "this account cannot use the download endpoint" from "this author disabled manager
    // downloads": another mod, from a different author, answers that with one or two extra requests.
    Console.WriteLine();
    Console.WriteLine("  —— 复验：另一个作者的另一个 Mod ——");

    var others = await provider.SearchModsAsync(new NexusSearchQuery
    {
        GameDomain = game.DomainName,
        Page = 1,
        PageSize = 8,
    });

    var tried = 0;
    var otherForbidden = false;

    foreach (var other in others.Value?.Items.Where(item => item.ModId != first.ModId && !item.ContainsAdultContent) ?? [])
    {
        if (tried >= 2)
        {
            break;
        }

        var otherFiles = await provider.GetModFilesAsync(game.DomainName, other.ModId);
        var otherFile = otherFiles.Value?.OrderByDescending(file => file.IsPrimary).FirstOrDefault();
        if (otherFile is null)
        {
            Console.WriteLine($"  #{other.ModId} “{other.Name}” 没有文件，跳过。");
            continue;
        }

        tried++;
        var otherLink = await provider.GetDownloadLinkAsync(game.DomainName, other.ModId, otherFile.FileId);
        Console.WriteLine($"  #{other.ModId} “{other.Name}” 作者 {other.Author ?? "(无)"} 文件 #{otherFile.FileId} → HTTP {provider.LastError?.StatusCode?.ToString() ?? "200"} 分类 {provider.LastError?.Kind.ToString() ?? "None"} 成功={otherLink.Succeeded}");

        otherForbidden |= !otherLink.Succeeded && provider.LastError?.Kind == NexusFailureKind.Forbidden;
    }

    if (otherForbidden)
    {
        Console.WriteLine("  结论：不同作者的多个 Mod 都被 403 拒绝 → 账号级限制（Nexus 的 download_link 接口需要高级会员），不是单个作者的设置。按要求不做任何绕过。");
    }

    Console.WriteLine();
    Console.WriteLine("结论：真实下载 NOT TESTED（当前账号权限不允许管理器下载链接）。");
    Console.WriteLine("      因此 DownloadManager 真实下载 / ArchiveReader 真实文件 / 真实安装 / 真实卸载 均未测试。");
    Console.WriteLine("      本地安装流水线与 ArchiveReader 由既有测试与 SmokeRun 覆盖（非 Nexus 真实链路）。");
    return 0;
}

Console.WriteLine($"取得链接成功：文件 {link.Value.FileName}，有效期至 {link.Value.ExpiresAt?.ToLocalTime().ToString("HH:mm:ss") ?? "(未声明)"}，耗时 {watch.ElapsedMilliseconds} ms（链接内容不输出）");
// ---- 7. Download through DownloadManager --------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 7. DownloadManager 真实下载 ===");
Directory.CreateDirectory(probeRoot);
var probePaths = new AppPaths(probeRoot);
probePaths.EnsureCreated();

var probeStore = new JsonDocumentStore(probePaths);
var probeLibrary = new LibraryRepository(probeStore, probePaths, logger);
var downloads = new DownloadManager(probeLibrary, probePaths, new DownloadOptions(), logger);

watch.Restart();
var record = await downloads.EnqueueAsync(new DownloadRequest
{
    Url = link.Value.Url,
    FileName = link.Value.FileName,
    ModNameHint = first.Name,
    NexusModId = first.ModId,
    NexusFileId = target.FileId,
    Headers = provider.GetDownloadHeaders(),
});

var completed = await WaitForDownloadAsync(downloads, record.Id, TimeSpan.FromMinutes(10));
watch.Stop();

if (completed is null || completed.State != DownloadState.Completed)
{
    Console.WriteLine($"下载失败：状态 {completed?.State.ToString() ?? "(无记录)"} 错误 {completed?.Error}");
    Console.WriteLine($"耗时 {watch.Elapsed.TotalSeconds:0.0}s");
    return 1;
}

var archivePath = completed.TargetPath;
var size = new FileInfo(archivePath).Length;
Console.WriteLine($"下载完成：{Path.GetFileName(archivePath)}  {size / 1024.0:0.#} KB  用时 {watch.Elapsed.TotalSeconds:0.#}s  状态 {completed.State}");
Console.WriteLine($"  HTTPS 分段/断点字段：SupportsResume={completed.ServerSupportsResume}  Segments={completed.Segments.Count}");
Console.WriteLine($"  校验值：声明 {(string.IsNullOrWhiteSpace(completed.ExpectedSha256) ? "(无)" : "有")}  实际 {(string.IsNullOrWhiteSpace(completed.ActualSha256) ? "(未计算)" : "已计算")}");

// ---- 8. ArchiveReader ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 8. ArchiveReader 分析（不执行任何内容）===");
var archives = new ArchiveReader();
var indexed = await archives.IndexAsync(archivePath);

Console.WriteLine($"  识别格式：{indexed.ArchivePath.Split('.').Last()}  条目 {indexed.Entries.Count} 个  总大小 {indexed.Entries.Sum(entry => entry.Size) / 1024.0:0.#} KB");
Console.WriteLine($"  可疑条目（EXE/BAT/CMD/PS1 等，仅静态列出、绝不执行）：");

var dangerous = new[] { ".exe", ".bat", ".cmd", ".ps1", ".msi", ".vbs", ".scr" };
var flagged = indexed.Entries
    .Where(entry => dangerous.Contains(Path.GetExtension(entry.Path), StringComparer.OrdinalIgnoreCase))
    .Take(10)
    .ToList();

foreach (var entry in flagged)
{
    Console.WriteLine($"    {entry.Path}（{entry.Size} 字节）→ 已忽略，不执行");
}

if (flagged.Count == 0)
{
    Console.WriteLine("    （压缩包内没有可执行文件）");
}

// ---- 9. Install through the existing pipeline ---------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 9. 真实安装（现有安全流水线）===");

var safety = new PathSafetyService(probePaths, new SafetyOptions(), logger);
var planner = new DeploymentPlanner(safety, logger);
var journals = new JournalService(probePaths, logger);
var verify = new VerifyService();
var dependencies = new DependencyService();
var analyzer = new ModAnalyzer();
var applier = new DeploymentApplier(safety, journals, probePaths, probeLibrary);
var installSettings = new SettingsCarrier();

var install = new ModInstallService(
    probePaths,
    archives,
    analyzer,
    planner,
    applier,
    verify,
    dependencies,
    probeLibrary,
    probeLibrary,
    probeLibrary,
    safety,
    installSettings);

// A minimal, honest adapter: this real mod belongs to another game, so its layout is analysed generically
// and deployed under a Mods\ folder inside the test game root.
var definition = new GameDefinition
{
    Id = "probe-game",
    Name = "Probe Game",
    ModRoot = "Mods",
    Capabilities = GameCapability.HardlinkDeployment | GameCapability.SymlinkDeployment | GameCapability.Profiles
        | GameCapability.FileConflicts | GameCapability.Dependencies | GameCapability.NonZipArchives,
    DeploymentPreference = [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy],
    IsUserDefined = true,
};

var adapter = new GenericGameAdapter(definition);
Directory.CreateDirectory(gameRoot);
var modRoot = adapter.ResolveModRoot(gameRoot);
safety.ApproveGameRoot(definition.Id, gameRoot, modRoot);

var staged = await install.StageAsync(
    new InstallRequest
    {
        ArchivePath = archivePath,
        GameId = definition.Id,
        AcceptLowConfidence = true,
        NexusModId = first.ModId,
        NexusFileId = target.FileId,
        SourceUrl = first.PageUrl,
        Version = first.Version,
        DisplayName = first.Name,
    },
    adapter,
    logger);

if (!staged.Succeeded || staged.Value is null)
{
    Console.WriteLine($"暂存失败（按现有规则拒绝即可）：{staged.Error}");
    return 1;
}

Console.WriteLine($"  暂存成功：根目录 “{staged.Value.RootRelativePath}”  部署文件 {staged.Value.Metadata.StagedFileCount} 个  置信度 {staged.Value.Metadata.AnalyzedRootConfidence:0.00}");
Console.WriteLine($"  Nexus 关联：ModID {staged.Value.Metadata.NexusModId}  FileID {staged.Value.Metadata.NexusFileId}  版本 {staged.Value.Metadata.Version}");

var deployed = await install.InstallAsync(staged.Value, adapter, gameRoot, logger);
if (!deployed.Succeeded)
{
    Console.WriteLine($"安装失败：{deployed.Error}");
    return 1;
}

Console.WriteLine($"  安装成功：写入 {deployed.Manifest!.Files.Count} 个文件，目录 {deployed.Manifest.DeploymentRoot}");
Console.WriteLine($"  Journal 事务：{deployed.TransactionId}");

foreach (var file in deployed.Manifest.Files.Take(5))
{
    Console.WriteLine($"    {file.RelativeTarget}  {(File.Exists(file.SourcePath ?? string.Empty) ? "" : "")}{file.Method}");
}

var landed = deployed.Manifest.Files.Count(file => File.Exists(Path.Combine(deployed.Manifest.DeploymentRoot, file.RelativeTarget)));
Console.WriteLine($"  落盘校验：{landed}/{deployed.Manifest.Files.Count} 个文件实际存在");

// ---- 10. Uninstall ------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 10. 真实卸载 ===");
var removed = await install.UninstallAsync(definition.Id, staged.Value.Metadata.Id, adapter, gameRoot, false, logger);

Console.WriteLine($"  卸载结果：{(removed.Succeeded ? "成功" : "失败：" + removed.Error)}");
var leftBehind = deployed.Manifest.Files.Count(file => File.Exists(Path.Combine(deployed.Manifest.DeploymentRoot, file.RelativeTarget)));
Console.WriteLine($"  残留文件：{leftBehind}（应为 0）");
Console.WriteLine($"  库中剩余 Mod：{probeLibrary.ModsForGame(definition.Id).Count}（应为 0）");
Console.WriteLine($"  Journal 残留：{journals.FindInterrupted().Count}（应为 0）");

return 0;

static async Task<DownloadRecord?> WaitForDownloadAsync(IDownloadManager downloads, string id, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    var lastReport = DateTimeOffset.MinValue;

    while (DateTimeOffset.UtcNow < deadline)
    {
        var current = downloads.Get(id);
        if (current is null)
        {
            return null;
        }

        if (current.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled)
        {
            return current;
        }

        if (DateTimeOffset.UtcNow - lastReport > TimeSpan.FromSeconds(3))
        {
            lastReport = DateTimeOffset.UtcNow;
            Console.WriteLine($"    进度：{FileSystemOps.FormatBytes(current.BytesDownloaded)} / {(current.TotalBytes is { } total ? FileSystemOps.FormatBytes(total) : "未知")}  {current.Fraction * 100:0}%");
        }

        await Task.Delay(300);
    }

    return downloads.Get(id);
}
