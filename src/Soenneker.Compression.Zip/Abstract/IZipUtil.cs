using Soenneker.Compression.Zip.Options;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Compression.Zip.Abstract;

/// <summary>
/// Creates and safely extracts ZIP archives using asynchronous streaming I/O.
/// </summary>
public interface IZipUtil
{
    /// <summary>
    /// Compresses a directory into a ZIP file. The destination is replaced atomically after the archive has been completed.
    /// </summary>
    /// <param name="sourceDirectory">The directory whose contents will be archived.</param>
    /// <param name="archivePath">The destination ZIP file.</param>
    /// <param name="options">Optional compression behavior. Defaults are used when omitted.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task representing the compression operation.</returns>
    /// <remarks>File-system reparse points are skipped. If the archive is inside the source directory, it is excluded from the archive.</remarks>
    ValueTask Compress(string sourceDirectory, string archivePath, ZipCompressionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compresses a directory into a caller-owned stream.
    /// </summary>
    /// <param name="sourceDirectory">The directory whose contents will be archived.</param>
    /// <param name="destination">A writable destination stream. It remains open after completion.</param>
    /// <param name="options">Optional compression behavior. Defaults are used when omitted.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task representing the compression operation.</returns>
    /// <remarks>File-system reparse points are skipped.</remarks>
    ValueTask Compress(string sourceDirectory, Stream destination, ZipCompressionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Safely extracts a ZIP file into a directory.
    /// </summary>
    /// <param name="archivePath">The ZIP file to extract.</param>
    /// <param name="destinationDirectory">The destination directory, which is created when necessary.</param>
    /// <param name="options">Optional extraction behavior and resource limits. Defaults are used when omitted.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task representing the extraction operation.</returns>
    /// <remarks>
    /// Entry paths are validated before extraction. Links, path traversal, duplicate destinations, and file/directory collisions are rejected.
    /// Each file is moved into place only after it has been fully written.
    /// </remarks>
    ValueTask Extract(string archivePath, string destinationDirectory, ZipExtractionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Safely extracts a ZIP archive from a caller-owned stream into a directory.
    /// </summary>
    /// <param name="archive">A readable, seekable ZIP stream. It remains open after completion.</param>
    /// <param name="destinationDirectory">The destination directory, which is created when necessary.</param>
    /// <param name="options">Optional extraction behavior and resource limits. Defaults are used when omitted.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task representing the extraction operation.</returns>
    /// <remarks>
    /// Entry paths are validated before extraction. Links, path traversal, duplicate destinations, and file/directory collisions are rejected.
    /// Each file is moved into place only after it has been fully written.
    /// </remarks>
    ValueTask Extract(Stream archive, string destinationDirectory, ZipExtractionOptions? options = null,
        CancellationToken cancellationToken = default);
}
