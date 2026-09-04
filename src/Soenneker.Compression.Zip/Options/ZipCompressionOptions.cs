using System.IO.Compression;

namespace Soenneker.Compression.Zip.Options;

/// <summary>
/// Controls ZIP archive creation.
/// </summary>
public sealed class ZipCompressionOptions
{
    /// <summary>
    /// Gets the compression level. The default is <see cref="CompressionLevel.Optimal"/>.
    /// </summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Optimal;

    /// <summary>
    /// Gets whether the source directory itself is included as the archive's top-level directory.
    /// </summary>
    public bool IncludeBaseDirectory { get; init; }

    /// <summary>
    /// Gets whether an existing destination archive may be replaced. The default is <see langword="true"/>.
    /// </summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>
    /// Gets whether source last-write timestamps are stored in the archive. The default is <see langword="true"/>.
    /// </summary>
    public bool PreserveTimestamps { get; init; } = true;

    /// <summary>
    /// Gets the asynchronous file-copy buffer size in bytes. The default is 128 KiB.
    /// </summary>
    public int BufferSize { get; init; } = 128 * 1024;
}
