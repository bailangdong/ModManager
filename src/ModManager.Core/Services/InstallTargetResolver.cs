namespace ModManager.Core.Services;

/// <summary>
/// Resolves a staged file to its absolute game target using the definition's declared rules.
/// </summary>
/// <remarks>
/// This type deliberately contains no heuristics based on the mod name. A target is either produced
/// by an explicit <see cref="InstallTargetRule"/> from the game definition, or by the game's normal
/// mod root. Anything else is not invented here; the planner surfaces it as unsafe instead.
/// </remarks>
public static class InstallTargetResolver
{
    /// <summary>Resolves one staged path.</summary>
    /// <param name="definition">Game definition supplying the rules.</param>
    /// <param name="installPath">Absolute game install directory.</param>
    /// <param name="sourceRelative">Staged path, forward slashes.</param>
    public static ResolvedInstallTarget Resolve(
        GameDefinition definition,
        string installPath,
        string sourceRelative)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var source = Normalize(sourceRelative);

        // Longest prefix wins, so a rule for `BepInEx/plugins` is not shadowed by a bare `plugins`.
        foreach (var rule in definition.InstallTargets
                     .Where(rule => !string.IsNullOrWhiteSpace(rule.SourcePrefix))
                     .OrderByDescending(rule => Normalize(rule.SourcePrefix).Length))
        {
            var prefix = Normalize(rule.SourcePrefix);
            if (!Matches(source, prefix))
            {
                continue;
            }

            var remainder = rule.StripSourcePrefix
                ? source[prefix.Length..].TrimStart('/')
                : source;
            var root = ResolveRuleRoot(definition, installPath, rule.TargetRoot);
            var absolute = Combine(root, remainder);

            return new ResolvedInstallTarget(
                absolute,
                root,
                $"规则 “{prefix}/” → “{rule.TargetRoot}”");
        }

        var defaultRoot = DeploymentPlanner.ResolveDeploymentRoot(definition, installPath);
        return new ResolvedInstallTarget(
            Combine(defaultRoot, source),
            defaultRoot,
            "游戏默认 Mod 根目录");
    }

    /// <summary>True when <paramref name="path"/> equals or sits under <paramref name="prefix"/>.</summary>
    public static bool Matches(string path, string prefix) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    private static string ResolveRuleRoot(GameDefinition definition, string installPath, string targetRoot)
    {
        var root = targetRoot.Replace('\\', '/').Trim('/');

        if (Path.IsPathRooted(root))
        {
            return Path.GetFullPath(root);
        }

        var baseDirectory = definition.ModRootInUserProfile
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : installPath;

        return Combine(baseDirectory, root);
    }

    private static string Combine(string root, string relative)
    {
        var combined = root;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            combined = Path.Combine(combined, segment);
        }

        return Path.GetFullPath(combined);
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').Trim('/');
}

/// <summary>One resolved target plus the root and evidence that produced it.</summary>
/// <param name="AbsolutePath">Absolute target file path.</param>
/// <param name="BaseRoot">Absolute directory the target rule anchors to.</param>
/// <param name="Evidence">Human-readable reason, shown in the preview.</param>
public sealed record ResolvedInstallTarget(string AbsolutePath, string BaseRoot, string Evidence);
