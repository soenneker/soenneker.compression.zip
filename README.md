[![](https://img.shields.io/nuget/v/soenneker.compression.zip.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.compression.zip/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.compression.zip/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.compression.zip/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.compression.zip.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.compression.zip/)

# Soenneker.Compression.Zip

High-performance, asynchronous ZIP compression and secure extraction for .NET.

## Features

- Streams file contents asynchronously with configurable buffers
- Atomically replaces completed archive files, preserving the previous archive on failure
- Writes extracted files to temporary files before moving them into place
- Rejects ZIP-slip traversal, links, duplicate destinations, and file/directory collisions
- Supports optional entry-count, expanded-size, and compression-ratio limits for untrusted archives
- Preserves file and directory timestamps
- Supports paths and caller-owned streams
- Uses only the ZIP implementation built into .NET

## Installation

```bash
dotnet add package Soenneker.Compression.Zip
```

## Quick start

```csharp
using Soenneker.Compression.Zip;

var zip = new ZipUtil();

await zip.Compress("./input", "./output/archive.zip", cancellationToken: cancellationToken);
await zip.Extract("./output/archive.zip", "./restored", cancellationToken: cancellationToken);
```

Extraction does not overwrite existing files by default. Opt in when replacement is intended:

```csharp
using Soenneker.Compression.Zip.Options;

await zip.Extract(
    "./archive.zip",
    "./destination",
    new ZipExtractionOptions { OverwriteFiles = true },
    cancellationToken);
```

## Dependency injection

```csharp
using Soenneker.Compression.Zip.Registrars;

services.AddZipUtilAsSingleton();
```

Then inject `IZipUtil`:

```csharp
using Soenneker.Compression.Zip.Abstract;

public sealed class ArchiveService(IZipUtil zip)
{
    public ValueTask Backup(string source, string archive, CancellationToken cancellationToken = default) =>
        zip.Compress(source, archive, cancellationToken: cancellationToken);
}
```

## Compression options

```csharp
using System.IO.Compression;
using Soenneker.Compression.Zip.Options;

var options = new ZipCompressionOptions
{
    CompressionLevel = CompressionLevel.Fastest,
    IncludeBaseDirectory = true,
    Overwrite = true,
    PreserveTimestamps = true,
    BufferSize = 128 * 1024
};

await zip.Compress("./input", "./archive.zip", options, cancellationToken);
```

File-system reparse points are intentionally skipped during compression.

## Extracting untrusted archives

Path and link validation is always enabled. For externally supplied archives, set limits appropriate for your workload to reduce ZIP-bomb risk:

```csharp
var options = new ZipExtractionOptions
{
    MaximumEntryCount = 10_000,
    MaximumEntrySize = 256L * 1024 * 1024,
    MaximumTotalUncompressedSize = 2L * 1024 * 1024 * 1024,
    MaximumCompressionRatio = 200
};

await zip.Extract(uploadStream, "./destination", options, cancellationToken);
```

Caller-owned streams remain open. Archive streams used for extraction must be readable and seekable.
