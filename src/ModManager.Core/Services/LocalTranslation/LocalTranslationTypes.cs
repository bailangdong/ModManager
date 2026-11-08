namespace ModManager.Core.Services;

/// <summary>How far the local translation engine has got.</summary>
/// <remarks>
/// The settings page shows exactly this value, and it is deliberately not a boolean: "not set up
/// yet", "downloading", "working" and "broken" need different actions from the user, and a single
/// <c>IsReady</c> flag is what lets a half-installed engine masquerade as a working one.
/// </remarks>
public enum LocalEngineState
{
    /// <summary>Nothing has been installed yet.</summary>
    NotInitialized,

    /// <summary>An install is in flight.</summary>
    Initializing,

    /// <summary>Engine and language pack are present and a real translation has succeeded.</summary>
    Ready,

    /// <summary>Setup was attempted and failed; <see cref="LocalEngineStatus.Detail"/> says why.</summary>
    Failed,
}

/// <summary>The step of initialisation currently running, so progress is not a bare percentage.</summary>
public enum LocalEngineStage
{
    /// <summary>Looking for Python and any existing installation.</summary>
    Probing,

    /// <summary>Creating the isolated virtual environment.</summary>
    Runtime,

    /// <summary>Installing the inference libraries.</summary>
    Dependencies,

    /// <summary>Fetching the language pack.</summary>
    DownloadingPackage,

    /// <summary>Unpacking the language pack.</summary>
    InstallingPackage,

    /// <summary>Running a real translation to prove the engine works.</summary>
    Verifying,

    /// <summary>Finished.</summary>
    Done,

    /// <summary>Stopped with an error.</summary>
    Failed,
}

/// <summary>One progress report from initialisation.</summary>
/// <param name="Stage">Which step this is.</param>
/// <param name="Message">Human-readable line, already in Chinese.</param>
/// <param name="Fraction">0..1 when the step can measure itself, otherwise null.</param>
/// <param name="BytesReceived">Bytes downloaded so far, when downloading.</param>
/// <param name="TotalBytes">Total bytes, when the server reported a length.</param>
public sealed record LocalEngineProgress(
    LocalEngineStage Stage,
    string Message,
    double? Fraction = null,
    long? BytesReceived = null,
    long? TotalBytes = null);

/// <summary>A snapshot of the local engine, for display and for deciding what to do next.</summary>
/// <param name="State">Overall state.</param>
/// <param name="PythonPath">Interpreter that will be used, when one was found.</param>
/// <param name="PythonVersion">Its version string, when it could be read.</param>
/// <param name="RuntimeReady">True when the isolated environment with the inference libraries exists.</param>
/// <param name="PackageReady">True when the English → Chinese pack is installed.</param>
/// <param name="PackageId">Installed pack directory name, when present.</param>
/// <param name="PackageVersion">Installed pack version, when present.</param>
/// <param name="PackageBytes">Size of the installed pack on disk, in bytes.</param>
/// <param name="Detail">Plain-language explanation of the current state, including any failure.</param>
public sealed record LocalEngineStatus(
    LocalEngineState State,
    string? PythonPath,
    string? PythonVersion,
    bool RuntimeReady,
    bool PackageReady,
    string? PackageId,
    string? PackageVersion,
    long PackageBytes,
    string Detail)
{
    /// <summary>True when a translation can be attempted right now.</summary>
    public bool CanTranslate => State == LocalEngineState.Ready;
}

/// <summary>Where one official language pack comes from, so the UI can state the source and size.</summary>
/// <param name="Id">Stable identifier, e.g. <c>translate-en_zh-1_9</c>.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="SourceUrl">Exact download URL.</param>
/// <param name="SizeBytes">Size reported by the server, or 0 when unknown.</param>
/// <param name="FromCode">Source language code.</param>
/// <param name="ToCode">Target language code.</param>
/// <param name="Version">Package version.</param>
/// <param name="Sha256">Expected SHA-256, when the index publishes one. Empty means unverified.</param>
public sealed record LanguagePackageDescriptor(
    string Id,
    string DisplayName,
    string SourceUrl,
    long SizeBytes,
    string FromCode,
    string ToCode,
    string Version,
    string Sha256 = "")
{
    /// <summary>Size in a form a person can read.</summary>
    public string SizeText => SizeBytes > 0
        ? $"{SizeBytes / (1024d * 1024d):N1} MB"
        : "大小未知";
}
