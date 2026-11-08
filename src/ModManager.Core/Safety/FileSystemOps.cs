using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ModManager.Core.Safety;

/// <summary>
/// Low-level filesystem primitives shared by the whole engine.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix <c>deploy/fsops.rs</c>. We adopt the same primitives and, importantly,
/// the same reason for each: a temp file in the <i>same directory</i> as the target so the rename
/// is same-volume and therefore atomic, and every replacement routed through rename so a
/// half-written file is never observable at the destination.
/// <para>
/// Beyond Modrix we add three Windows-specific hardenings, because Modrix explicitly leaves them
/// out and they are the difference between "works on my machine" and "works on a machine with an
/// antivirus":
/// </para>
/// <list type="number">
/// <item><b>Long paths.</b> Paths are prefixed with <c>\\?\</c> for Win32 calls so paths past
/// <c>MAX_PATH</c> work regardless of the process manifest.</item>
/// <item><b>Sharing-violation retry.</b> Antivirus and the search indexer routinely hold transient
/// handles; a short bounded retry converts a hard failure into a small delay.</item>
/// <item><b>Read-only clearing.</b> A read-only attribute makes rename and delete fail with no
/// useful error; we clear it deliberately rather than treating it as a corrupt file.</item>
/// </list>
/// </remarks>
public static class FileSystemOps
{
    /// <summary>Streaming buffer size. Matches Modrix's 64 KiB, which is a good disk/CPU balance.</summary>
    public const int BufferSize = 64 * 1024;

    /// <summary>Depth bound for any parent walk, mirroring Modrix's <c>MAX_ANCESTOR_DEPTH</c>.</summary>
    public const int MaxAncestorDepth = 256;

    /// <summary>Bounded retry count for sharing violations.</summary>
    private const int SharingViolationRetries = 6;

    /// <summary>Base delay between sharing-violation retries.</summary>
    private static readonly TimeSpan SharingViolationDelay = TimeSpan.FromMilliseconds(60);

    /// <summary>Stem of the sibling temp file used for atomic replacement.</summary>
    private const string TempPrefix = ".mm-tmp.";

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkNative(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>
    /// Converts a path to its extended-length form so Win32 calls are not bound by <c>MAX_PATH</c>.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    public static string ToExtendedPath(string path)
    {
        if (!OperatingSystem.IsWindows() || path.Length < 250)
        {
            return path;
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    /// <summary>Computes a lowercase hex SHA-256 of a file using a streaming read.</summary>
    /// <param name="path">File to hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = OpenForRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Computes a lowercase hex SHA-256 of a file synchronously.</summary>
    /// <param name="path">File to hash.</param>
    public static string HashFile(string path)
    {
        using var stream = OpenForRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>Computes a lowercase hex SHA-256 of a byte array.</summary>
    /// <param name="bytes">Bytes to hash.</param>
    public static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Computes a lowercase hex SHA-256 of a UTF-8 string.</summary>
    /// <param name="text">Text to hash.</param>
    public static string HashText(string text) => HashBytes(Encoding.UTF8.GetBytes(text));

    /// <summary>Opens a file for reading, tolerating a read-only attribute.</summary>
    /// <param name="path">File to open.</param>
    private static FileStream OpenForRead(string path) => new(
        ToExtendedPath(path),
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        BufferSize,
        FileOptions.SequentialScan);

    /// <summary>
    /// Sibling temp path used for atomic replacement. Same directory means same volume, which is
    /// what makes the rename atomic.
    /// </summary>
    /// <param name="targetPath">Path the temp will replace.</param>
    public static string SiblingTempPath(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath) ?? targetPath;
        var name = Path.GetFileName(targetPath);
        return Path.Combine(directory, TempPrefix + name);
    }

    /// <summary>
    /// Writes bytes to a temp sibling, flushes to disk, then atomically renames over the target.
    /// This is the primitive that guarantees a half-written control file is never observed.
    /// </summary>
    /// <param name="targetPath">Destination path.</param>
    /// <param name="bytes">Content to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task WriteAtomicAsync(string targetPath, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(ToExtendedPath(directory));
        }

        var temp = SiblingTempPath(targetPath);
        RemoveIfPresent(temp);

        await using (var stream = new FileStream(
            ToExtendedPath(temp),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            // Flush(true) maps to FlushFileBuffers, the closest managed equivalent of fsync.
            stream.Flush(flushToDisk: true);
        }

        CommitRename(temp, targetPath);
    }

    /// <summary>Writes text atomically as UTF-8.</summary>
    /// <param name="targetPath">Destination path.</param>
    /// <param name="text">Content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task WriteAtomicAsync(string targetPath, string text, CancellationToken cancellationToken = default)
        => WriteAtomicAsync(targetPath, Encoding.UTF8.GetBytes(text), cancellationToken);

    /// <summary>
    /// Renames a temp file over its target, clearing a read-only attribute and retrying transient
    /// sharing violations. On Windows <c>File.Move</c> with overwrite is an atomic replace for
    /// regular files.
    /// </summary>
    /// <param name="tempPath">Prepared temp file.</param>
    /// <param name="targetPath">Destination.</param>
    public static void CommitRename(string tempPath, string targetPath)
    {
        RunWithRetry(() =>
        {
            ClearReadOnly(targetPath);
            File.Move(ToExtendedPath(tempPath), ToExtendedPath(targetPath), overwrite: true);
        });
    }

    /// <summary>
    /// Attempts a hardlink. Returns false instead of throwing when the filesystem or volume refuses,
    /// which is how the applier degrades per file.
    /// </summary>
    /// <param name="sourcePath">Existing staged file.</param>
    /// <param name="targetPath">Link to create.</param>
    public static bool TryCreateHardLink(string sourcePath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            // .NET has no managed hardlink API off Windows; symlink is the next best thing and the
            // applier will fall through to it.
            return false;
        }

        try
        {
            ClearReadOnly(targetPath);
            RemoveIfPresent(targetPath);
            return CreateHardLinkNative(
                ToExtendedPath(targetPath),
                ToExtendedPath(sourcePath),
                IntPtr.Zero);
        }
        catch (Exception ex) when (IsRecoverableIoFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts a file symlink. Returns false when the platform or policy forbids it — most often a
    /// Windows machine without Developer Mode, which is a normal condition, not an error.
    /// </summary>
    /// <param name="sourcePath">Link target.</param>
    /// <param name="targetPath">Link to create.</param>
    public static bool TryCreateSymlink(string sourcePath, string targetPath)
    {
        try
        {
            ClearReadOnly(targetPath);
            RemoveIfPresent(targetPath);
            File.CreateSymbolicLink(ToExtendedPath(targetPath), ToExtendedPath(sourcePath));
            return true;
        }
        catch (Exception ex) when (IsRecoverableIoFailure(ex))
        {
            return false;
        }
    }

    /// <summary>Attempts a directory symlink (junction on Windows).</summary>
    /// <param name="sourcePath">Directory to point at.</param>
    /// <param name="targetPath">Link to create.</param>
    public static bool TryCreateDirectorySymlink(string sourcePath, string targetPath)
    {
        try
        {
            RemoveIfPresent(targetPath);
            Directory.CreateSymbolicLink(ToExtendedPath(targetPath), ToExtendedPath(sourcePath));
            return true;
        }
        catch (Exception ex) when (IsRecoverableIoFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Streams a copy and flushes it to disk. Used as the universal fallback and for backups.
    /// </summary>
    /// <param name="sourcePath">File to copy.</param>
    /// <param name="targetPath">Destination.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task CopyAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(ToExtendedPath(directory));
        }

        await using (var source = OpenForRead(sourcePath))
        await using (var destination = new FileStream(
            ToExtendedPath(targetPath),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous))
        {
            await source.CopyToAsync(destination, BufferSize, cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// Deletes a file if present, treating "not found" as success.
    /// </summary>
    /// <remarks>
    /// Idempotence here is what makes rollback re-entrant: a rollback interrupted halfway can be
    /// re-run safely, because every step that already happened simply succeeds again.
    /// </remarks>
    /// <param name="path">File to delete.</param>
    /// <returns>True when the file is gone afterwards.</returns>
    public static bool RemoveIfPresent(string path)
    {
        try
        {
            if (!File.Exists(ToExtendedPath(path)) && !Directory.Exists(ToExtendedPath(path)))
            {
                return true;
            }

            RunWithRetry(() =>
            {
                ClearReadOnly(path);
                File.Delete(ToExtendedPath(path));
            });
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (IsRecoverableIoFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes a directory tree only when it is a plain directory, never a reparse point.
    /// </summary>
    /// <remarks>
    /// This is deliberately the <i>only</i> recursive directory delete in the codebase, and it
    /// refuses reparse points so a junction planted in staging cannot redirect the delete somewhere
    /// else. Callers must still have validated the path through <c>PathSafetyService</c>.
    /// </remarks>
    /// <param name="directoryPath">Directory to remove.</param>
    /// <returns>True when it is gone afterwards.</returns>
    public static bool RemoveDirectoryTree(string directoryPath)
    {
        try
        {
            var extended = ToExtendedPath(directoryPath);
            if (!Directory.Exists(extended))
            {
                return true;
            }

            var attributes = File.GetAttributes(extended);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // A reparse point is a symlink/junction: delete the link itself, never its target.
                Directory.Delete(extended, recursive: false);
                return true;
            }

            RunWithRetry(() => Directory.Delete(extended, recursive: true));
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (IsRecoverableIoFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a directory and returns the chain actually created, outermost first, so a rollback
    /// can remove exactly what this operation added and nothing else.
    /// </summary>
    /// <param name="directoryPath">Directory to create.</param>
    public static IReadOnlyList<string> EnsureDirectoryChain(string directoryPath)
    {
        var created = new List<string>();
        var pending = new Stack<string>();
        var current = directoryPath;

        while (!string.IsNullOrEmpty(current) && !Directory.Exists(ToExtendedPath(current)))
        {
            pending.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                Directory.CreateDirectory(ToExtendedPath(directory));
                created.Add(directory);
            }
            catch (IOException)
            {
                // Concurrent creation is fine: someone else made it, so it is not ours to remove.
            }
        }

        return created;
    }

    /// <summary>
    /// Walks up from a file, removing directories that became empty, stopping at — and never
    /// removing — <paramref name="stopRoot"/>.
    /// </summary>
    /// <remarks>
    /// DESIGN REFERENCE — Modrix's <c>remove_empty_ancestors</c>, including its termination
    /// conditions. The bound and the <c>stopRoot</c> test are what keep "tidy up the folders I made"
    /// from becoming "delete the user's Data folder": a directory is removed only when
    /// <c>Directory.Delete</c> succeeds on an empty directory, and the walk is clamped.
    /// </remarks>
    /// <param name="childPath">Starting path (usually the deleted file).</param>
    /// <param name="stopRoot">Root that must never be removed.</param>
    /// <returns>Directories actually removed.</returns>
    public static IReadOnlyList<string> RemoveEmptyAncestors(string childPath, string stopRoot)
    {
        var removed = new List<string>();
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopRoot));
        var current = Path.GetDirectoryName(Path.GetFullPath(childPath));

        for (var depth = 0; depth < MaxAncestorDepth && !string.IsNullOrEmpty(current); depth++)
        {
            var normalized = Path.TrimEndingDirectorySeparator(current);
            if (string.Equals(normalized, stop, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!normalized.StartsWith(stop + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            try
            {
                Directory.Delete(ToExtendedPath(normalized), recursive: false);
                removed.Add(normalized);
            }
            catch (IOException)
            {
                // Non-empty or in use: stop. Anything above it is therefore also non-empty.
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }

            current = Path.GetDirectoryName(normalized);
        }

        return removed;
    }

    /// <summary>True when the path exists, following no reparse point.</summary>
    /// <param name="path">Path to test.</param>
    /// <remarks>
    /// Any probe failure — including an unreachable network share, which surfaces as a plain
    /// <see cref="IOException"/> — counts as "not here". The safety gate must fail closed with a
    /// rejection, never by propagating a probe exception out of a validation call.
    /// </remarks>
    public static bool ExistsNoFollow(string path)
    {
        try
        {
            File.GetAttributes(ToExtendedPath(path));
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when the argument is a symlink or junction.</summary>
    /// <param name="path">Path to test.</param>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(ToExtendedPath(path));
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (IsRecoverableIoFailure(ex))
        {
            return false;
        }
    }

    /// <summary>True when the path is a directory and not a reparse point.</summary>
    /// <param name="path">Path to test.</param>
    public static bool IsPlainDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(ToExtendedPath(path));
            return (attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (IsRecoverableIoFailure(ex))
        {
            return false;
        }
    }

    /// <summary>Clears the read-only attribute so rename and delete can proceed.</summary>
    /// <param name="path">Path to make writable.</param>
    public static void ClearReadOnly(string path)
    {
        try
        {
            var extended = ToExtendedPath(path);
            if (!File.Exists(extended) && !Directory.Exists(extended))
            {
                return;
            }

            var attributes = File.GetAttributes(extended);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(extended, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (IsRecoverableIoFailure(ex))
        {
            // Best effort: the caller's own retry or fallback will report a real failure.
        }
    }

    /// <summary>
    /// Runs an IO action, retrying a bounded number of times on a transient sharing violation.
    /// </summary>
    /// <param name="action">Action to run.</param>
    private static void RunWithRetry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < SharingViolationRetries)
            {
                Thread.Sleep(SharingViolationDelay * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < SharingViolationRetries && OperatingSystem.IsWindows())
            {
                // UnauthorizedAccessException on Windows is frequently a read-only or locked file
                // rather than a real permission problem, so a retry is worth one attempt.
                Thread.Sleep(SharingViolationDelay * (attempt + 1));
            }
        }
    }

    /// <summary>True for IO exceptions that can reasonably be transient or downgradable.</summary>
    /// <param name="exception">Exception to classify.</param>
    private static bool IsRecoverableIoFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PlatformNotSupportedException;

    /// <summary>Enumerates files under a directory, skipping our own temp files.</summary>
    /// <param name="root">Directory to enumerate.</param>
    public static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        if (!Directory.Exists(ToExtendedPath(root)))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(ToExtendedPath(root), "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).StartsWith(TempPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }

    /// <summary>Formats a byte count for the UI.</summary>
    /// <param name="bytes">Byte count.</param>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
