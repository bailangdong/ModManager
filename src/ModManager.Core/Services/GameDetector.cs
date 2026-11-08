using ModManager.Core.Safety;

namespace ModManager.Core.Services;

/// <summary>
/// Finds game installations without ever asking the user to browse for a folder first.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — Modrix's <c>detect.rs</c>, which probes Steam cross-platform plus GOG, Epic,
/// Xbox, Origin and Uplay on Windows, reading "registry / manifest reads, no store APIs". We
/// implement the two mechanisms that cover the overwhelming majority of installs and are pure
/// file/registry reads:
/// </para>
/// <list type="bullet">
/// <item><b>Steam</b> — parse <c>libraryfolders.vdf</c> for every library root, then read each
/// <c>appmanifest_&lt;appid&gt;.acf</c> for its <c>installdir</c>. This is why a mod manager can find
/// a game the user never told it about.</item>
/// <item><b>Registry</b> — read the vendor key the definition names, which is how games sold outside
/// Steam (and The Sims 4 in particular) are found.</item>
/// </list>
/// <para>
/// Every probe is best-effort and never throws: a missing Steam, an unreadable registry hive or a
/// locked file all resolve to "not found here", and the next probe still runs. That layering matters
/// because detection runs over every known game at startup, and one failure must not stop the rest.
/// </para>
/// </remarks>
public sealed class GameDetector : IGameDetector
{
    /// <summary>Maximum Steam library roots examined, so a pathological VDF cannot stall detection.</summary>
    public const int MaxSteamLibraries = 64;

    /// <summary>Maximum games offered per Steam library, bounding the directory walk.</summary>
    public const int MaxSteamAppsScanned = 5_000;

    /// <inheritdoc />
    public GameDetectionResult Detect(GameDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var probes = definition.InstallProbes.Count > 0
            ? definition.InstallProbes
            : DefaultProbes(definition);

        foreach (var probe in probes)
        {
            var result = probe switch
            {
                InstallProbeKind.Steam => ProbeSteam(definition),
                InstallProbeKind.Registry => ProbeRegistry(definition),
                InstallProbeKind.WellKnown or InstallProbeKind.PathHint => ProbeWellKnown(definition),
                // GOG/Epic/Xbox/Origin/Uplay are declared in the definition schema and accepted here,
                // but not implemented. Reporting them as "not found" is honest; silently pretending to
                // support them would not be.
                _ => null,
            };

            if (result is { Found: true })
            {
                return result;
            }
        }

        return new GameDetectionResult(
            definition,
            false,
            null,
            probes.Count > 0 ? probes[0] : InstallProbeKind.PathHint,
            ["No install was found by any of the configured probes."],
            0d);
    }

    /// <inheritdoc />
    public IReadOnlyList<GameDetectionResult> DetectAll(IGameAdapterRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var results = new List<GameDetectionResult>();
        foreach (var adapter in registry.All())
        {
            results.Add(Detect(adapter.Definition));
        }

        return results;
    }

    /// <inheritdoc />
    public GameDetectionResult ValidateManualPath(GameDefinition definition, string installPath)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (string.IsNullOrWhiteSpace(installPath))
        {
            return new GameDetectionResult(
                definition, false, null, InstallProbeKind.PathHint,
                ["No directory was given."], 0d);
        }

        var adapter = new GenericGameAdapter(definition);
        var problems = adapter.ValidateInstall(installPath);

        if (problems.Count > 0)
        {
            return new GameDetectionResult(
                definition, false, installPath, InstallProbeKind.PathHint, problems, 0d);
        }

        var evidence = new List<string> { "The directory was chosen by the user." };
        if (definition.RequiredFiles.Count > 0)
        {
            evidence.Add($"All {definition.RequiredFiles.Count} required file(s) are present.");
        }
        else
        {
            evidence.Add("The definition requires no specific files, so any directory is accepted.");
        }

        return new GameDetectionResult(
            definition,
            true,
            Path.GetFullPath(installPath),
            InstallProbeKind.PathHint,
            evidence,
            1d);
    }

    /// <summary>Probes Steam for a game's install directory.</summary>
    /// <param name="definition">Game definition.</param>
    private static GameDetectionResult? ProbeSteam(GameDefinition definition)
    {
        if (definition.SteamAppId is not { } appId)
        {
            return null;
        }

        var evidence = new List<string>();

        foreach (var library in EnumerateSteamLibraries())
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            var installDir = ReadAcfValue(manifest, "installdir");

            if (string.IsNullOrWhiteSpace(installDir))
            {
                continue;
            }

            var candidate = Path.Combine(library, "steamapps", "common", installDir);
            if (!Directory.Exists(FileSystemOps.ToExtendedPath(candidate)))
            {
                continue;
            }

            evidence.Add($"Steam manifest {manifest} names installdir '{installDir}'.");

            var problems = new GenericGameAdapter(definition).ValidateInstall(candidate);
            if (problems.Count > 0)
            {
                // Found the manifest but the directory does not satisfy the definition. Reported as
                // evidence rather than silently skipped, because it usually means a stale manifest or a
                // definition with the wrong required file.
                evidence.AddRange(problems);
                continue;
            }

            return new GameDetectionResult(
                definition, true, Path.GetFullPath(candidate), InstallProbeKind.Steam, evidence, 0.95d);
        }

        return null;
    }

    /// <summary>Probes the vendor registry keys a definition declares.</summary>
    /// <param name="definition">Game definition.</param>
    private static GameDetectionResult? ProbeRegistry(GameDefinition definition)
    {
        if (!OperatingSystem.IsWindows() || definition.RegistryKeys.Count == 0)
        {
            return null;
        }

        foreach (var probe in definition.RegistryKeys)
        {
            if (!TryReadRegistry(probe, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var candidate = value.Trim('"', ' ');

            // A registry value frequently points at the executable rather than the directory.
            if (File.Exists(FileSystemOps.ToExtendedPath(candidate)))
            {
                candidate = Path.GetDirectoryName(candidate) ?? candidate;
            }

            if (!Directory.Exists(FileSystemOps.ToExtendedPath(candidate)))
            {
                continue;
            }

            var problems = new GenericGameAdapter(definition).ValidateInstall(candidate);
            if (problems.Count > 0)
            {
                continue;
            }

            return new GameDetectionResult(
                definition,
                true,
                Path.GetFullPath(candidate),
                InstallProbeKind.Registry,
                [$"Registry {probe.Hive}\\{probe.Key}\\{probe.Value} names '{candidate}'."],
                0.9d);
        }

        return null;
    }

    /// <summary>Probes well-known and hinted directories.</summary>
    /// <param name="definition">Game definition.</param>
    private static GameDetectionResult? ProbeWellKnown(GameDefinition definition)
    {
        var adapter = new GenericGameAdapter(definition);

        foreach (var hint in adapter.GetInstallPathHints())
        {
            if (!Directory.Exists(FileSystemOps.ToExtendedPath(hint)))
            {
                continue;
            }

            if (adapter.ValidateInstall(hint).Count > 0)
            {
                continue;
            }

            return new GameDetectionResult(
                definition,
                true,
                Path.GetFullPath(hint),
                InstallProbeKind.WellKnown,
                [$"Found at the common location '{hint}'."],
                0.6d);
        }

        return null;
    }

    /// <summary>Enumerates Steam library roots from the default install and its VDF.</summary>
    /// <remarks>
    /// Steam's own location comes from the registry on Windows, and library folders beyond it are
    /// listed in <c>libraryfolders.vdf</c>. Both are plain reads; there is no Steam API involved, which
    /// matches Modrix's "manifest reads, no store APIs" approach.
    /// </remarks>
    public static IReadOnlyList<string> EnumerateSteamLibraries()
    {
        var libraries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || libraries.Count >= MaxSteamLibraries)
            {
                return;
            }

            var normalized = Path.GetFullPath(candidate);
            if (seen.Add(normalized) && Directory.Exists(FileSystemOps.ToExtendedPath(normalized)))
            {
                libraries.Add(normalized);
            }
        }

        string? steamRoot = null;

        if (OperatingSystem.IsWindows())
        {
            TryReadRegistry(
                new RegistryProbe("HKCU", @"SOFTWARE\Valve\Steam", "SteamPath"),
                out steamRoot);
        }

        if (string.IsNullOrWhiteSpace(steamRoot))
        {
            steamRoot = OperatingSystem.IsWindows()
                ? @"C:\Program Files (x86)\Steam"
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "Steam");
        }

        Add(steamRoot);

        if (!string.IsNullOrWhiteSpace(steamRoot))
        {
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            foreach (var path in ReadVdfPaths(vdf))
            {
                Add(path);
            }
        }

        return libraries;
    }

    /// <summary>Extracts <c>"path"</c> values from a Steam <c>libraryfolders.vdf</c>.</summary>
    /// <param name="vdfPath">VDF file path.</param>
    /// <remarks>
    /// A deliberately small hand-rolled scan rather than a VDF parser: the format is
    /// key-value-with-braces, the values we need are always quoted strings, and a full parser would be
    /// more code to get wrong than the four lines it replaces. The file is read bounded, so a
    /// pathological VDF cannot exhaust memory.
    /// </remarks>
    public static IReadOnlyList<string> ReadVdfPaths(string vdfPath)
    {
        var paths = new List<string>();

        try
        {
            if (!File.Exists(FileSystemOps.ToExtendedPath(vdfPath)))
            {
                return paths;
            }

            var length = new FileInfo(FileSystemOps.ToExtendedPath(vdfPath)).Length;
            if (length is <= 0 or > 8 * 1024 * 1024)
            {
                return paths;
            }

            foreach (var rawLine in File.ReadLines(FileSystemOps.ToExtendedPath(vdfPath)))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var firstQuote = line.IndexOf('"', 6);
                if (firstQuote < 0)
                {
                    continue;
                }

                var lastQuote = line.LastIndexOf('"');
                if (lastQuote <= firstQuote)
                {
                    continue;
                }

                // VDF escapes backslashes in paths.
                paths.Add(line[(firstQuote + 1)..lastQuote].Replace(@"\\", @"\", StringComparison.Ordinal));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: an unreadable VDF just means this library is not discovered.
        }

        return paths;
    }

    /// <summary>Reads one quoted value from a Steam <c>.acf</c> manifest.</summary>
    /// <param name="acfPath">Manifest path.</param>
    /// <param name="key">Key to find.</param>
    public static string? ReadAcfValue(string acfPath, string key)
    {
        try
        {
            if (!File.Exists(FileSystemOps.ToExtendedPath(acfPath)))
            {
                return null;
            }

            foreach (var rawLine in File.ReadLines(FileSystemOps.ToExtendedPath(acfPath)))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith('"' + key + '"', StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var firstQuote = line.IndexOf('"', key.Length + 2);
                if (firstQuote < 0)
                {
                    continue;
                }

                var lastQuote = line.LastIndexOf('"');
                if (lastQuote <= firstQuote)
                {
                    continue;
                }

                return line[(firstQuote + 1)..lastQuote];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }

        return null;
    }

    /// <summary>Reads a registry string value, using the Registry API via P/Invoke-free managed access.</summary>
    /// <param name="probe">Registry location.</param>
    /// <param name="value">Value read.</param>
    /// <remarks>
    /// Uses <c>Microsoft.Win32.Registry</c>, which is part of the Windows Desktop runtime, so it costs
    /// no extra package. On non-Windows this returns false without touching anything.
    /// </remarks>
    public static bool TryReadRegistry(RegistryProbe probe, out string? value)
    {
        value = null;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var hive = probe.Hive.ToUpperInvariant() switch
            {
                "HKLM" or "HKEY_LOCAL_MACHINE" => Microsoft.Win32.RegistryHive.LocalMachine,
                "HKCU" or "HKEY_CURRENT_USER" => Microsoft.Win32.RegistryHive.CurrentUser,
                "HKCR" or "HKEY_CLASSES_ROOT" => Microsoft.Win32.RegistryHive.ClassesRoot,
                "HKU" or "HKEY_USERS" => Microsoft.Win32.RegistryHive.Users,
                _ => (Microsoft.Win32.RegistryHive?)null,
            };

            if (hive is null)
            {
                return false;
            }

            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive.Value, Microsoft.Win32.RegistryView.Default);
            using var key = baseKey.OpenSubKey(probe.Key);
            if (key is null)
            {
                return false;
            }

            value = key.GetValue(probe.Value) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Default probe order when a definition declares none.</summary>
    /// <param name="definition">Game definition.</param>
    private static IReadOnlyList<InstallProbeKind> DefaultProbes(GameDefinition definition)
    {
        var probes = new List<InstallProbeKind>();

        if (definition.SteamAppId is not null)
        {
            probes.Add(InstallProbeKind.Steam);
        }

        if (definition.RegistryKeys.Count > 0)
        {
            probes.Add(InstallProbeKind.Registry);
        }

        probes.Add(InstallProbeKind.WellKnown);
        return probes;
    }
}
