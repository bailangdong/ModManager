using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ModManager.Core.Services;

/// <summary>
/// Resolves a directory to the final path Windows actually uses, so junctions, directory symbolic
/// links, 8.3 short names and trailing-separator/case variants share one lock identity.
/// </summary>
/// <remarks>
/// A lexical <see cref="Path.GetFullPath(string)"/> cannot tell that <c>C:\games\link</c> and
/// <c>C:\games\real</c> are the same directory. Opening a handle without
/// <c>FILE_FLAG_OPEN_REPARSE_POINT</c> follows the reparse point; asking the kernel for the final
/// path then gives the physical target. The handle is closed before returning, so nothing keeps the
/// directory locked.
/// </remarks>
public static class PhysicalPathResolver
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint FileNameNormalized = 0x00000000;
    private const uint VolumeNameDos = 0x00000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    /// <summary>
    /// Attempts to resolve a directory to its final, normalized physical path.
    /// </summary>
    /// <param name="path">Directory to resolve.</param>
    /// <param name="resolved">Normalized final path, upper-cased for lock-key comparison.</param>
    /// <param name="error">A reason when resolution failed; never contains the full path.</param>
    /// <returns>True only when the kernel returned a final path.</returns>
    public static bool TryResolve(string path, out string resolved, out string? error)
    {
        resolved = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "The path is empty.";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "The path is not a valid absolute path.";
            return false;
        }

        if (!Directory.Exists(full))
        {
            error = "The deployment root does not exist or is not a directory.";
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return TryResolveWindows(full, out resolved, out error);
        }

        return TryResolvePortable(full, out resolved, out error);
    }

    private static bool TryResolveWindows(string full, out string resolved, out string? error)
    {
        resolved = string.Empty;
        error = null;

        using var handle = CreateFileW(
            full,
            FileReadAttributes,
            ShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            BackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            error = code is ErrorFileNotFound or ErrorPathNotFound
                ? "The deployment root does not exist."
                : $"The deployment root could not be opened (Win32 error {code}).";
            return false;
        }

        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, VolumeNameDos | FileNameNormalized);
        if (length == 0)
        {
            var code = Marshal.GetLastWin32Error();
            error = $"The final path could not be read (Win32 error {code}).";
            return false;
        }

        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, VolumeNameDos | FileNameNormalized);
            if (length == 0 || length >= buffer.Capacity)
            {
                error = "The final path is too long to resolve safely.";
                return false;
            }
        }

        var final = StripExtendedPrefix(buffer.ToString());
        try
        {
            final = Path.GetFullPath(final);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "The resolved path is not valid.";
            return false;
        }

        resolved = Path.TrimEndingDirectorySeparator(final).ToUpperInvariant();
        if (resolved.Length == 0)
        {
            error = "The resolved path is empty.";
            return false;
        }

        return true;
    }

    private static bool TryResolvePortable(string full, out string resolved, out string? error)
    {
        resolved = string.Empty;
        error = null;

        try
        {
            var info = new DirectoryInfo(full);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            var final = target?.FullName ?? info.FullName;
            resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(final)).ToUpperInvariant();
            return resolved.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "The deployment root could not be resolved.";
            return false;
        }
    }

    private static string StripExtendedPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        const string extendedPrefix = @"\\?\";
        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
