using ModManager.Core;

namespace ModManager.App.ViewModels;

/// <summary>What the interface should tell the user about a Nexus failure.</summary>
public enum NexusUiState
{
    /// <summary>No failure.</summary>
    Ok = 0,

    /// <summary>No credential configured yet.</summary>
    MissingKey = 1,

    /// <summary>The stored credential was rejected.</summary>
    Unauthorized = 2,

    /// <summary>Refused: premium-only, author-disabled API access, or age gating.</summary>
    Forbidden = 3,

    /// <summary>The mod or file does not exist.</summary>
    NotFound = 4,

    /// <summary>Rate limited; the server named a wait.</summary>
    RateLimited = 5,

    /// <summary>The server failed.</summary>
    ServerError = 6,

    /// <summary>The host could not be reached.</summary>
    Network = 7,

    /// <summary>The request timed out.</summary>
    Timeout = 8,

    /// <summary>The response could not be understood.</summary>
    Malformed = 9,

    /// <summary>The request itself was rejected as invalid (400/422).</summary>
    BadRequest = 10,
}

/// <summary>
/// Turns a classified Nexus failure into the sentence and the next action the user should see.
/// </summary>
/// <remarks>
/// One failure message for every cause is the thing this type exists to prevent: "connection failed" is
/// actively misleading when the real answer is "you have not entered a key", and it is useless when the real
/// answer is "this file needs premium, open the page". Each state therefore says what happened and what the
/// user can do about it, and the two flags tell the page which button to offer.
/// </remarks>
public static class NexusUi
{
    /// <summary>Maps a failure to a UI state.</summary>
    /// <param name="error">Classified failure, when the provider recorded one.</param>
    /// <param name="fallbackMessage">Raw message, used when no classification is available.</param>
    public static NexusUiState Classify(NexusError? error, string? fallbackMessage)
    {
        if (error is not null && error.Kind != NexusFailureKind.None)
        {
            return error.Kind switch
            {
                NexusFailureKind.MissingCredential => NexusUiState.MissingKey,
                NexusFailureKind.Unauthorized => NexusUiState.Unauthorized,
                NexusFailureKind.Forbidden => NexusUiState.Forbidden,
                NexusFailureKind.NotFound => NexusUiState.NotFound,
                NexusFailureKind.RateLimited => NexusUiState.RateLimited,
                NexusFailureKind.ServerError => NexusUiState.ServerError,
                NexusFailureKind.Network => NexusUiState.Network,
                NexusFailureKind.Timeout => NexusUiState.Timeout,
                NexusFailureKind.Malformed => NexusUiState.Malformed,
                NexusFailureKind.BadRequest => NexusUiState.BadRequest,
                _ => NexusUiState.Network,
            };
        }

        // Fallback for callers that only have a message: still separate the cases that matter.
        var text = fallbackMessage ?? string.Empty;
        if (text.Length == 0)
        {
            return NexusUiState.Ok;
        }

        if (text.Contains("API key", StringComparison.OrdinalIgnoreCase)
            || text.Contains("密钥", StringComparison.Ordinal))
        {
            return NexusUiState.MissingKey;
        }

        if (text.Contains("401", StringComparison.Ordinal))
        {
            return NexusUiState.Unauthorized;
        }

        if (text.Contains("403", StringComparison.Ordinal) || text.Contains("premium", StringComparison.OrdinalIgnoreCase))
        {
            return NexusUiState.Forbidden;
        }

        if (text.Contains("404", StringComparison.Ordinal))
        {
            return NexusUiState.NotFound;
        }

        if (text.Contains("429", StringComparison.Ordinal) || text.Contains("限流", StringComparison.Ordinal))
        {
            return NexusUiState.RateLimited;
        }

        return NexusUiState.Network;
    }

    /// <summary>Builds the sentence shown to the user.</summary>
    /// <param name="error">Classified failure.</param>
    /// <param name="fallbackMessage">Raw message, used when there is no classification.</param>
    public static string Describe(NexusError? error, string? fallbackMessage = null)
    {
        var state = Classify(error, fallbackMessage);

        return state switch
        {
            NexusUiState.Ok => string.Empty,
            NexusUiState.MissingKey => "尚未配置 Nexus API 密钥：请在“设置 → Nexus”填入你自己的 API 密钥，"
                + "或在浏览器中打开 Nexus 页面手动下载。",
            NexusUiState.Unauthorized => "Nexus 拒绝了当前凭据（401）。密钥可能已失效，请在“设置 → Nexus”重新生成并填入。",
            NexusUiState.Forbidden => DescribeForbidden(error),
            NexusUiState.NotFound => "Nexus 上找不到该 Mod 或文件（404），它可能已被作者移除。",
            NexusUiState.RateLimited => DescribeRateLimit(error),
            NexusUiState.ServerError => $"Nexus 服务器返回 {(error?.StatusCode?.ToString() ?? "5xx")}，属于对方服务的问题，请稍后重试。",
            NexusUiState.Timeout => "请求超时：Nexus 在规定时间内没有响应。可能是网络较慢或对方服务繁忙。",
            NexusUiState.Malformed => "Nexus 返回的数据无法解析（字段缺失或格式变化）。这不是你的网络问题，请重试或查看日志。",
            NexusUiState.BadRequest => "Nexus 拒绝了这次请求的参数（400/422）。这通常意味着接口有变化，"
                + "请把日志里的状态码反馈给开发者；你的密钥没有问题。",
            _ => string.IsNullOrWhiteSpace(fallbackMessage)
                ? "无法连接到 Nexus Mods。请检查网络后重试。"
                : fallbackMessage!,
        };
    }

    /// <summary>True when the fix is in this application's settings.</summary>
    /// <param name="error">Classified failure.</param>
    public static bool NeedsSettings(NexusError? error, string? fallbackMessage = null) =>
        Classify(error, fallbackMessage) is NexusUiState.MissingKey or NexusUiState.Unauthorized;

    /// <summary>True when the user has to finish the action on the Nexus website.</summary>
    /// <param name="error">Classified failure.</param>
    public static bool NeedsNexusPage(NexusError? error, string? fallbackMessage = null) =>
        Classify(error, fallbackMessage) == NexusUiState.Forbidden;

    /// <summary>Explains a refusal without inventing a reason.</summary>
    /// <param name="error">Classified failure.</param>
    private static string DescribeForbidden(NexusError? error) => error?.IsPremiumRequired == true
        ? "该文件需要 Nexus 高级会员：请前往 Nexus 页面完成下载，再用“从压缩包安装…”导入。"
        : "Nexus 拒绝了这个请求（403）。可能是该文件需要高级会员、作者关闭了管理器下载，或内容需要年龄段确认 —— "
            + "这些限制只能在 Nexus 页面上解决。";

    /// <summary>Explains throttling, including the server's own requested wait.</summary>
    /// <param name="error">Classified failure.</param>
    private static string DescribeRateLimit(NexusError? error) => error?.RetryAfter is { } wait && wait > TimeSpan.Zero
        ? $"请求过于频繁，Nexus 要求等待约 {Math.Ceiling(wait.TotalSeconds):0} 秒后再试。"
        : "请求过于频繁，已被 Nexus 限流。请稍等几分钟再试；本程序不会立即重试。";
}
