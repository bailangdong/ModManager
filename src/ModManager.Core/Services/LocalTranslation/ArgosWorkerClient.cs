using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModManager.Core.Services;

/// <summary>What the worker reported when it came up.</summary>
/// <param name="Package">Installed package directory name.</param>
/// <param name="FromCode">Source language code.</param>
/// <param name="ToCode">Target language code.</param>
/// <param name="PackageVersion">Language pack version.</param>
/// <param name="ArgosVersion">Argos version the pack was built for.</param>
/// <param name="LoadSeconds">How long the model took to load.</param>
public sealed record WorkerReadyInfo(
    string? Package,
    string? FromCode,
    string? ToCode,
    string? PackageVersion,
    string? ArgosVersion,
    double LoadSeconds);

/// <summary>
/// Runs the Python translation worker as a separate process and talks to it over JSON lines.
/// </summary>
/// <remarks>
/// <para>
/// WHY A SEPARATE PROCESS: the engine is CTranslate2 plus SentencePiece, both native libraries with their
/// own runtime requirements, and a WPF process cannot be restarted when a native load goes wrong or a model
/// decides to consume every core. Isolation means a translation failure is a return value here, not a
/// crashed application — and it keeps the online search, paging and image paths completely untouched by
/// anything translation does.
/// </para>
/// <para>
/// WHY ONE LONG-LIVED PROCESS: loading the model costs about a tenth of a second, but paying it per mod
/// would still mean re-reading an 83 MB model file for every title on a page. The process stays up while
/// requests keep arriving and is torn down on dispose, on an idle timeout, or on the first protocol error —
/// a wedged worker is never reused, because a wrong translation is worse than a reported failure.
/// </para>
/// <para>
/// CANCELLATION AND TIMEOUTS: a request and its answer are a single round trip, so there is no safe point to
/// abandon one. Cancelling therefore terminates the worker process and reports honestly; the next call
/// starts a fresh one. Every read is bounded by <see cref="TimeSpan"/> so a hung engine surfaces as an error
/// instead of an interface that never returns.
/// </para>
/// </remarks>
public sealed class ArgosWorkerClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Process _process;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAppLogger? _logger;
    private readonly TimeSpan _timeout;
    private readonly StringBuilder _standardError = new();
    private int _nextId;
    private bool _disposed;

    private ArgosWorkerClient(
        Process process,
        WorkerReadyInfo ready,
        IAppLogger? logger,
        TimeSpan timeout,
        string pythonPath,
        string packageDirectory)
    {
        _process = process;
        _logger = logger;
        _timeout = timeout;
        Ready = ready;
        PythonPath = pythonPath;
        PackageDirectory = packageDirectory;

        _ = DrainStandardErrorAsync(process);
    }

    /// <summary>Information the worker reported at startup.</summary>
    public WorkerReadyInfo Ready { get; }

    /// <summary>Interpreter running the worker.</summary>
    public string PythonPath { get; }

    /// <summary>Language pack the worker loaded.</summary>
    public string PackageDirectory { get; }

    /// <summary>True while the process is alive.</summary>
    public bool IsAlive
    {
        get
        {
            try
            {
                return !_disposed && !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Starts a worker and waits for it to report readiness.
    /// </summary>
    /// <param name="pythonPath">Interpreter to run.</param>
    /// <param name="workerScriptPath">The <c>engine_worker.py</c> file.</param>
    /// <param name="packageDirectory">Installed language pack directory.</param>
    /// <param name="timeout">Budget for one request, and for startup.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<OperationResult<ArgosWorkerClient>> StartAsync(
        string pythonPath,
        string workerScriptPath,
        string packageDirectory,
        TimeSpan timeout,
        IAppLogger? logger,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(pythonPath))
        {
            return OperationResult<ArgosWorkerClient>.Fail($"找不到 Python 解释器：{pythonPath}");
        }

        if (!File.Exists(workerScriptPath))
        {
            return OperationResult<ArgosWorkerClient>.Fail($"找不到翻译引擎脚本：{workerScriptPath}");
        }

        if (!Directory.Exists(packageDirectory))
        {
            return OperationResult<ArgosWorkerClient>.Fail($"找不到语言包目录：{packageDirectory}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(workerScriptPath) ?? AppContext.BaseDirectory,
        };

        startInfo.ArgumentList.Add(workerScriptPath);
        startInfo.ArgumentList.Add("--package-dir");
        startInfo.ArgumentList.Add(packageDirectory);

        // The worker writes Chinese to stdout; without this a non-UTF-8 console code page would
        // corrupt it on the way in.
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start 返回 null。");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return OperationResult<ArgosWorkerClient>.Fail($"无法启动翻译引擎进程：{ex.Message}");
        }

        try
        {
            var line = await ReadLineAsync(process, timeout, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                Kill(process);
                return OperationResult<ArgosWorkerClient>.Fail(
                    "翻译引擎进程没有返回就绪信息就已退出，通常是 Python 依赖不完整。");
            }

            WorkerResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<WorkerResponse>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                Kill(process);
                return OperationResult<ArgosWorkerClient>.Fail($"翻译引擎返回了无法解析的就绪信息：{ex.Message}");
            }

            if (response is null || !response.Ok || response.Fatal == true)
            {
                Kill(process);
                return OperationResult<ArgosWorkerClient>.Fail(
                    response?.Error ?? "翻译引擎未能加载语言包。");
            }

            var ready = new WorkerReadyInfo(
                response.Package,
                response.FromCode,
                response.ToCode,
                response.PackageVersion,
                response.ArgosVersion,
                response.LoadSeconds ?? 0);

            logger?.Info("Translate", $"本地翻译引擎已就绪（{ready.Package} {ready.PackageVersion}）。");

            return OperationResult<ArgosWorkerClient>.Ok(
                new ArgosWorkerClient(process, ready, logger, timeout, pythonPath, packageDirectory));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return OperationResult<ArgosWorkerClient>.Fail("启动翻译引擎被取消或超时。");
        }
    }

    /// <summary>Translates several texts in one round trip.</summary>
    /// <param name="texts">Texts to translate, already protected and segmented.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<OperationResult<IReadOnlyList<string>>> TranslateAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (texts.Count == 0)
        {
            return OperationResult<IReadOnlyList<string>>.Ok([]);
        }

        if (!IsAlive)
        {
            return OperationResult<IReadOnlyList<string>>.Fail("翻译引擎进程已退出，请重试。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var id = Interlocked.Increment(ref _nextId);
            var request = JsonSerializer.Serialize(
                new WorkerRequest(id, "translate", [.. texts]),
                JsonOptions);

            try
            {
                await _process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return OperationResult<IReadOnlyList<string>>.Fail(
                    $"无法向翻译引擎发送请求：{ex.Message}{DescribeStandardError()}");
            }

            string? line;
            try
            {
                line = await ReadLineAsync(_process, _timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Kill(_process);
                return OperationResult<IReadOnlyList<string>>.Fail(
                    $"翻译超时（超过 {_timeout.TotalSeconds:0} 秒未返回），已终止翻译进程。");
            }

            if (line is null)
            {
                return OperationResult<IReadOnlyList<string>>.Fail(
                    $"翻译引擎进程意外退出。{DescribeStandardError()}");
            }

            WorkerResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<WorkerResponse>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                Kill(_process);
                return OperationResult<IReadOnlyList<string>>.Fail($"翻译引擎返回了无法解析的结果：{ex.Message}");
            }

            if (response is null || !response.Ok)
            {
                return OperationResult<IReadOnlyList<string>>.Fail(response?.Error ?? "翻译引擎报告了未知错误。");
            }

            if (response.Translations is null || response.Translations.Count != texts.Count)
            {
                return OperationResult<IReadOnlyList<string>>.Fail(
                    $"翻译引擎返回了 {response.Translations?.Count ?? 0} 条结果，但请求了 {texts.Count} 条。");
            }

            _logger?.Debug("Translate", $"本地引擎翻译 {texts.Count} 段，用时 {response.Seconds:0.000} 秒。");

            return OperationResult<IReadOnlyList<string>>.Ok(response.Translations);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Asks the worker whether it is still healthy.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !IsAlive)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            var request = JsonSerializer.Serialize(new WorkerRequest(id, "ping", null), JsonOptions);

            await _process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            var line = await ReadLineAsync(_process, _timeout, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return false;
            }

            var response = JsonSerializer.Deserialize<WorkerResponse>(line, JsonOptions);
            return response?.Ok == true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException
            or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                // Ask nicely first so the interpreter can unwind its native libraries; kill if it does not.
                try
                {
                    var request = JsonSerializer.Serialize(
                        new WorkerRequest(Interlocked.Increment(ref _nextId), "shutdown", null),
                        JsonOptions);

                    _process.StandardInput.WriteLine(request);
                    _process.StandardInput.Flush();

                    if (!_process.WaitForExit(2000))
                    {
                        Kill(_process);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    Kill(_process);
                }
            }
        }
        finally
        {
            _process.Dispose();
            _gate.Dispose();
        }
    }

    /// <summary>Reads one line, bounded by a timeout so a hung worker cannot block forever.</summary>
    /// <param name="process">Worker process.</param>
    /// <param name="timeout">Budget.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    private static async Task<string?> ReadLineAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var read = process.StandardOutput.ReadLineAsync(linked.Token).AsTask();
        return await read.ConfigureAwait(false);
    }

    /// <summary>Collects the worker's stderr so a failure can quote the real reason.</summary>
    /// <param name="process">Worker process.</param>
    private async Task DrainStandardErrorAsync(Process process)
    {
        try
        {
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                lock (_standardError)
                {
                    if (_standardError.Length < 4000)
                    {
                        _standardError.AppendLine(line);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process went away; nothing to drain.
        }
    }

    /// <summary>Last stderr text, appended to an error message so the cause is visible.</summary>
    private string DescribeStandardError()
    {
        lock (_standardError)
        {
            var text = _standardError.ToString().Trim();
            return text.Length == 0 ? string.Empty : " 引擎输出：" + text;
        }
    }

    /// <summary>Terminates a process without letting a teardown failure escape.</summary>
    /// <param name="process">Process to terminate.</param>
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            // Already gone.
        }
    }

    /// <summary>One request line.</summary>
    /// <param name="Id">Correlation id.</param>
    /// <param name="Cmd">Command name: <c>translate</c>, <c>ping</c> or <c>shutdown</c>.</param>
    /// <param name="Texts">Texts to translate, for a <c>translate</c> request.</param>
    private sealed record WorkerRequest(int Id, string Cmd, IReadOnlyList<string>? Texts);

    /// <summary>One response line.</summary>
    /// <param name="Id">Correlation id.</param>
    /// <param name="Ok">True on success.</param>
    /// <param name="Error">Failure text.</param>
    /// <param name="Fatal">True when the worker could not start at all.</param>
    /// <param name="Pong">Answer to a ping.</param>
    /// <param name="Translations">Translated texts.</param>
    /// <param name="Seconds">Time the engine spent.</param>
    /// <param name="Package">Package directory name.</param>
    /// <param name="FromCode">Source language.</param>
    /// <param name="ToCode">Target language.</param>
    /// <param name="PackageVersion">Language pack version.</param>
    /// <param name="ArgosVersion">Argos version of the pack.</param>
    /// <param name="LoadSeconds">Model load time.</param>
    private sealed record WorkerResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("fatal")] bool? Fatal,
        [property: JsonPropertyName("pong")] bool? Pong,
        [property: JsonPropertyName("translations")] IReadOnlyList<string>? Translations,
        [property: JsonPropertyName("seconds")] double? Seconds,
        [property: JsonPropertyName("package")] string? Package,
        [property: JsonPropertyName("from_code")] string? FromCode,
        [property: JsonPropertyName("to_code")] string? ToCode,
        [property: JsonPropertyName("package_version")] string? PackageVersion,
        [property: JsonPropertyName("argos_version")] string? ArgosVersion,
        [property: JsonPropertyName("load_seconds")] double? LoadSeconds);
}
