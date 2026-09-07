using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Compression.Zip.Abstract;
using Soenneker.Compression.Zip.Options;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.ExecutionContexts;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.MemoryStream;
using Soenneker.Utils.Path;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Compression.Zip;

public sealed class ZipUtil : IZipUtil
{
    private static readonly StringComparer _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly SearchValues<char> _invalidFileNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());
    private readonly ILogger<ZipUtil> _logger;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;

    public ZipUtil() : this(NullLogger<ZipUtil>.Instance,
        new Soenneker.Utils.File.FileUtil(NullLogger<Soenneker.Utils.File.FileUtil>.Instance, new MemoryStreamUtil()),
        new Soenneker.Utils.Directory.DirectoryUtil(new PathUtil(), NullLogger<Soenneker.Utils.Directory.DirectoryUtil>.Instance))
    {
    }

    public ZipUtil(ILogger<ZipUtil> logger, IFileUtil fileUtil, IDirectoryUtil directoryUtil)
    {
        _logger = logger;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
    }

    public async ValueTask Compress(string sourceDirectory, string archivePath, ZipCompressionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new ZipCompressionOptions();
        Validate(options);

        string destinationPath = Path.GetFullPath(archivePath);
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (destinationDirectory is not null)
            await _directoryUtil.Create(destinationDirectory, log: false, cancellationToken).ConfigureAwait(false);

        if (!options.Overwrite && await _fileUtil.Exists(destinationPath, cancellationToken).ConfigureAwait(false))
            throw new IOException($"The destination archive already exists: {destinationPath}");

        _logger.LogInformation("Compressing directory {SourceDirectory} to {ArchivePath}", sourceDirectory, destinationPath);

        string fullSourcePath = Path.GetFullPath(sourceDirectory);
        if (options.Overwrite && !IsWithinDirectory(fullSourcePath, destinationPath))
        {
            await _fileUtil.WriteAtomically(destinationPath,
                (destination, ct) => CompressCore(sourceDirectory, destination, options, null, null, ct), log: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string temporaryPath = GetTemporaryPath(destinationPath);
        try
        {
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = options.BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };

            await using (var destination = new FileStream(temporaryPath, streamOptions))
            {
                await CompressCore(sourceDirectory, destination, options, destinationPath, temporaryPath, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, options.Overwrite);
        }
        catch
        {
            await _fileUtil.TryDelete(temporaryPath, log: false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask Compress(string sourceDirectory, Stream destination, ZipCompressionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));

        options ??= new ZipCompressionOptions();
        Validate(options);
        _logger.LogInformation("Compressing directory {SourceDirectory} to a stream", sourceDirectory);
        return CompressCore(sourceDirectory, destination, options, null, null, cancellationToken);
    }

    public async ValueTask Extract(string archivePath, string destinationDirectory, ZipExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new ZipExtractionOptions();
        Validate(options);

        string fullArchivePath = Path.GetFullPath(archivePath);
        _logger.LogInformation("Extracting archive {ArchivePath} to {DestinationDirectory}", fullArchivePath, destinationDirectory);

        await using FileStream archive = _fileUtil.OpenRead(fullArchivePath, log: false);
        await ExtractCore(archive, destinationDirectory, options, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask Extract(Stream archive, string destinationDirectory, ZipExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!archive.CanRead || !archive.CanSeek)
            throw new ArgumentException("The archive stream must be readable and seekable.", nameof(archive));

        options ??= new ZipExtractionOptions();
        Validate(options);
        _logger.LogInformation("Extracting a ZIP stream to {DestinationDirectory}", destinationDirectory);
        return ExtractCore(archive, destinationDirectory, options, cancellationToken);
    }

    private static async ValueTask CompressCore(string sourceDirectory, Stream destination, ZipCompressionOptions options, string? excludedPath,
        string? temporaryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        CompressionPlan plan = await ExecutionContextUtil.RunInlineOrOffload(static state => BuildCompressionPlan(state.SourceDirectory, state.Options, state.Token),
                                                    (SourceDirectory: sourceDirectory, Options: options, Token: cancellationToken), cancellationToken)
                                                .ConfigureAwait(false);

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        if (plan.BaseName is not null && plan.Entries.Count == 0)
            CreateDirectoryEntry(zip, plan.BaseName + '/', plan.Source, options);

        foreach (FileSystemInfo item in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item is FileInfo &&
                ((excludedPath is not null && string.Equals(item.FullName, excludedPath, _pathComparison)) ||
                 (temporaryPath is not null && string.Equals(item.FullName, temporaryPath, _pathComparison))))
                continue;

            string entryName = GetEntryName(plan.Source.FullName, item.FullName, plan.BaseName);
            if (item is DirectoryInfo directory)
            {
                CreateDirectoryEntry(zip, entryName + '/', directory, options);
                continue;
            }

            var file = (FileInfo)item;
            ZipArchiveEntry entry = zip.CreateEntry(entryName, options.CompressionLevel);
            if (options.PreserveTimestamps)
                entry.LastWriteTime = ClampZipTimestamp(file.LastWriteTimeUtc);

            var inputOptions = new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = options.BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };

            await using FileStream input = file.Open(inputOptions);
            await using Stream output = entry.Open();
            await input.CopyToAsync(output, options.BufferSize, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ExtractCore(Stream archiveStream, string destinationDirectory, ZipExtractionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        string rootPath = EnsureTrailingSeparator(Path.GetFullPath(destinationDirectory));
        (ZipArchive Archive, List<ExtractionPlan> Plans) setup = await ExecutionContextUtil.RunInlineOrOffload(static state =>
        {
            var archive = new ZipArchive(state.Stream, ZipArchiveMode.Read, leaveOpen: true);
            try
            {
                return (archive, BuildExtractionPlan(archive, state.RootPath, state.Options, state.Token));
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }, (Stream: archiveStream, RootPath: rootPath, Options: options, Token: cancellationToken), cancellationToken).ConfigureAwait(false);

        using ZipArchive archive = setup.Archive;
        List<ExtractionPlan> plans = setup.Plans;

        await ExecutionContextUtil.RunInlineOrOffload(static state =>
        {
            EnsureDirectoryPath(state.RootPath, state.RootPath);
            PreflightDestination(state.Plans, state.RootPath, state.Options);
        }, (Plans: plans, RootPath: rootPath, Options: options), cancellationToken).ConfigureAwait(false);

        foreach (ExtractionPlan plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (plan.IsDirectory)
            {
                await ExecutionContextUtil.RunInlineOrOffload(static state => EnsureDirectoryPath(state.RootPath, state.DirectoryPath),
                    (RootPath: rootPath, DirectoryPath: plan.DestinationPath), cancellationToken).ConfigureAwait(false);
                continue;
            }

            string parentDirectory = Path.GetDirectoryName(plan.DestinationPath)!;
            await ExecutionContextUtil.RunInlineOrOffload(static state => EnsureDirectoryPath(state.RootPath, state.DirectoryPath),
                (RootPath: rootPath, DirectoryPath: parentDirectory), cancellationToken).ConfigureAwait(false);
            string temporaryPath = GetTemporaryPath(plan.DestinationPath);

            try
            {
                var outputOptions = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = options.BufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                };

                await using (Stream input = plan.Entry.Open())
                await using (var output = new FileStream(temporaryPath, outputOptions))
                {
                    await input.CopyToAsync(output, options.BufferSize, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (options.OverwriteFiles)
                    await _fileUtil.Move(temporaryPath, plan.DestinationPath, log: false, cancellationToken).ConfigureAwait(false);
                else
                    await ExecutionContextUtil.RunInlineOrOffload(static state => File.Move(state.Source, state.Destination, overwrite: false),
                        (Source: temporaryPath, Destination: plan.DestinationPath), cancellationToken).ConfigureAwait(false);

                if (options.PreserveTimestamps)
                    await _fileUtil.SetLastWriteTimeUtc(plan.DestinationPath, plan.LastWriteTime.UtcDateTime, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _fileUtil.TryDelete(temporaryPath, log: false, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        if (options.PreserveTimestamps)
        {
            await ExecutionContextUtil.RunInlineOrOffload(static extractionPlans =>
            {
                foreach (ExtractionPlan plan in extractionPlans.Where(static plan => plan.IsDirectory)
                                                               .OrderByDescending(static plan => plan.DestinationPath.Length))
                    Directory.SetLastWriteTimeUtc(plan.DestinationPath, plan.LastWriteTime.UtcDateTime);
            }, plans, cancellationToken).ConfigureAwait(false);
        }
    }

    private static CompressionPlan BuildCompressionPlan(string sourceDirectory, ZipCompressionOptions options, CancellationToken cancellationToken)
    {
        var source = new DirectoryInfo(Path.GetFullPath(sourceDirectory));
        if (!source.Exists)
            throw new DirectoryNotFoundException($"The source directory does not exist: {source.FullName}");

        if ((source.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The source directory cannot be a reparse point: {source.FullName}");

        string? baseName = options.IncludeBaseDirectory ? source.Name : null;
        if (options.IncludeBaseDirectory && string.IsNullOrEmpty(baseName))
            throw new ArgumentException("A file-system root cannot be included as a base directory.", nameof(sourceDirectory));

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var entries = new List<FileSystemInfo>();
        foreach (FileSystemInfo item in source.EnumerateFileSystemInfos("*", enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(item);
        }

        return new CompressionPlan(source, entries, baseName);
    }

    private static List<ExtractionPlan> BuildExtractionPlan(ZipArchive archive, string rootPath, ZipExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var plans = new List<ExtractionPlan>(archive.Entries.Count);
        var destinations = new HashSet<string>(_pathComparer);
        var fileDestinations = new HashSet<string>(_pathComparer);
        long totalSize = 0;

        if (options.MaximumEntryCount is int maximumEntryCount && archive.Entries.Count > maximumEntryCount)
            throw new InvalidDataException($"The archive contains {archive.Entries.Count} entries, exceeding the configured limit of {maximumEntryCount}.");

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string entryName = entry.FullName;
            if (string.IsNullOrEmpty(entryName))
                continue;

            ValidateEntryName(entryName);

            if (IsLink(entry))
                throw new InvalidDataException($"Archive entry is a link and cannot be extracted safely: {entryName}");

            bool isDirectory = entryName.EndsWith('/') || entryName.EndsWith('\\') || entry.Name.Length == 0;
            string destinationPath = GetSafeDestinationPath(rootPath, entryName);
            if (!destinations.Add(destinationPath))
                throw new InvalidDataException($"Multiple archive entries resolve to the same destination: {entryName}");

            if (!isDirectory)
            {
                long length = entry.Length;
                if (options.MaximumEntrySize is long maximumEntrySize && length > maximumEntrySize)
                    throw new InvalidDataException($"Archive entry exceeds the configured size limit: {entryName}");

                try
                {
                    totalSize = checked(totalSize + length);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException("The archive's total uncompressed size is invalid.", exception);
                }

                if (options.MaximumTotalUncompressedSize is long maximumTotalSize && totalSize > maximumTotalSize)
                    throw new InvalidDataException("The archive exceeds the configured total uncompressed size limit.");

                if (options.MaximumCompressionRatio is double maximumRatio && GetCompressionRatio(entry) > maximumRatio)
                    throw new InvalidDataException($"Archive entry exceeds the configured compression ratio limit: {entryName}");

                fileDestinations.Add(destinationPath);
            }

            plans.Add(new ExtractionPlan(entry, destinationPath, isDirectory, entry.LastWriteTime));
        }

        foreach (string filePath in fileDestinations)
        {
            string? parent = Path.GetDirectoryName(filePath);
            while (parent is not null && parent.StartsWith(rootPath, _pathComparison))
            {
                if (fileDestinations.Contains(parent))
                    throw new InvalidDataException($"An archive entry is both a file and a parent directory: {filePath}");

                parent = Path.GetDirectoryName(parent);
            }
        }

        return plans;
    }

    private static void PreflightDestination(IEnumerable<ExtractionPlan> plans, string rootPath, ZipExtractionOptions options)
    {
        foreach (ExtractionPlan plan in plans)
        {
            string pathToCheck = plan.IsDirectory ? plan.DestinationPath : Path.GetDirectoryName(plan.DestinationPath)!;
            EnsureNoReparsePoints(rootPath, pathToCheck);

            if (plan.IsDirectory)
            {
                if (File.Exists(plan.DestinationPath))
                    throw new IOException($"A file already exists where the archive requires a directory: {plan.DestinationPath}");
            }
            else
            {
                if (Directory.Exists(plan.DestinationPath))
                    throw new IOException($"A directory already exists where the archive requires a file: {plan.DestinationPath}");

                if (File.Exists(plan.DestinationPath))
                {
                    if ((File.GetAttributes(plan.DestinationPath) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException($"The destination is a reparse point: {plan.DestinationPath}");

                    if (!options.OverwriteFiles)
                        throw new IOException($"The destination file already exists: {plan.DestinationPath}");
                }
            }
        }
    }

    private static void EnsureDirectoryPath(string rootPath, string directoryPath)
    {
        Directory.CreateDirectory(rootPath);
        EnsureNoReparsePoints(rootPath, rootPath);

        string relative = Path.GetRelativePath(rootPath, directoryPath);
        if (relative == ".")
            return;

        string current = Path.TrimEndingDirectorySeparator(rootPath);
        foreach (string segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
                throw new IOException($"A file blocks an archive directory: {current}");

            Directory.CreateDirectory(current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"An extraction directory is a reparse point: {current}");
        }
    }

    private static void EnsureNoReparsePoints(string rootPath, string directoryPath)
    {
        string relative = Path.GetRelativePath(rootPath, directoryPath);
        string current = Path.TrimEndingDirectorySeparator(rootPath);

        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"The extraction directory is a reparse point: {current}");

        if (relative == ".")
            return;

        foreach (string segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"An extraction directory is a reparse point: {current}");
        }
    }

    private static string GetSafeDestinationPath(string rootPath, string entryName)
    {
        string normalized = entryName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        string destinationPath = Path.GetFullPath(Path.Combine(rootPath, normalized));
        if (!destinationPath.StartsWith(rootPath, _pathComparison))
            throw new InvalidDataException($"Archive entry path escapes the destination directory: {entryName}");

        return Path.TrimEndingDirectorySeparator(destinationPath);
    }

    private static void ValidateEntryName(string entryName)
    {
        ReadOnlySpan<char> path = entryName.AsSpan();
        foreach (Range range in path.SplitAny("/\\"))
        {
            ReadOnlySpan<char> segment = path[range];
            if (segment.IsEmpty)
                continue;
            if (segment.SequenceEqual(".") || segment.SequenceEqual(".."))
                throw new InvalidDataException($"Archive entry contains a relative path segment: {entryName}");

            if (segment.IndexOfAny(_invalidFileNameChars) >= 0)
                throw new InvalidDataException($"Archive entry contains invalid path characters: {entryName}");

            if (OperatingSystem.IsWindows() && (segment[^1] == ' ' || segment[^1] == '.' || IsWindowsDeviceName(segment)))
                throw new InvalidDataException($"Archive entry contains a Windows-reserved path segment: {entryName}");
        }
    }

    private static bool IsWindowsDeviceName(ReadOnlySpan<char> segment)
    {
        ReadOnlySpan<char> name = Path.GetFileNameWithoutExtension(segment).TrimEnd(" .");
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;

        return name.Length == 4 &&
               (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               name[3] is >= '1' and <= '9';
    }

    private static bool IsLink(ZipArchiveEntry entry)
    {
        int unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        bool isUnixSymbolicLink = unixFileType == 0xA000;
        bool isWindowsReparsePoint = ((FileAttributes)(entry.ExternalAttributes & 0xFFFF) & FileAttributes.ReparsePoint) != 0;
        return isUnixSymbolicLink || isWindowsReparsePoint;
    }

    private static double GetCompressionRatio(ZipArchiveEntry entry)
    {
        if (entry.Length == 0)
            return 0;

        return entry.CompressedLength == 0 ? double.PositiveInfinity : (double)entry.Length / entry.CompressedLength;
    }

    private static string GetEntryName(string rootPath, string itemPath, string? baseName)
    {
        string relativePath = Path.GetRelativePath(rootPath, itemPath).Replace('\\', '/');
        return baseName is null ? relativePath : $"{baseName}/{relativePath}";
    }

    private static void CreateDirectoryEntry(ZipArchive archive, string entryName, FileSystemInfo directory, ZipCompressionOptions options)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        if (options.PreserveTimestamps)
            entry.LastWriteTime = ClampZipTimestamp(directory.LastWriteTimeUtc);
    }

    private static DateTimeOffset ClampZipTimestamp(DateTime value)
    {
        DateTime utc = value.ToUniversalTime();
        if (utc.Year < 1980)
            return new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        if (utc.Year > 2107)
            return new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static string EnsureTrailingSeparator(string path) => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static bool IsWithinDirectory(string directoryPath, string candidatePath) =>
        Path.GetFullPath(candidatePath).StartsWith(EnsureTrailingSeparator(Path.GetFullPath(directoryPath)), _pathComparison);

    private static string GetTemporaryPath(string destinationPath)
    {
        string directory = Path.GetDirectoryName(destinationPath)!;
        return Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
    }

    private static void Validate(ZipCompressionOptions options)
    {
        if (options.BufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.BufferSize, "BufferSize must be greater than zero.");
    }

    private static void Validate(ZipExtractionOptions options)
    {
        if (options.BufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.BufferSize, "BufferSize must be greater than zero.");
        if (options.MaximumEntryCount is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaximumEntryCount, "MaximumEntryCount must be greater than zero.");
        if (options.MaximumEntrySize is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaximumEntrySize, "MaximumEntrySize must be greater than zero.");
        if (options.MaximumTotalUncompressedSize is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaximumTotalUncompressedSize, "MaximumTotalUncompressedSize must be greater than zero.");
        if (options.MaximumCompressionRatio is double maximumCompressionRatio &&
            (maximumCompressionRatio <= 0 || double.IsNaN(maximumCompressionRatio)))
            throw new ArgumentOutOfRangeException(nameof(options), options.MaximumCompressionRatio, "MaximumCompressionRatio must be greater than zero.");
    }

    private sealed record ExtractionPlan(ZipArchiveEntry Entry, string DestinationPath, bool IsDirectory, DateTimeOffset LastWriteTime);

    private sealed record CompressionPlan(DirectoryInfo Source, List<FileSystemInfo> Entries, string? BaseName);
}
