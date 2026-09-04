namespace Soenneker.Compression.Zip.Options;

/// <summary>
/// Controls safe ZIP extraction and optional resource limits for untrusted archives.
/// </summary>
public sealed class ZipExtractionOptions
{
    /// <summary>
    /// Gets whether existing destination files may be replaced. The default is <see langword="false"/>.
    /// </summary>
    public bool OverwriteFiles { get; init; }

    /// <summary>
    /// Gets whether archive last-write timestamps are applied to extracted files and directories. The default is <see langword="true"/>.
    /// </summary>
    public bool PreserveTimestamps { get; init; } = true;

    /// <summary>
    /// Gets the maximum number of entries allowed, or <see langword="null"/> for no limit.
    /// </summary>
    public int? MaximumEntryCount { get; init; }

    /// <summary>
    /// Gets the maximum uncompressed size of one file in bytes, or <see langword="null"/> for no limit.
    /// </summary>
    public long? MaximumEntrySize { get; init; }

    /// <summary>
    /// Gets the maximum combined uncompressed size in bytes, or <see langword="null"/> for no limit.
    /// </summary>
    public long? MaximumTotalUncompressedSize { get; init; }

    /// <summary>
    /// Gets the maximum allowed uncompressed-to-compressed size ratio for each file, or <see langword="null"/> for no limit.
    /// </summary>
    public double? MaximumCompressionRatio { get; init; }

    /// <summary>
    /// Gets the asynchronous file-copy buffer size in bytes. The default is 128 KiB.
    /// </summary>
    public int BufferSize { get; init; } = 128 * 1024;
}
