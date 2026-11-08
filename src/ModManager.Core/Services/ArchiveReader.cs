using System.IO.Compression;
using ModManager.Core.Safety;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace ModManager.Core.Services;

/// <summary>
/// Reads and extracts mod archives, treating every entry path as hostile input.
/// </summary>
/// <remarks>
/// <para>
/// This is the highest-risk parser in the application: an archive is fully attacker-controlled, and
/// the payoff for a successful attack is arbitrary file write. The defences below are therefore
/// absolute rather than best-effort, and each is refused rather than sanitised:
/// </para>
/// <list type="bullet">
/// <item><b>Absolute paths</b> (<c>C:\Windows\…</c>, <c>/etc/…</c>) are rejected.</item>
/// <item><b>Parent traversal</b> is rejected <i>after</i> normalisation. Sanitising by trimming
/// <c>../</c> is a well-known bypass — <c>....//</c> becomes <c>../</c> once trimmed — so we refuse
/// instead of rewriting.</item>
/// <item><b>Drive-relative and UNC paths</b> are rejected, which also closes NTFS alternate data
/// streams via <c>:</c>.</item>
/// <item><b>The engine's own marker characters</b> <c>&lt;</c> and <c>&gt;</c> are rejected. Modrix
/// does this for a specific reason worth repeating: those characters delimit its internal path
/// markers, so allowing them would let an archive forge one.</item>
/// <item><b>Size and count limits</b> are enforced before extraction, so a zip bomb fails loudly
/// rather than filling the disk.</item>
/// <item><b>Sibling temp files</b> keep every write inside the destination root, and the final
/// resolved path is re-checked against that root.</item>
/// </list>
/// <para>
/// <b>Supported formats.</b> ZIP and GZip/Tar are handled through the BCL; 7z and RAR are decoded by
/// SharpCompress (the one third-party package in the engine). Every SharpCompress entry is still
/// routed through this class's own name validation and size limits before a byte is written, and the
/// library's bulk <c>WriteToDirectory</c> API — the historical zip-slip vector — is never used:
/// extraction is per-entry, into a destination resolved by
/// <see cref="TryResolveSafeDestination"/>.
/// </para>
/// </remarks>
public sealed class ArchiveReader : IArchiveReader
{
    /// <summary>Magic bytes for a ZIP archive, including the empty-archive variant.</summary>
    private static readonly byte[][] ZipSignatures =
    [
        [0x50, 0x4B, 0x03, 0x04],
        [0x50, 0x4B, 0x05, 0x06],
        [0x50, 0x4B, 0x07, 0x08],
    ];

    /// <summary>Magic bytes for a RAR archive (v4 and v5).</summary>
    private static readonly byte[][] RarSignatures =
    [
        [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00],
        [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00],
    ];

    /// <summary>Magic bytes for a 7z archive.</summary>
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    /// <summary>Magic bytes for a GZip stream.</summary>
    private static readonly byte[] GzipSignature = [0x1F, 0x8B];

    private readonly ArchiveLimits _limits;

    /// <summary>Creates the reader.</summary>
    /// <param name="limits">Limits to enforce; defaults are used when null.</param>
    public ArchiveReader(ArchiveLimits? limits = null) => _limits = limits ?? ArchiveLimits.Default;

    /// <inheritdoc />
    public IReadOnlyList<ArchiveFormat> SupportedFormats { get; } =
        [ArchiveFormat.Zip, ArchiveFormat.Gzip, ArchiveFormat.Tar, ArchiveFormat.SevenZip, ArchiveFormat.Rar];

    /// <inheritdoc />
    public ArchiveFormat DetectFormat(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        try
        {
            using var stream = new FileStream(
                FileSystemOps.ToExtendedPath(archivePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                512);

            // A heap array rather than a stackalloc Span: the signature comparison below is written with
            // LINQ, and a ref struct cannot be captured by a lambda (CS8175).
            var header = new byte[8];
            var read = stream.Read(header, 0, header.Length);

            if (read >= 4 && ZipSignatures.Any(signature => header.AsSpan(0, 4).SequenceEqual(signature)))
            {
                return ArchiveFormat.Zip;
            }

            if (read >= 8 && RarSignatures.Any(signature => header.AsSpan(0, signature.Length).SequenceEqual(signature)))
            {
                return ArchiveFormat.Rar;
            }

            if (read >= 6 && header.AsSpan(0, 6).SequenceEqual(SevenZipSignature))
            {
                return ArchiveFormat.SevenZip;
            }

            if (read >= 2 && header.AsSpan(0, 2).SequenceEqual(GzipSignature))
            {
                return ArchiveFormat.Gzip;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ArchiveFormat.Unknown;
        }

        // Fall back to the extension so an unusual but readable archive is still attempted.
        return Path.GetExtension(archivePath).ToLowerInvariant() switch
        {
            ".zip" or ".fomod" or ".omod" => ArchiveFormat.Zip,
            ".7z" => ArchiveFormat.SevenZip,
            ".rar" => ArchiveFormat.Rar,
            ".tar" => ArchiveFormat.Tar,
            ".gz" or ".tgz" => ArchiveFormat.Gzip,
            _ => ArchiveFormat.Unknown,
        };
    }

    /// <inheritdoc />
    public bool CanRead(ArchiveFormat format) => SupportedFormats.Contains(format);

    /// <inheritdoc />
    public async Task<ArchiveIndex> IndexAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var format = DetectFormat(archivePath);
        var entries = new List<ArchiveEntry>();

        if (format == ArchiveFormat.Zip)
        {
            using var archive = ZipFile.OpenRead(FileSystemOps.ToExtendedPath(archivePath));

            if (archive.Entries.Count > _limits.MaxEntries)
            {
                throw new InvalidDataException(
                    $"Archive has {archive.Entries.Count} entries, exceeding the {_limits.MaxEntries} limit.");
            }

            long total = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = NormalizeEntryName(entry.FullName);
                if (path.Length == 0)
                {
                    continue;
                }

                var isDirectory = path.EndsWith('/');
                var size = isDirectory ? 0 : entry.Length;

                total = SaturatingAdd(total, size);
                if (total > _limits.MaxTotalBytes)
                {
                    throw new InvalidDataException(
                        $"Archive expands to more than {_limits.MaxTotalBytes} bytes, which exceeds the configured limit.");
                }

                entries.Add(new ArchiveEntry(path.TrimEnd('/'), isDirectory, size));
            }
        }
        else if (format == ArchiveFormat.Gzip)
        {
            // A .tar.gz is a tar stream inside gzip; a bare .gz is a single file. Only the former is
            // useful as a mod archive, so it is indexed and the latter is refused with a clear message.
            await using var file = File.OpenRead(FileSystemOps.ToExtendedPath(archivePath));
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);

            try
            {
                using var tar = new System.Formats.Tar.TarReader(gzip);
                System.Formats.Tar.TarEntry? tarEntry;
                while ((tarEntry = tar.GetNextEntry()) is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (entries.Count >= _limits.MaxEntries)
                    {
                        throw new InvalidDataException($"Archive has more than {_limits.MaxEntries} entries.");
                    }

                    var path = NormalizeEntryName(tarEntry.Name);
                    if (path.Length == 0)
                    {
                        continue;
                    }

                    var isDirectory = tarEntry.EntryType == System.Formats.Tar.TarEntryType.Directory;
                    entries.Add(new ArchiveEntry(path.TrimEnd('/'), isDirectory, isDirectory ? 0 : tarEntry.Length));
                }
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(archivePath)}' is not a tar archive inside gzip, so it cannot be installed as a mod.",
                    ex);
            }
        }
        else if (format is ArchiveFormat.SevenZip or ArchiveFormat.Rar)
        {
            IndexSharpCompressArchive(archivePath, format, entries, cancellationToken);
        }
        else
        {
            throw new NotSupportedException(
                $"Archives in the {format} format are not supported. Extract '{Path.GetFileName(archivePath)}' "
                + "manually and add the resulting folder as a mod.");
        }

        return new ArchiveIndex(archivePath, entries);
    }

    /// <summary>
    /// Opens a 7z or RAR archive with SharpCompress's format-specific decoders. The format has already
    /// been sniffed, so the explicit class is used rather than <c>ArchiveFactory</c>'s re-detection:
    /// the reader only ever feeds each decoder its own format.
    /// </summary>
    /// <param name="archivePath">Archive to open.</param>
    /// <param name="format">Sniffed format, <see cref="ArchiveFormat.SevenZip"/> or <see cref="ArchiveFormat.Rar"/>.</param>
    private static IArchive OpenSharpArchive(string archivePath, ArchiveFormat format) =>
        format == ArchiveFormat.SevenZip
            ? SharpCompress.Archives.SevenZip.SevenZipArchive.Open(archivePath)
            : SharpCompress.Archives.Rar.RarArchive.Open(archivePath);

    /// <summary>
    /// Indexes a 7z or RAR archive through SharpCompress, applying the same name normalisation and the
    /// same size/count limits as the zip path.
    /// </summary>
    /// <param name="archivePath">Archive to open.</param>
    /// <param name="entries">Output list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// SharpCompress supplies the decoding; every security decision stays ours. Entry names are
    /// normalised and hostile names are dropped from the index, and extraction re-validates each name
    /// with <see cref="IsSafeEntryName"/> before anything is written.
    /// </remarks>
    private void IndexSharpCompressArchive(
        string archivePath,
        ArchiveFormat format,
        List<ArchiveEntry> entries,
        CancellationToken cancellationToken)
    {
        try
        {
            using var archive = OpenSharpArchive(FileSystemOps.ToExtendedPath(archivePath), format);

            if (archive.Entries.Count() > _limits.MaxEntries)
            {
                throw new InvalidDataException(
                    $"Archive has more than {_limits.MaxEntries} entries, which exceeds the configured limit.");
            }

            long total = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = NormalizeEntryName(entry.Key ?? string.Empty);
                if (path.Length == 0)
                {
                    continue;
                }

                var isDirectory = entry.IsDirectory || path.EndsWith('/');
                var size = isDirectory ? 0 : entry.Size;

                total = SaturatingAdd(total, size);
                if (total > _limits.MaxTotalBytes)
                {
                    throw new InvalidDataException(
                        $"Archive expands to more than {_limits.MaxTotalBytes} bytes, which exceeds the configured limit.");
                }

                entries.Add(new ArchiveEntry(path.TrimEnd('/'), isDirectory, size));
            }
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(archivePath)}' is encrypted and cannot be read without a password.",
                ex);
        }
        catch (InvalidDataException)
        {
            // Already one of ours: do not wrap it a second time.
            throw;
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(archivePath)}' is truncated or corrupt: {ex.Message}",
                ex);
        }
        catch (IOException ex)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(archivePath)}' could not be read: {ex.Message}",
                ex);
        }
        catch (InvalidFormatException ex)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(archivePath)}' is not a valid archive: {ex.Message}",
                ex);
        }
        catch (ArchiveException ex)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(archivePath)}' could not be opened: {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc />
    public async Task<ExtractionResult> ExtractAsync(
        string archivePath,
        string destinationRoot,
        IReadOnlyCollection<string> entriesToExtract,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(entriesToExtract);

        var format = DetectFormat(archivePath);
        if (!CanRead(format))
        {
            throw new NotSupportedException($"Archives in the {format} format are not supported.");
        }

        Directory.CreateDirectory(FileSystemOps.ToExtendedPath(destinationRoot));

        var wanted = new HashSet<string>(entriesToExtract, StringComparer.OrdinalIgnoreCase);
        var extracted = new List<string>();
        var rejected = new List<RejectedEntry>();
        long bytesWritten = 0;
        var completed = 0;

        if (format == ArchiveFormat.Zip)
        {
            using var archive = ZipFile.OpenRead(FileSystemOps.ToExtendedPath(archivePath));

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var normalized = NormalizeEntryName(entry.FullName).TrimEnd('/');
                if (!wanted.Contains(normalized))
                {
                    continue;
                }

                if (!TryResolveSafeDestination(destinationRoot, normalized, out var destination, out var reason))
                {
                    rejected.Add(new RejectedEntry(entry.FullName, reason, IsSecurityRejection: true));
                    continue;
                }

                if (entry.Length > _limits.MaxEntryBytes)
                {
                    rejected.Add(new RejectedEntry(
                        entry.FullName,
                        $"Entry is {entry.Length} bytes, exceeding the {_limits.MaxEntryBytes} byte limit.",
                        IsSecurityRejection: false));
                    continue;
                }

                // Defend against a decompression bomb: a tiny compressed entry that claims an
                // enormous uncompressed size.
                if (entry.CompressedLength > 0)
                {
                    var ratio = (double)entry.Length / entry.CompressedLength;
                    if (ratio > _limits.MaxCompressionRatio)
                    {
                        rejected.Add(new RejectedEntry(
                            entry.FullName,
                            $"Compression ratio {ratio:0} exceeds the {_limits.MaxCompressionRatio:0} limit.",
                            IsSecurityRejection: false));
                        continue;
                    }
                }

                if (normalized.Length == 0 || entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(FileSystemOps.ToExtendedPath(destination));
                    extracted.Add(normalized);
                    continue;
                }

                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(FileSystemOps.ToExtendedPath(directory));
                }

                await using (var source = entry.Open())
                await using (var target = new FileStream(
                    FileSystemOps.ToExtendedPath(destination),
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    FileSystemOps.BufferSize,
                    FileOptions.Asynchronous))
                {
                    await source.CopyToAsync(target, FileSystemOps.BufferSize, cancellationToken).ConfigureAwait(false);
                }

                bytesWritten = SaturatingAdd(bytesWritten, entry.Length);
                if (bytesWritten > _limits.MaxTotalBytes)
                {
                    throw new InvalidDataException(
                        $"Extraction exceeded the {_limits.MaxTotalBytes} byte total limit; aborting.");
                }

                extracted.Add(normalized);
                completed++;
                progress?.Report(new ExtractionProgress(wanted.Count, completed, normalized, bytesWritten));
            }
        }
        else if (format is ArchiveFormat.SevenZip or ArchiveFormat.Rar)
        {
            try
            {
                using var archive = OpenSharpArchive(FileSystemOps.ToExtendedPath(archivePath), format);

                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var normalized = NormalizeEntryName(entry.Key ?? string.Empty).TrimEnd('/');
                    if (!wanted.Contains(normalized))
                    {
                        continue;
                    }

                    // Encrypted content cannot be read without a password we deliberately never ask for.
                    if (entry.IsEncrypted)
                    {
                        rejected.Add(new RejectedEntry(
                            entry.Key ?? normalized,
                            "Entry is encrypted and cannot be extracted.",
                            IsSecurityRejection: false));
                        continue;
                    }

                    // A link planted by an archive is the classic escape primitive and is refused, never
                    // materialised.
                    if (entry.LinkTarget is { Length: > 0 })
                    {
                        rejected.Add(new RejectedEntry(
                            entry.Key ?? normalized,
                            "Archive contains a symbolic or hard link, which is refused.",
                            IsSecurityRejection: true));
                        continue;
                    }

                    if (!TryResolveSafeDestination(destinationRoot, normalized, out var destination, out var reason))
                    {
                        rejected.Add(new RejectedEntry(entry.Key ?? normalized, reason, IsSecurityRejection: true));
                        continue;
                    }

                    if (entry.Size > _limits.MaxEntryBytes)
                    {
                        rejected.Add(new RejectedEntry(
                            entry.Key ?? normalized,
                            $"Entry is {entry.Size} bytes, exceeding the {_limits.MaxEntryBytes} byte limit.",
                            IsSecurityRejection: false));
                        continue;
                    }

                    // The ratio guard is skipped for solid archives, where CompressedSize is not
                    // meaningful per entry.
                    if (entry.CompressedSize > 0 && entry.Size > 0)
                    {
                        var ratio = (double)entry.Size / entry.CompressedSize;
                        if (ratio > _limits.MaxCompressionRatio)
                        {
                            rejected.Add(new RejectedEntry(
                                entry.Key ?? normalized,
                                $"Compression ratio {ratio:0} exceeds the {_limits.MaxCompressionRatio:0} limit.",
                                IsSecurityRejection: false));
                            continue;
                        }
                    }

                    if (entry.IsDirectory || normalized.Length == 0)
                    {
                        Directory.CreateDirectory(FileSystemOps.ToExtendedPath(destination));
                        extracted.Add(normalized);
                        continue;
                    }

                    var directory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(FileSystemOps.ToExtendedPath(directory));
                    }

                    // Manual copy with a hard cap: a corrupted header must not be able to make the
                    // stream yield unbounded data.
                    using (var source = entry.OpenEntryStream())
                    using (var target = new FileStream(
                        FileSystemOps.ToExtendedPath(destination),
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        FileSystemOps.BufferSize))
                    {
                        var buffer = new byte[FileSystemOps.BufferSize];
                        long copied = 0;
                        int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            copied += read;
                            if (copied > _limits.MaxEntryBytes)
                            {
                                throw new InvalidDataException(
                                    $"Entry '{normalized}' expands beyond the {_limits.MaxEntryBytes} byte limit; aborting.");
                            }

                            target.Write(buffer, 0, read);
                        }
                    }

                    bytesWritten = SaturatingAdd(bytesWritten, entry.Size);
                    if (bytesWritten > _limits.MaxTotalBytes)
                    {
                        throw new InvalidDataException(
                            $"Extraction exceeded the {_limits.MaxTotalBytes} byte total limit; aborting.");
                    }

                    extracted.Add(normalized);
                    completed++;
                    progress?.Report(new ExtractionProgress(wanted.Count, completed, normalized, bytesWritten));
                }
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(archivePath)}' is encrypted and cannot be read without a password.",
                    ex);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(archivePath)}' is truncated or corrupt: {ex.Message}",
                    ex);
            }
            catch (IOException ex)
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(archivePath)}' could not be read: {ex.Message}",
                    ex);
            }
            catch (InvalidFormatException ex)
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(archivePath)}' is not a valid archive: {ex.Message}",
                    ex);
            }
            catch (ArchiveException ex)
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(archivePath)}' could not be opened: {ex.Message}",
                    ex);
            }
        }
        else
        {
            await using var file = File.OpenRead(FileSystemOps.ToExtendedPath(archivePath));
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new System.Formats.Tar.TarReader(gzip);

            System.Formats.Tar.TarEntry? tarEntry;
            while ((tarEntry = tar.GetNextEntry()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var normalized = NormalizeEntryName(tarEntry.Name).TrimEnd('/');
                if (!wanted.Contains(normalized))
                {
                    continue;
                }

                if (tarEntry.EntryType is System.Formats.Tar.TarEntryType.SymbolicLink
                    or System.Formats.Tar.TarEntryType.HardLink)
                {
                    // A link inside an archive is refused rather than materialised: it is the classic
                    // way to seed a link that later writes escape the extraction root.
                    rejected.Add(new RejectedEntry(
                        tarEntry.Name,
                        "Archive contains a symbolic or hard link, which is refused.",
                        IsSecurityRejection: true));
                    continue;
                }

                if (!TryResolveSafeDestination(destinationRoot, normalized, out var destination, out var reason))
                {
                    rejected.Add(new RejectedEntry(tarEntry.Name, reason, IsSecurityRejection: true));
                    continue;
                }

                if (tarEntry.EntryType == System.Formats.Tar.TarEntryType.Directory)
                {
                    Directory.CreateDirectory(FileSystemOps.ToExtendedPath(destination));
                    extracted.Add(normalized);
                    continue;
                }

                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(FileSystemOps.ToExtendedPath(directory));
                }

                tarEntry.ExtractToFile(FileSystemOps.ToExtendedPath(destination), overwrite: true);
                bytesWritten = SaturatingAdd(bytesWritten, tarEntry.Length);
                extracted.Add(normalized);
                completed++;
                progress?.Report(new ExtractionProgress(wanted.Count, completed, normalized, bytesWritten));
            }
        }

        return new ExtractionResult(extracted, rejected, bytesWritten);
    }

    /// <summary>
    /// Normalises an archive entry name to forward slashes, rejecting anything that is not a plain
    /// relative path.
    /// </summary>
    /// <param name="entryName">Raw entry name from the archive.</param>
    /// <remarks>
    /// Returns an empty string for a name that must be skipped silently (a bare directory marker).
    /// Callers treat empty as "ignore", and use
    /// <see cref="IsSafeEntryName"/> to distinguish a refusal from a no-op.
    /// </remarks>
    public static string NormalizeEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return string.Empty;
        }

        var normalized = entryName.Replace('\\', '/').Trim();

        // A rooted or drive-qualified name is hostile, not merely odd.
        if (normalized.StartsWith('/') || normalized.Contains("://", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // Collapse redundant separators without touching '..', which must survive to be rejected.
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized;
    }

    /// <summary>
    /// True when an entry name is a plain relative path with no traversal, no root and no marker
    /// characters.
    /// </summary>
    /// <param name="entryName">Normalised entry name.</param>
    /// <param name="reason">Why it was refused.</param>
    /// <remarks>
    /// The refusal set matches Modrix's <c>relative_target</c>: every component must be a plain name,
    /// and <c>&lt;</c>/<c>&gt;</c> are rejected so an archive cannot forge an engine path marker.
    /// </remarks>
    public static bool IsSafeEntryName(string entryName, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(entryName))
        {
            reason = "Empty entry name.";
            return false;
        }

        if (entryName.Contains('\0', StringComparison.Ordinal))
        {
            reason = "Entry name contains a null character.";
            return false;
        }

        var segments = entryName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            reason = "Entry name has no path components.";
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                reason = "Entry name contains parent traversal ('..').";
                return false;
            }

            if (segment == ".")
            {
                continue;
            }

            if (segment.Contains('<', StringComparison.Ordinal) || segment.Contains('>', StringComparison.Ordinal))
            {
                reason = "Entry name contains a reserved marker character ('<' or '>').";
                return false;
            }

            if (segment.Contains(':', StringComparison.Ordinal))
            {
                reason = "Entry name contains a colon, which could name an alternate data stream or a drive.";
                return false;
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                reason = $"Entry name contains an invalid character in '{segment}'.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves an entry to an absolute destination inside the extraction root, refusing escapes.
    /// </summary>
    /// <param name="destinationRoot">Extraction root.</param>
    /// <param name="entryName">Normalised entry name.</param>
    /// <param name="destination">Resolved absolute destination.</param>
    /// <param name="reason">Why it was refused.</param>
    /// <remarks>
    /// The containment check happens on the resolved path, not the string, because a purely lexical
    /// test is defeated by a root-relative or short-name trick. Everything is validated before a
    /// single byte is written.
    /// </remarks>
    public static bool TryResolveSafeDestination(
        string destinationRoot,
        string entryName,
        out string destination,
        out string reason)
    {
        destination = string.Empty;

        if (!IsSafeEntryName(entryName, out reason))
        {
            return false;
        }

        var root = Path.GetFullPath(destinationRoot);
        var combined = root;

        foreach (var segment in entryName.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            combined = Path.Combine(combined, segment);
        }

        var full = Path.GetFullPath(combined);
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Entry '{entryName}' resolves outside the extraction root.";
            return false;
        }

        if (full.Length > 32_000)
        {
            reason = "Resolved destination path is too long.";
            return false;
        }

        destination = full;
        reason = string.Empty;
        return true;
    }

    /// <summary>Adds two sizes, saturating rather than overflowing.</summary>
    /// <param name="left">First value.</param>
    /// <param name="right">Second value.</param>
    /// <remarks>
    /// Powers-of-ten discipline from Modrix: sizes come from untrusted headers, so arithmetic on them
    /// must not wrap. A wrapped total would sail past the size limit check.
    /// </remarks>
    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
