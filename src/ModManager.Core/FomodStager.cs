namespace ModManager.Core;

/// <summary>Files written by a FOMOD selection, relative to the staging root.</summary>
public sealed record FomodStageResult(
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Applies a resolved FOMOD selection to a staging directory. It never touches the game folder;
/// the caller hands the staging directory to the normal deployment pipeline.
/// </summary>
public static class FomodStager
{
    /// <summary>Extracts selected files/folders and copies them to their declared destinations.</summary>
    public static async Task<OperationResult<FomodStageResult>> StageAsync(
        string archivePath,
        FomodPlan plan,
        IReadOnlyList<FomodSelection> selections,
        string destinationRoot,
        IArchiveReader archives,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(archives);

        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            return OperationResult<FomodStageResult>.Fail("暂存目录为空。");
        }

        var selection = FomodPlanSelector.Resolve(plan, selections);
        if (selection.Errors.Count > 0)
        {
            return OperationResult<FomodStageResult>.Fail(string.Join("；", selection.Errors));
        }

        var warnings = new List<string>(selection.Warnings);
        ArchiveIndex index;
        try
        {
            index = await archives.IndexAsync(archivePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return OperationResult<FomodStageResult>.Fail($"无法读取安装包：{ex.Message}");
        }

        var entries = index.Entries
            .Where(entry => !entry.IsDirectory)
            .ToDictionary(entry => entry.LowerPath, entry => entry, StringComparer.Ordinal);

        var copies = new List<(string EntryPath, string Destination)>();
        foreach (var file in selection.Files)
        {
            if (file.IsFolder)
            {
                var prefix = file.Source.TrimEnd('/').ToLowerInvariant() + "/";
                var matches = entries.Values
                    .Where(entry => entry.LowerPath.StartsWith(prefix, StringComparison.Ordinal))
                    .ToList();

                if (matches.Count == 0)
                {
                    warnings.Add($"FOMOD 文件夹「{file.Source}」在归档中不存在，已跳过。");
                    continue;
                }

                foreach (var entry in matches)
                {
                    var relative = entry.Path[prefix.Length..];
                    copies.Add((entry.Path, CombineRelative(file.Destination, relative)));
                }
            }
            else
            {
                var key = file.Source.ToLowerInvariant();
                if (!entries.TryGetValue(key, out var entry))
                {
                    warnings.Add($"FOMOD 文件「{file.Source}」在归档中不存在，已跳过。");
                    continue;
                }

                copies.Add((entry.Path, file.Destination));
            }
        }

        if (copies.Count == 0)
        {
            return OperationResult<FomodStageResult>.Fail("FOMOD 选择没有产生可安装文件。");
        }

        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        var scratch = Path.Combine(root, ".fomod-src-" + Guid.NewGuid().ToString("N"));

        var targets = new List<(string EntryPath, string Target)>();
        foreach (var (entryPath, destination) in copies)
        {
            if (!TryResolveTarget(root, destination, out var target))
            {
                return OperationResult<FomodStageResult>.Fail(
                    $"FOMOD 目标路径越界：{destination}");
            }

            targets.Add((entryPath, target));
        }

        var written = new List<string>();
        try
        {
            Directory.CreateDirectory(scratch);
            var extraction = await archives.ExtractAsync(
                archivePath,
                scratch,
                targets.Select(copy => copy.EntryPath).Distinct(StringComparer.Ordinal).ToList(),
                progress: null,
                cancellationToken).ConfigureAwait(false);

            if (extraction.ExtractedPaths.Count == 0)
            {
                return OperationResult<FomodStageResult>.Fail("FOMOD 选择无法安全解压。");
            }

            foreach (var (entryPath, target) in targets)
            {
                var source = Path.Combine(
                    scratch,
                    entryPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source))
                {
                    warnings.Add($"FOMOD 文件「{entryPath}」解压后不存在，已跳过。");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                written.Add(target);
            }

            return written.Count == 0
                ? OperationResult<FomodStageResult>.Fail("FOMOD 选择没有写入任何文件。")
                : OperationResult<FomodStageResult>.Ok(new FomodStageResult(
                    written.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToList(),
                    warnings));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            foreach (var target in written)
            {
                try
                {
                    File.Delete(target);
                }
                catch (IOException)
                {
                    // Best effort: the caller still sees a failure and the game folder is untouched.
                }
            }

            return OperationResult<FomodStageResult>.Fail($"FOMOD 暂存失败：{ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch (IOException)
            {
                // Leftover scratch files are harmless; the staging root is still usable.
            }
        }
    }

    private static string CombineRelative(string destination, string relative)
    {
        var left = destination.TrimEnd('/');
        var right = relative.TrimStart('/');
        return left.Length == 0 ? right : left + "/" + right;
    }

    private static bool TryResolveTarget(string root, string relative, out string target)
    {
        target = string.Empty;
        if (string.IsNullOrWhiteSpace(relative))
        {
            return false;
        }

        var normalized = relative.Replace('\\', '/').Trim();
        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return false;
        }

        var candidate = Path.GetFullPath(
            Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        target = candidate;
        return true;
    }
}
