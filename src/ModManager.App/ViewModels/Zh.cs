using ModManager.Core;

namespace ModManager.App.ViewModels;

/// <summary>
/// Chinese display names for the engine's enumerations and log fields.
/// </summary>
/// <remarks>
/// The engine records states as enum values and log entries with English category names, because those
/// are stable machine-facing identifiers that tests and journals depend on. Translating them at the
/// point of display — rather than renaming the enum members — keeps the persisted data and the recovery
/// logic untouched while the interface stays entirely Chinese.
///
/// Deliberately preserved in English: Mod, SHA256, ZIP/7Z/RAR, EXE, WPF/Windows, API. Those are terms
/// Chinese-speaking players and developers already read in English, and translating them would make the
/// interface harder, not easier, to use.
/// </remarks>
public static class Zh
{
    /// <summary>Log level.</summary>
    /// <param name="level">Engine level.</param>
    public static string LogLevel(LogLevel level) => level switch
    {
        Core.LogLevel.Trace => "跟踪",
        Core.LogLevel.Debug => "调试",
        Core.LogLevel.Information => "信息",
        Core.LogLevel.Warning => "警告",
        Core.LogLevel.Error => "错误",
        _ => level.ToString(),
    };

    /// <summary>Log subsystem name.</summary>
    /// <param name="category">Category recorded by the caller.</param>
    public static string Category(string? category) => category switch
    {
        null or "" => "通用",
        "Deploy" => "部署",
        "Install" => "安装",
        "Uninstall" => "卸载",
        "Import" => "导入",
        "Games" => "游戏",
        "Game" => "游戏",
        "Detection" => "游戏检测",
        "Ui" => "界面",
        "Journal" => "事务日志",
        "Recovery" => "恢复",
        "Settings" => "设置",
        "LoadOrder" => "加载顺序",
        "Profile" => "配置方案",
        "Library" => "Mod 库",
        "Conflict" => "冲突",
        "Dependency" => "依赖",
        "Verify" => "文件校验",
        "Health" => "健康检查",
        "Download" => "下载",
        "Nexus" => "Nexus",
        "External" => "外部扫描",
        "Backup" => "备份",
        "Archive" => "压缩包",
        "Safety" => "安全检查",
        "Startup" => "启动",
        "App" => "程序",
        _ => category,
    };

    /// <summary>Issue severity.</summary>
    /// <param name="severity">Recorded severity.</param>
    public static string Severity(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Info => "提示",
        IssueSeverity.Warning => "警告",
        IssueSeverity.Error => "错误",
        _ => severity.ToString(),
    };

    /// <summary>Journal state.</summary>
    /// <param name="state">Transaction state.</param>
    public static string JournalState(Core.JournalState state) => state switch
    {
        Core.JournalState.Open => "已开始",
        Core.JournalState.Applying => "正在应用",
        Core.JournalState.Applied => "已应用",
        Core.JournalState.Committed => "已完成",
        Core.JournalState.RolledBack => "已回滚",
        Core.JournalState.Failed => "失败",
        _ => state.ToString(),
    };

    /// <summary>Download state.</summary>
    /// <param name="state">Queue state.</param>
    /// <remarks>The enum is fully qualified because this method's own name shadows its type.</remarks>
    public static string DownloadState(Core.DownloadState state) => state switch
    {
        Core.DownloadState.Queued => "排队中",
        Core.DownloadState.Resolving => "解析中",
        Core.DownloadState.Downloading => "下载中",
        Core.DownloadState.Paused => "已暂停",
        Core.DownloadState.Verifying => "校验中",
        Core.DownloadState.Completed => "已完成",
        Core.DownloadState.Failed => "失败",
        Core.DownloadState.Cancelled => "已取消",
        _ => state.ToString(),
    };

    /// <summary>Mod installation state.</summary>
    /// <param name="state">Mod state.</param>
    /// <remarks>The enum is fully qualified because this method's own name shadows its type.</remarks>
    public static string ModState(Core.ModState state) => state switch
    {
        Core.ModState.Staged => "已暂存",
        Core.ModState.Deployed => "已启用",
        Core.ModState.Disabled => "已禁用",
        Core.ModState.UpdateAvailable => "有更新",
        Core.ModState.NeedsAttention => "需处理",
        _ => state.ToString(),
    };

    /// <summary>Path-safety verdict shown in the install preview.</summary>
    /// <param name="verdict">Engine verdict.</param>
    public static string Verdict(PathSafetyVerdict verdict) => verdict switch
    {
        PathSafetyVerdict.Safe => "安全",
        PathSafetyVerdict.Overwrite => "覆盖",
        PathSafetyVerdict.Conflict => "冲突",
        PathSafetyVerdict.Unsafe => "不安全",
        _ => verdict.ToString(),
    };

    /// <summary>How a file was materialised.</summary>
    /// <param name="method">Deployment method.</param>
    public static string Method(DeploymentMethod method) => method switch
    {
        DeploymentMethod.Hardlink => "硬链接",
        DeploymentMethod.Symlink => "符号链接",
        DeploymentMethod.Junction => "目录链接",
        DeploymentMethod.Copy => "复制",
        DeploymentMethod.Virtual => "虚拟文件系统",
        _ => method.ToString(),
    };

    /// <summary>
    /// Translates a recorded deployment-method string such as <c>"Hardlink, Copy"</c>.
    /// </summary>
    /// <param name="recorded">Value stored on the manifest.</param>
    public static string MethodList(string? recorded)
    {
        if (string.IsNullOrWhiteSpace(recorded))
        {
            return "未记录";
        }

        if (string.Equals(recorded, "none", StringComparison.OrdinalIgnoreCase))
        {
            return "无";
        }

        var names = new List<string>();
        foreach (var part in recorded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The applier records a usage count alongside the name ("Hardlink x2"), so translate the
            // leading token and keep whatever follows it.
            var segments = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var name = Enum.TryParse<DeploymentMethod>(segments[0], ignoreCase: true, out var method)
                ? Method(method)
                : segments[0];

            names.Add(segments.Length > 1 ? $"{name} {string.Join(' ', segments.Skip(1))}" : name);
        }

        return names.Count == 0 ? "未记录" : string.Join("、", names);
    }

    /// <summary>File verification result.</summary>
    /// <param name="status">Verify status.</param>
    public static string VerifyStatus(FileVerifyStatus status) => status switch
    {
        Core.FileVerifyStatus.Intact => "完好",
        Core.FileVerifyStatus.Missing => "文件缺失",
        Core.FileVerifyStatus.Modified => "已被修改",
        Core.FileVerifyStatus.Foreign => "已被替换",
        Core.FileVerifyStatus.Unreadable => "无法读取",
        _ => status.ToString(),
    };

    /// <summary>How a game installation was detected.</summary>
    /// <param name="kind">Probe kind.</param>
    public static string ProbeKind(InstallProbeKind kind) => kind switch
    {
        InstallProbeKind.Steam => "Steam 库",
        InstallProbeKind.Registry => "注册表",
        InstallProbeKind.PathHint => "历史路径",
        InstallProbeKind.Gog => "GOG",
        InstallProbeKind.Epic => "Epic",
        InstallProbeKind.WellKnown => "常见安装目录",
        _ => kind.ToString(),
    };

    /// <summary>Conflict resolution.</summary>
    /// <param name="kind">Resolution kind.</param>
    public static string Resolution(ConflictResolutionKind kind) => kind switch
    {
        ConflictResolutionKind.Undecided => "按加载顺序",
        ConflictResolutionKind.PreferMod => "指定提供方",
        ConflictResolutionKind.ExcludeMod => "排除该 Mod",
        _ => kind.ToString(),
    };
}
