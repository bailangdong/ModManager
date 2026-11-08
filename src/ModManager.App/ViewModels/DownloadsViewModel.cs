using System.Windows.Media;
using ModManager.Core;
using ModManager.Core.Safety;

namespace ModManager.App.ViewModels;

/// <summary>
/// Downloads: the queue, with pause, resume, retry and cancel.
/// </summary>
/// <remarks>
/// The user's spec §8 asks for concurrent downloads with pause, resume, cancel, retry, resumable transfer,
/// speed, ETA, SHA-256 and failure recovery. All of that lives in the engine; this page is a projection of
/// it, which is the intended division — a view-model that re-implemented retry policy would be a second
/// place for the policy to be wrong.
/// </remarks>
public sealed class DownloadsViewModel : ViewModelBase, IPageViewModel
{
    private readonly ApplicationHost _host;
    private string _status = "No downloads.";
    private int _activeCount;
    private int _failedCount;
    private int _completedCount;

    /// <summary>Creates the page and subscribes to engine progress.</summary>
    /// <param name="host">Composition root.</param>
    public DownloadsViewModel(ApplicationHost host)
    {
        _host = host;

        // Subscribed once here rather than polled: the engine pushes progress, and a timer would be both
        // laggier and more code.
        _host.Downloads.ProgressChanged += (_, _) => Refresh();
        _host.Downloads.StateChanged += (_, _) => Refresh();

        PauseCommand = new RelayCommand<DownloadRow>(row => PauseAsync(row));
        ResumeCommand = new RelayCommand<DownloadRow>(row => ResumeAsync(row));
        CancelCommand = new RelayCommand<DownloadRow>(row => CancelAsync(row));
        RetryCommand = new RelayCommand<DownloadRow>(row => RetryAsync(row));
        RemoveCommand = new RelayCommand<DownloadRow>(row => RemoveAsync(row));
        PauseAllCommand = new RelayCommand(() => PauseAllAsync());
    }

    /// <summary>Download rows.</summary>
    public BulkObservableCollection<DownloadRow> Downloads { get; } = [];

    /// <summary>Pauses one download.</summary>
    public RelayCommand<DownloadRow> PauseCommand { get; }

    /// <summary>Resumes one download.</summary>
    public RelayCommand<DownloadRow> ResumeCommand { get; }

    /// <summary>Cancels one download.</summary>
    public RelayCommand<DownloadRow> CancelCommand { get; }

    /// <summary>Retries a failed download.</summary>
    public RelayCommand<DownloadRow> RetryCommand { get; }

    /// <summary>Removes a finished download from the list.</summary>
    public RelayCommand<DownloadRow> RemoveCommand { get; }

    /// <summary>Pauses everything.</summary>
    public RelayCommand PauseAllCommand { get; }

    /// <summary>Status line.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Active download count.</summary>
    public int ActiveCount
    {
        get => _activeCount;
        private set => SetProperty(ref _activeCount, value);
    }

    /// <summary>Failed download count.</summary>
    public int FailedCount
    {
        get => _failedCount;
        private set => SetProperty(ref _failedCount, value);
    }

    /// <summary>Completed download count.</summary>
    public int CompletedCount
    {
        get => _completedCount;
        private set => SetProperty(ref _completedCount, value);
    }

    /// <inheritdoc />
    public Task RefreshAsync()
    {
        Refresh();
        return Task.CompletedTask;
    }

    /// <summary>Rebuilds the list from the engine's queue.</summary>
    private void Refresh()
    {
        var records = _host.Downloads.All();

        Downloads.ReplaceAll(records.Select(record => new DownloadRow(record)));

        ActiveCount = records.Count(record => record.IsActive);
        FailedCount = records.Count(record => record.State == DownloadState.Failed);
        CompletedCount = records.Count(record => record.State == DownloadState.Completed);

        Status = records.Count == 0
            ? "No downloads. Install from an archive, or download from Nexus when it is configured."
            : $"{ActiveCount} active, {CompletedCount} completed, {FailedCount} failed.";
    }

    /// <summary>Pauses a download.</summary>
    /// <param name="row">Row to pause.</param>
    private async Task PauseAsync(DownloadRow row)
    {
        if (row is null)
        {
            return;
        }

        await _host.Downloads.PauseAsync(row.Id).ConfigureAwait(true);
    }

    /// <summary>Resumes a download.</summary>
    /// <param name="row">Row to resume.</param>
    private async Task ResumeAsync(DownloadRow row)
    {
        if (row is null)
        {
            return;
        }

        await _host.Downloads.ResumeAsync(row.Id).ConfigureAwait(true);
    }

    /// <summary>Cancels a download and removes its partial file.</summary>
    /// <param name="row">Row to cancel.</param>
    private async Task CancelAsync(DownloadRow row)
    {
        if (row is null)
        {
            return;
        }

        await _host.Downloads.CancelAsync(row.Id).ConfigureAwait(true);
    }

    /// <summary>Retries a failed download, resetting its attempt counter.</summary>
    /// <param name="row">Row to retry.</param>
    private async Task RetryAsync(DownloadRow row)
    {
        if (row is null)
        {
            return;
        }

        await _host.Downloads.RetryAsync(row.Id).ConfigureAwait(true);
    }

    /// <summary>Removes a download record.</summary>
    /// <param name="row">Row to remove.</param>
    private async Task RemoveAsync(DownloadRow row)
    {
        if (row is null)
        {
            return;
        }

        await _host.Downloads.RemoveAsync(row.Id, deleteFile: false).ConfigureAwait(true);
    }

    /// <summary>Pauses every active download.</summary>
    private async Task PauseAllAsync() => await _host.Downloads.PauseAllAsync().ConfigureAwait(true);
}

/// <summary>A download row.</summary>
public sealed class DownloadRow
{
    /// <summary>Creates a row.</summary>
    /// <param name="record">Engine download record.</param>
    public DownloadRow(DownloadRecord record) => Record = record;

    /// <summary>Engine record.</summary>
    public DownloadRecord Record { get; }

    /// <summary>Job id.</summary>
    public string Id => Record.Id;

    /// <summary>File name.</summary>
    public string FileName => Record.FileName;

    /// <summary>Display name for the mod, when known.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Record.ModNameHint) ? Record.FileName : Record.ModNameHint;

    /// <summary>State badge text.</summary>
    public string StateText => Zh.DownloadState(Record.State);

    /// <summary>State badge colour.</summary>
    public Brush Tone => Palette.ForDownloadState(Record.State);

    /// <summary>State badge fill.</summary>
    public Brush Fill => Palette.FillForDownloadState(Record.State);

    /// <summary>Completion percentage, or a dash when the size is unknown.</summary>
    public string PercentText => Record.Fraction is { } fraction ? $"{fraction * 100:0}%" : "--";

    /// <summary>Progress fraction 0..1.</summary>
    public double Fraction => Record.Fraction ?? 0d;

    /// <summary>True when the total size is known, so a bar is meaningful.</summary>
    public bool HasTotal => Record.TotalBytes is > 0;

    /// <summary>Transferred and total size.</summary>
    public string SizeText => Record.TotalBytes is { } total
        ? $"{FileSystemOps.FormatBytes(Record.BytesDownloaded)} / {FileSystemOps.FormatBytes(total)}"
        : FileSystemOps.FormatBytes(Record.BytesDownloaded);

    /// <summary>Throughput.</summary>
    public string SpeedText => Record.BytesPerSecond <= 0.5d
        ? "--"
        : FileSystemOps.FormatBytes((long)Record.BytesPerSecond) + "/s";

    /// <summary>Estimated time remaining.</summary>
    public string EtaText => Record.Eta is { } eta
        ? eta.TotalHours >= 1
            ? $"{eta.TotalHours:0.#} h remaining"
            : $"{eta.TotalMinutes:0.#} min remaining"
        : "--";

    /// <summary>Last error, when any.</summary>
    public string ErrorText => Record.Error ?? string.Empty;

    /// <summary>True when there is an error to show.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(Record.Error);

    /// <summary>Whether the server supported resuming, which is what makes pause useful.</summary>
    public string ResumeText => Record.ServerSupportsResume ? "Resumable" : "Not resumable";

    /// <summary>Attempt count.</summary>
    public string AttemptsText => Record.Attempts <= 1 ? "First attempt" : $"{Record.Attempts} attempts";

    /// <summary>Verification result.</summary>
    public string HashText => string.IsNullOrWhiteSpace(Record.ActualSha256)
        ? (string.IsNullOrWhiteSpace(Record.ExpectedSha256) ? "未提供校验值" : "尚未校验")
        : "SHA-256 verified";

    /// <summary>True when a pause is possible.</summary>
    public bool CanPause => Record.State is DownloadState.Downloading or DownloadState.Queued or DownloadState.Resolving;

    /// <summary>True when a resume is possible.</summary>
    public bool CanResume => Record.State is DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled;

    /// <summary>True when a retry is meaningful.</summary>
    public bool CanRetry => Record.State is DownloadState.Failed or DownloadState.Cancelled;

    /// <summary>True while the job is active, so cancel is meaningful.</summary>
    public bool CanCancel => Record.IsActive || Record.State == DownloadState.Paused;

    /// <summary>Game the archive is destined for.</summary>
    public string GameText => string.IsNullOrWhiteSpace(Record.GameId) ? "未选择游戏" : Record.GameId;

    /// <summary>Source label.</summary>
    public string SourceText => Record.NexusModId is null ? "Direct URL" : $"Nexus mod {Record.NexusModId}";
}
