using System.Xml;
using System.Xml.Linq;

namespace ModManager.Core;

/// <summary>
/// A read-only description of a FOMOD installer. The wizard UI decides which options to select;
/// this model only carries what the archive declared, so applying it never needs to parse XML again.
/// </summary>
public sealed record FomodPlan(
    string ModuleName,
    IReadOnlyList<FomodStep> Steps,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> UnsupportedFeatures);

/// <summary>One page of a FOMOD wizard.</summary>
public sealed record FomodStep(string Name, IReadOnlyList<FomodGroup> Groups);

/// <summary>How many options a group allows.</summary>
public enum FomodGroupType
{
    /// <summary>Exactly one option.</summary>
    SelectExactlyOne = 0,

    /// <summary>At least one option.</summary>
    SelectAtLeastOne = 1,

    /// <summary>Any number of options, including none.</summary>
    SelectAny = 2,

    /// <summary>Every option is selected and cannot be changed.</summary>
    SelectAll = 3,
}

/// <summary>A group of mutually related options inside one step.</summary>
public sealed record FomodGroup(
    string Name,
    FomodGroupType Type,
    IReadOnlyList<FomodOption> Options);

/// <summary>One selectable plugin/component.</summary>
public sealed record FomodOption(
    string Name,
    string? Description,
    IReadOnlyList<FomodFile> Files,
    IReadOnlyList<string> Flags);

/// <summary>A file or folder the selected option wants to place.</summary>
public sealed record FomodFile(
    string Source,
    string Destination,
    int Priority,
    bool IsFolder);

/// <summary>
/// Reads <c>fomod/ModuleConfig.xml</c> from an archive and returns a platform-neutral plan.
/// </summary>
/// <remarks>
/// This is deliberately a planner, not an installer: it does not write into the game directory,
/// does not run scripts and does not choose an option on the user's behalf. FOMOD XML is
/// attacker-controlled, so DTDs are prohibited, the document size is bounded and every
/// source/destination path is rejected if it is rooted or escapes with <c>..</c>.
/// </remarks>
public static class FomodPlanReader
{
    private const long MaxConfigChars = 2_000_000;

    /// <summary>Reads and validates the FOMOD configuration.</summary>
    public static async Task<OperationResult<FomodPlan>> ReadAsync(
        string archivePath,
        IArchiveReader archives,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return OperationResult<FomodPlan>.Fail("安装包文件不存在。");
        }

        ArchiveIndex index;
        try
        {
            index = await archives.IndexAsync(archivePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return OperationResult<FomodPlan>.Fail($"无法读取安装包：{ex.Message}");
        }

        var configEntry = index.Entries.FirstOrDefault(entry =>
            string.Equals(entry.LowerPath, "fomod/moduleconfig.xml", StringComparison.Ordinal)
            || entry.LowerPath.EndsWith("/fomod/moduleconfig.xml", StringComparison.Ordinal));

        if (configEntry is null)
        {
            return OperationResult<FomodPlan>.Fail("安装包中没有 fomod/ModuleConfig.xml。");
        }

        // Keep the scratch directory beside the archive: the package library is writable, while
        // the process temp directory may be locked down by the host sandbox.
        var archiveDirectory = Path.GetDirectoryName(Path.GetFullPath(archivePath));
        if (string.IsNullOrWhiteSpace(archiveDirectory))
        {
            return OperationResult<FomodPlan>.Fail("无法确定安装包所在目录。");
        }

        var tempRoot = Path.Combine(
            archiveDirectory,
            ".fomod-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempRoot);
            var extraction = await archives.ExtractAsync(
                archivePath,
                tempRoot,
                [configEntry.Path],
                progress: null,
                cancellationToken).ConfigureAwait(false);

            if (extraction.ExtractedPaths.Count == 0)
            {
                return OperationResult<FomodPlan>.Fail("FOMOD 配置无法安全解压。");
            }

            var localPath = Path.Combine(
                tempRoot,
                configEntry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(localPath))
            {
                return OperationResult<FomodPlan>.Fail("FOMOD 配置解压后不存在。");
            }

            return Parse(localPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return OperationResult<FomodPlan>.Fail($"读取 FOMOD 配置失败：{ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is harmless; never fail the plan because cleanup failed.
            }
        }
    }

    private static OperationResult<FomodPlan> Parse(string configPath)
    {
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxConfigChars,
                IgnoreComments = true,
            };
            using var stream = File.OpenRead(configPath);
            using var reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            return OperationResult<FomodPlan>.Fail($"FOMOD 配置不是有效 XML：{ex.Message}");
        }

        var root = document.Root;
        if (root is null || !LocalNameIs(root, "config"))
        {
            return OperationResult<FomodPlan>.Fail("FOMOD 配置缺少 config 根节点。");
        }

        var warnings = new List<string>();
        var moduleName = ChildValue(root, "moduleName");
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            moduleName = "FOMOD 安装包";
            warnings.Add("FOMOD 没有声明 moduleName，已使用默认名称。");
        }

        var steps = new List<FomodStep>();
        var installSteps = Children(root, "installSteps").FirstOrDefault();
        foreach (var stepElement in Children(installSteps, "installStep"))
        {
            var stepName = stepElement.Attribute("name")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(stepName))
            {
                stepName = ChildValue(stepElement, "name");
            }

            if (string.IsNullOrWhiteSpace(stepName))
            {
                stepName = $"步骤 {steps.Count + 1}";
            }

            var groups = new List<FomodGroup>();
            var optionalGroups = Children(stepElement, "optionalFileGroups").FirstOrDefault();
            foreach (var groupElement in Children(optionalGroups, "group"))
            {
                var groupName = groupElement.Attribute("name")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(groupName))
                {
                    groupName = ChildValue(groupElement, "name");
                }

                if (string.IsNullOrWhiteSpace(groupName))
                {
                    groupName = $"选项组 {groups.Count + 1}";
                }

                var type = ParseGroupType(groupElement.Attribute("type")?.Value, warnings, groupName);
                var options = new List<FomodOption>();
                var plugins = Children(groupElement, "plugins").FirstOrDefault();
                foreach (var plugin in Children(plugins, "plugin"))
                {
                    var optionName = plugin.Attribute("name")?.Value?.Trim();
                    if (string.IsNullOrWhiteSpace(optionName))
                    {
                        optionName = ChildValue(plugin, "name");
                    }

                    if (string.IsNullOrWhiteSpace(optionName))
                    {
                        optionName = $"选项 {options.Count + 1}";
                    }

                    var description = ChildValue(plugin, "description");
                    var files = new List<FomodFile>();
                    var fileList = Children(plugin, "files").FirstOrDefault();
                    foreach (var fileElement in Children(fileList, "file"))
                    {
                        var source = fileElement.Attribute("source")?.Value;
                        var destination = fileElement.Attribute("destination")?.Value;
                        AddFile(files, source, destination, priority: ParsePriority(fileElement), isFolder: false, warnings, optionName);
                    }

                    foreach (var folderElement in Children(fileList, "folder"))
                    {
                        var source = folderElement.Attribute("source")?.Value;
                        var destination = folderElement.Attribute("destination")?.Value;
                        AddFile(files, source, destination, priority: ParsePriority(folderElement), isFolder: true, warnings, optionName);
                    }

                    var flags = new List<string>();
                    var conditionFlags = Children(plugin, "conditionFlags").FirstOrDefault();
                    foreach (var flag in Children(conditionFlags, "flag"))
                    {
                        var flagName = flag.Attribute("name")?.Value?.Trim();
                        if (!string.IsNullOrWhiteSpace(flagName))
                        {
                            flags.Add(flagName);
                        }
                    }

                    options.Add(new FomodOption(
                        optionName,
                        string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                        files,
                        flags));
                }

                if (options.Count == 0)
                {
                    warnings.Add($"选项组「{groupName}」没有可安装选项，已跳过。");
                    continue;
                }

                groups.Add(new FomodGroup(groupName, type, options));
            }

            if (groups.Count == 0)
            {
                warnings.Add($"步骤「{stepName}」没有选项组，已跳过。");
                continue;
            }

            steps.Add(new FomodStep(stepName, groups));
        }

        if (steps.Count == 0)
        {
            return OperationResult<FomodPlan>.Fail("FOMOD 配置没有可执行的安装步骤。");
        }

        var unsupported = new List<string>();
        if (root.Descendants().Any(element => LocalNameIs(element, "conditionalFileInstalls")))
        {
            unsupported.Add("conditionalFileInstalls");
        }

        if (root.Descendants().Any(element => LocalNameIs(element, "fileDependency")))
        {
            unsupported.Add("fileDependency");
        }

        if (root.Descendants().Any(element => LocalNameIs(element, "gameDependency")))
        {
            unsupported.Add("gameDependency");
        }

        if (unsupported.Count > 0)
        {
            warnings.Add(
                "该 FOMOD 使用了当前尚未支持的安装特性："
                + string.Join("、", unsupported)
                + "。为避免错误安装，已禁止自动暂存。");
        }

        return OperationResult<FomodPlan>.Ok(new FomodPlan(moduleName.Trim(), steps, warnings, unsupported));
    }

    private static void AddFile(
        List<FomodFile> files,
        string? source,
        string? destination,
        int priority,
        bool isFolder,
        List<string> warnings,
        string optionName)
    {
        if (!TryNormalizeRelative(source, out var safeSource))
        {
            warnings.Add($"选项「{optionName}」包含不安全的源路径，已忽略。");
            return;
        }

        if (!TryNormalizeRelative(destination, out var safeDestination))
        {
            warnings.Add($"选项「{optionName}」包含不安全的目标路径，已忽略。");
            return;
        }

        files.Add(new FomodFile(safeSource, safeDestination, priority, isFolder));
    }

    private static bool TryNormalizeRelative(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Replace('\\', '/').Trim();
        if (candidate.StartsWith('/') || candidate.Contains(':'))
        {
            return false;
        }

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment == ".." || segment == "."))
        {
            return false;
        }

        normalized = string.Join('/', segments);
        return normalized.Length <= 512;
    }

    private static FomodGroupType ParseGroupType(string? value, List<string> warnings, string groupName)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "selectexactlyone" => FomodGroupType.SelectExactlyOne,
            "selectatleastone" => FomodGroupType.SelectAtLeastOne,
            "selectall" => FomodGroupType.SelectAll,
            "selectany" => FomodGroupType.SelectAny,
            null or "" => FomodGroupType.SelectAny,
            _ => UnknownType(value, warnings, groupName),
        };
    }

    private static FomodGroupType UnknownType(string value, List<string> warnings, string groupName)
    {
        warnings.Add($"选项组「{groupName}」的类型「{value}」未知，按 SelectAny 处理。");
        return FomodGroupType.SelectAny;
    }

    private static int ParsePriority(XElement element)
    {
        return int.TryParse(element.Attribute("priority")?.Value, out var priority) ? priority : 0;
    }

    private static string? ChildValue(XElement? parent, string localName)
    {
        var child = Children(parent, localName).FirstOrDefault();
        return child is null ? null : child.Value;
    }

    private static IEnumerable<XElement> Children(XElement? parent, string localName)
    {
        return parent is null
            ? []
            : parent.Elements().Where(element => LocalNameIs(element, localName));
    }

    private static bool LocalNameIs(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);
}
