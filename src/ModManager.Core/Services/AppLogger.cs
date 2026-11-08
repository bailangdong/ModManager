using System.Collections.Concurrent;
using System.Text;

namespace ModManager.Core.Services;

/// <summary>
/// In-memory ring buffer plus optional file sink.
/// </summary>
/// <remarks>
/// SECURITY: every message passes through <see cref="Redact"/> before it is stored or written. The
/// user's spec §7 forbids leaking credentials, and a log is the most likely place for one to escape
/// — a URL with a query token, an Authorization header echoed in a failure message. Redacting at
/// the single point where entries are created means no call site has to remember.
/// </remarks>
public sealed partial class AppLogger : IAppLogger
{
    private const int DefaultCapacity = 5_000;

    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly int _capacity;
    private readonly string? _logDirectory;
    /// <summary>
    /// Serialises appends to the log file. A plain <see cref="object"/> because
    /// <c>System.Threading.Lock</c> requires .NET 9 and this project targets <c>net8.0</c>.
    /// </summary>
    private readonly object _fileLock = new();
    private readonly LogLevel _minimumLevel;

    /// <summary>Creates a logger.</summary>
    /// <param name="logDirectory">Directory for the rolling text log, or null for memory only.</param>
    /// <param name="minimumLevel">Lowest level that is kept.</param>
    /// <param name="capacity">In-memory entry capacity.</param>
    public AppLogger(string? logDirectory = null, LogLevel minimumLevel = LogLevel.Information, int capacity = DefaultCapacity)
    {
        _logDirectory = logDirectory;
        _minimumLevel = minimumLevel;
        _capacity = capacity;

        if (!string.IsNullOrEmpty(_logDirectory))
        {
            try
            {
                Directory.CreateDirectory(_logDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never be the reason the app fails to start.
                _logDirectory = null;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<LogEntry>? EntryWritten;

    /// <inheritdoc />
    public void Log(LogEntry entry)
    {
        if (entry.Level < _minimumLevel)
        {
            return;
        }

        var safe = Redact(entry);
        _entries.Enqueue(safe);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
            // Trim from the front so the newest entries always survive.
        }

        WriteToFile(safe);
        EntryWritten?.Invoke(this, safe);
    }

    /// <inheritdoc />
    public IReadOnlyList<LogEntry> Recent(int count = 500) =>
        _entries.Reverse().Take(Math.Max(1, count)).Reverse().ToList();

    /// <summary>Removes credential-shaped substrings from an entry.</summary>
    /// <param name="entry">Entry to sanitise.</param>
    public static LogEntry Redact(LogEntry entry)
    {
        var message = RedactText(entry.Message);
        var detail = entry.Detail is null ? null : RedactText(entry.Detail);

        IReadOnlyDictionary<string, string>? context = null;
        if (entry.Context is { Count: > 0 })
        {
            var copy = new Dictionary<string, string>(entry.Context.Count, StringComparer.Ordinal);
            foreach (var (key, value) in entry.Context)
            {
                copy[key] = IsSecretName(key) ? "[redacted]" : RedactText(value);
            }

            context = copy;
        }

        return entry with { Message = message, Detail = detail, Context = context };
    }

    /// <summary>
    /// Redacts values likely to be secrets: <c>key=…</c>, bearer tokens, and query parameters such
    /// as <c>token</c>, <c>api_key</c>, <c>apikey</c> and <c>md5</c>-style download hashes.
    /// </summary>
    /// <param name="text">Text to sanitise.</param>
    public static string RedactText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = SecretPattern().Replace(text, "$1[redacted]");
        result = BearerPattern().Replace(result, "$1[redacted]");
        result = ApiKeyAssignmentPattern().Replace(result, "$1[redacted]");
        return result;
    }

    /// <summary>True when a context key names a secret.</summary>
    /// <param name="name">Key name.</param>
    private static bool IsSecretName(string name) =>
        name.Contains("key", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("cookie", StringComparison.OrdinalIgnoreCase)
        || name.Contains("authorization", StringComparison.OrdinalIgnoreCase);

    [System.Text.RegularExpressions.GeneratedRegex(
        @"([?&](?:token|api_?key|key|access_token|refresh_token|md5|sig|signature)=)[^&\s""']+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SecretPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"(Bearer\s+)\S+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex BearerPattern();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"((?:api[_-]?key|apikey|token|secret|password|authorization)[""']?\s*[:=]\s*[""']?)[^\s""',;]+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ApiKeyAssignmentPattern();

    /// <summary>Appends one line to today's log file, swallowing IO failures.</summary>
    /// <param name="entry">Entry to write.</param>
    private void WriteToFile(LogEntry entry)
    {
        if (string.IsNullOrEmpty(_logDirectory))
        {
            return;
        }

        try
        {
            var path = Path.Combine(_logDirectory, $"modmanager-{DateTimeOffset.UtcNow:yyyyMMdd}.log");
            var builder = new StringBuilder()
                .Append(entry.Timestamp.ToString("O"))
                .Append(' ')
                .Append(entry.Level.ToString().ToUpperInvariant())
                .Append(' ')
                .Append('[').Append(entry.Category).Append("] ")
                .Append(entry.Message);

            if (!string.IsNullOrEmpty(entry.Detail))
            {
                builder.Append(" | ").Append(entry.Detail.ReplaceLineEndings(" "));
            }

            builder.AppendLine();

            // A file append is not on the hot path, so a simple lock is cheaper than async plumbing.
            lock (_fileLock)
            {
                File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never let logging break the application.
        }
    }
}

/// <summary>
/// A logger that discards everything. Useful in tests and before the real logger is constructed.
/// </summary>
public sealed class NullLogger : IAppLogger
{
    /// <summary>Shared instance.</summary>
    public static NullLogger Instance { get; } = new();

    /// <inheritdoc />
    public event EventHandler<LogEntry>? EntryWritten
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public void Log(LogEntry entry)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<LogEntry> Recent(int count = 500) => [];
}
