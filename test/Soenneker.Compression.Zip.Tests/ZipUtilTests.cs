using Soenneker.Compression.Zip.Abstract;
using Soenneker.Compression.Zip.Options;
using Soenneker.Tests.HostedUnit;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Compression.Zip.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class ZipUtilTests : HostedUnitTest
{
    private readonly IZipUtil _util;

    public ZipUtilTests(Host host) : base(host)
    {
        _util = Resolve<IZipUtil>(true);
    }

    [Test]
    public async Task CompressAndExtract_RoundTripsNestedFilesAndEmptyDirectories(CancellationToken cancellationToken)
    {
        string root = CreateTempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string archive = Path.Combine(root, "archive.zip");
        string destination = Path.Combine(root, "destination");

        try
        {
            Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
            await File.WriteAllTextAsync(Path.Combine(source, "root.txt"), "root payload", cancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(source, "nested", "data.bin"), [0, 1, 2, 3, 255], cancellationToken);

            await _util.Compress(source, archive, cancellationToken: cancellationToken);
            await _util.Extract(archive, destination, cancellationToken: cancellationToken);

            await Assert.That(await File.ReadAllTextAsync(Path.Combine(destination, "root.txt"), cancellationToken)).IsEqualTo("root payload");
            await Assert.That(await File.ReadAllBytesAsync(Path.Combine(destination, "nested", "data.bin"), cancellationToken))
                .IsEquivalentTo(new byte[] { 0, 1, 2, 3, 255 });
            await Assert.That(Directory.Exists(Path.Combine(destination, "nested", "empty"))).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Compress_WhenArchiveIsInsideSource_DoesNotArchiveItself(CancellationToken cancellationToken)
    {
        string source = CreateTempDirectory();
        string archivePath = Path.Combine(source, "archive.zip");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "content.txt"), "content", cancellationToken);
            await _util.Compress(source, archivePath, cancellationToken: cancellationToken);

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            await Assert.That(archive.Entries.Select(static entry => entry.FullName)).IsEquivalentTo(new[] { "content.txt" });
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Test]
    public async Task StreamOverloads_LeaveCallerOwnedStreamsOpen(CancellationToken cancellationToken)
    {
        string source = CreateTempDirectory();
        string destination = CreateTempDirectory();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "content.txt"), "content", cancellationToken);
            await using var archive = new MemoryStream();

            await _util.Compress(source, archive, cancellationToken: cancellationToken);
            await Assert.That(archive.CanWrite).IsTrue();

            archive.Position = 0;
            await _util.Extract(archive, destination, cancellationToken: cancellationToken);

            await Assert.That(archive.CanRead).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(destination, "content.txt"), cancellationToken)).IsEqualTo("content");
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(destination, recursive: true);
        }
    }

    [Test]
    public async Task Extract_RejectsPathTraversalBeforeWritingFiles(CancellationToken cancellationToken)
    {
        string root = CreateTempDirectory();
        string destination = Path.Combine(root, "destination");
        string escapedPath = Path.Combine(root, "escaped.txt");

        try
        {
            await using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteEntry(archive, "valid.txt", "valid", cancellationToken);
                await WriteEntry(archive, "../escaped.txt", "escaped", cancellationToken);
            }

            stream.Position = 0;
            async Task Extract() => await _util.Extract(stream, destination, cancellationToken: cancellationToken);

            await Assert.That(Extract).Throws<InvalidDataException>();
            await Assert.That(File.Exists(Path.Combine(destination, "valid.txt"))).IsFalse();
            await Assert.That(File.Exists(escapedPath)).IsFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Extract_EnforcesConfiguredUncompressedSizeLimit(CancellationToken cancellationToken)
    {
        string destination = CreateTempDirectory();

        try
        {
            await using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                await WriteEntry(archive, "large.txt", new string('a', 1024), cancellationToken);

            stream.Position = 0;
            var options = new ZipExtractionOptions { MaximumTotalUncompressedSize = 100 };
            async Task Extract() => await _util.Extract(stream, destination, options, cancellationToken);

            await Assert.That(Extract).Throws<InvalidDataException>();
            await Assert.That(Directory.EnumerateFileSystemEntries(destination).Any()).IsFalse();
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Test]
    public async Task Extract_DoesNotOverwriteByDefault(CancellationToken cancellationToken)
    {
        string destination = CreateTempDirectory();
        string destinationFile = Path.Combine(destination, "content.txt");

        try
        {
            await File.WriteAllTextAsync(destinationFile, "existing", cancellationToken);
            await using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                await WriteEntry(archive, "content.txt", "replacement", cancellationToken);

            stream.Position = 0;
            async Task Extract() => await _util.Extract(stream, destination, cancellationToken: cancellationToken);

            await Assert.That(Extract).Throws<IOException>();
            await Assert.That(await File.ReadAllTextAsync(destinationFile, cancellationToken)).IsEqualTo("existing");
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"soenneker-zip-{Guid.NewGuid():N}");
        return Directory.CreateDirectory(path).FullName;
    }

    private static async Task WriteEntry(ZipArchive archive, string name, string content, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        await using Stream stream = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken);
    }
}
