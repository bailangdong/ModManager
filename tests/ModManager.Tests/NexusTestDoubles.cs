using System.IO;
using ModManager.Core;

namespace ModManager.Tests;

/// <summary>
/// Builds mod metadata for the platform tests without touching a real game folder.
/// </summary>
public static class TestMods
{
    /// <summary>Creates a staged mod metadata record.</summary>
    /// <param name="id">Mod id.</param>
    /// <param name="name">Display name.</param>
    /// <param name="version">Installed version.</param>
    /// <param name="dependencies">Declared dependencies.</param>
    /// <param name="nexusModId">Nexus mod id, when the mod came from Nexus.</param>
    public static ModMetadata Create(
        string id,
        string? name = null,
        string? version = null,
        IEnumerable<string>? dependencies = null,
        int? nexusModId = null) => new()
        {
            Id = id,
            Name = name ?? id,
            GameId = "testgame",
            Version = version,
            Dependencies = dependencies?.ToList() ?? [],
            NexusModId = nexusModId,
            StagingPath = Path.Combine("artifacts", "test-mods", id),
            State = ModState.Deployed,
        };
}

/// <summary>
/// An <see cref="INexusProvider"/> that answers from memory.
/// </summary>
/// <remarks>
/// The tests must not depend on the live API: it needs a credential, it rate limits, and its content
/// changes underneath a test run. This double returns scripted answers so the business logic around the
/// API — association, update state, dependency mapping and the "we could not check" case — is what is
/// under test. It deliberately does not pretend to be a real client: anything not scripted fails.
/// </remarks>
public sealed class FakeNexusProvider : INexusProvider
{
    /// <summary>Search results to return.</summary>
    public List<NexusModSummary> SearchResults { get; } = [];

    /// <summary>Detail to return for one mod id.</summary>
    public Dictionary<int, NexusModDetail> Details { get; } = [];

    /// <summary>Files to return for one mod id.</summary>
    public Dictionary<int, List<NexusModFile>> Files { get; } = [];

    /// <summary>Update answers keyed by local mod id.</summary>
    public Dictionary<string, ModUpdateInfo> Updates { get; } = [];

    /// <summary>Download link to return, when scripted.</summary>
    public NexusDownloadLink? DownloadLink { get; set; }

    /// <summary>Error to return from every call, when set.</summary>
    public string? Error { get; set; }

    /// <summary>Number of search calls made.</summary>
    public int SearchCalls { get; private set; }

    /// <inheritdoc />
    public NexusAuthMode AuthMode { get; set; } = NexusAuthMode.ApiKey;

    /// <inheritdoc />
    public bool IsAuthenticated { get; set; } = true;

    /// <summary>
    /// Classified failure of the most recent call, so tests can drive the interface's error states without a
    /// network. Set alongside <see cref="Error"/> by <see cref="FailWith"/>.
    /// </summary>
    public NexusError? LastError { get; private set; }

    /// <summary>Scripts a failure with an explicit classification.</summary>
    /// <param name="error">Failure to return from every subsequent call.</param>
    public void FailWith(NexusError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error.Message;
        LastError = error;
    }

    /// <summary>Clears any scripted failure.</summary>
    public void Recover()
    {
        Error = null;
        LastError = null;
    }

    /// <inheritdoc />
    /// <remarks>Artwork is not part of the page logic these doubles exist to exercise.</remarks>
    public Task<OperationResult<IReadOnlyList<NexusGameArtwork>>> GetGameArtworkAsync(
        IReadOnlyList<string> domains,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<IReadOnlyList<NexusGameArtwork>>.Ok([]));

    /// <summary>Games returned by <see cref="GetGamesAsync"/>.</summary>
    public List<NexusGame> Games { get; } = [];

    /// <inheritdoc />
    public Task<OperationResult<NexusCredentials>> ValidateCredentialsAsync(
        NexusCredentials credentials,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<NexusCredentials>.Ok(credentials));

    /// <inheritdoc />
    public OperationResult<string> BeginOAuth(string redirectUri) =>
        OperationResult<string>.Ok("https://example.invalid/oauth");

    /// <inheritdoc />
    public Task<OperationResult<NexusCredentials>> CompleteOAuthAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<NexusCredentials>.Ok(NexusCredentials.Empty));

    /// <inheritdoc />
    public Task<OperationResult<IReadOnlyList<NexusGame>>> GetGamesAsync(CancellationToken cancellationToken = default) =>
        Error is null
            ? Task.FromResult(OperationResult<IReadOnlyList<NexusGame>>.Ok(Games))
            : Task.FromResult(OperationResult<IReadOnlyList<NexusGame>>.Fail(Error));

    /// <inheritdoc />
    public Task<OperationResult<NexusSearchResult>> SearchModsAsync(
        NexusSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        SearchCalls++;
        return Error is null
            ? Task.FromResult(OperationResult<NexusSearchResult>.Ok(
                new NexusSearchResult(SearchResults, query.Page, query.PageSize, SearchResults.Count)))
            : Task.FromResult(OperationResult<NexusSearchResult>.Fail(Error));
    }

    /// <inheritdoc />
    public Task<OperationResult<NexusModDetail>> GetModDetailAsync(
        string gameDomain,
        int modId,
        CancellationToken cancellationToken = default) =>
        Error is null && Details.TryGetValue(modId, out var detail)
            ? Task.FromResult(OperationResult<NexusModDetail>.Ok(detail))
            : Task.FromResult(OperationResult<NexusModDetail>.Fail(Error ?? "not scripted"));

    /// <inheritdoc />
    public Task<OperationResult<IReadOnlyList<NexusModFile>>> GetModFilesAsync(
        string gameDomain,
        int modId,
        CancellationToken cancellationToken = default) =>
        Error is null && Files.TryGetValue(modId, out var files)
            ? Task.FromResult(OperationResult<IReadOnlyList<NexusModFile>>.Ok(files))
            : Task.FromResult(OperationResult<IReadOnlyList<NexusModFile>>.Fail(Error ?? "not scripted"));

    /// <inheritdoc />
    public Task<OperationResult<NexusDownloadLink>> GetDownloadLinkAsync(
        string gameDomain,
        int modId,
        int fileId,
        CancellationToken cancellationToken = default) =>
        DownloadLink is null || Error is not null
            ? Task.FromResult(OperationResult<NexusDownloadLink>.Fail(Error ?? "no link scripted"))
            : Task.FromResult(OperationResult<NexusDownloadLink>.Ok(DownloadLink));

    /// <inheritdoc />
    public Task<OperationResult<IReadOnlyList<ModUpdateInfo>>> CheckForUpdatesAsync(
        string gameDomain,
        IReadOnlyList<(string ModId, int NexusModId, string? Version)> mods,
        CancellationToken cancellationToken = default)
    {
        if (Error is not null)
        {
            return Task.FromResult(OperationResult<IReadOnlyList<ModUpdateInfo>>.Fail(Error));
        }

        var answers = mods
            .Where(entry => Updates.ContainsKey(entry.ModId))
            .Select(entry => Updates[entry.ModId])
            .ToList();

        return Task.FromResult(OperationResult<IReadOnlyList<ModUpdateInfo>>.Ok(answers));
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetDownloadHeaders() =>
        new Dictionary<string, string> { ["apikey"] = "test-key" };
}
