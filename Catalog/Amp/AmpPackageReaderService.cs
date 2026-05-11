using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Arkadia;

public sealed class AmpPackageReaderService
{
    public bool TryReadReleases(string ampFilePath, out IReadOnlyList<AmpReleaseInfo> releases)
    {
        releases = [];
        try
        {
            using var zip   = ZipFile.OpenRead(ampFilePath);
            var       entry = zip.GetEntry("releases.json");
            if (entry is null) return false;

            using var stream = entry.Open();
            using var doc    = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            var list = new List<AmpReleaseInfo>();
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                var media = new List<AmpMediaEntryInfo>();
                if (rel.TryGetProperty("Media", out var mediaEl) &&
                    mediaEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mediaEl.EnumerateArray())
                    {
                        media.Add(new AmpMediaEntryInfo(
                            MediaType:   S(m, "MediaType"),
                            ArchivePath: S(m, "ArchivePath"),
                            FileName:    S(m, "FileName"),
                            Sha256:      S(m, "Sha256"),
                            SizeBytes:   L(m, "SizeBytes"),
                            Preferred:   B(m, "Preferred"),
                            Credits:     Sn(m, "Credits")));
                    }
                }

                list.Add(new AmpReleaseInfo(
                    ReleaseId:       S(rel, "ReleaseId"),
                    DatName:         S(rel, "DatName"),
                    Title:           S(rel, "Title"),
                    OriginalTitle:   S(rel, "OriginalTitle"),
                    SortTitle:       S(rel, "SortTitle"),
                    Developer:       S(rel, "Developer"),
                    Publisher:       S(rel, "Publisher"),
                    Year:            S(rel, "Year"),
                    Languages:       S(rel, "Languages"),
                    AlternateTitles: S(rel, "AlternateTitles"),
                    Description:     S(rel, "Description"),
                    Genre:           S(rel, "Genre"),
                    Subgenre:        S(rel, "Subgenre"),
                    Players:         S(rel, "Players"),
                    ReleaseType:     S(rel, "ReleaseType"),
                    Rating:          S(rel, "Rating"),
                    Media:           media));
            }

            releases = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryReadExclusions(string ampFilePath, out IReadOnlyList<AmpExclusionInfo> exclusions)
    {
        exclusions = [];
        try
        {
            using var zip   = ZipFile.OpenRead(ampFilePath);
            var       entry = zip.GetEntry("curation/exclusions.json");
            if (entry is null) return true;

            using var stream = entry.Open();
            using var doc    = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            var list = new List<AmpExclusionInfo>();
            foreach (var ex in doc.RootElement.EnumerateArray())
            {
                var sha = S(ex, "Sha256");
                if (string.IsNullOrEmpty(sha)) continue;

                list.Add(new AmpExclusionInfo(
                    ReleaseId: S(ex, "ReleaseId"),
                    DatName:   S(ex, "DatName"),
                    MediaType: S(ex, "MediaType"),
                    Sha256:    sha));
            }

            exclusions = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Stream OpenMediaStream(string ampFilePath, string archivePath)
    {
        if (string.IsNullOrEmpty(archivePath))
            throw new ArgumentException("Archive path must not be empty.", nameof(archivePath));

        if (archivePath.Contains('\\'))
            throw new ArgumentException("Archive path must not contain backslashes.", nameof(archivePath));

        if (Path.IsPathRooted(archivePath))
            throw new ArgumentException("Archive path must not be absolute.", nameof(archivePath));

        foreach (var seg in archivePath.Split('/'))
        {
            if (seg == "..")
                throw new ArgumentException("Archive path must not contain '..' segments.", nameof(archivePath));
        }

        if (!archivePath.StartsWith("media/", StringComparison.Ordinal))
            throw new ArgumentException("Archive path must start with 'media/'.", nameof(archivePath));

        var zip = ZipFile.OpenRead(ampFilePath);
        try
        {
            var entry = zip.GetEntry(archivePath)
                ?? throw new FileNotFoundException(
                    $"Entry not found in package: '{archivePath}'", archivePath);

            return new ZipEntryReadStream(zip, entry.Open());
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static string S(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static string? Sn(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : null
            : null;

    private static long L(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt64(out var l) ? l : 0L;

    private static bool B(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    // ── ZipEntryReadStream ────────────────────────────────────────────────────

    private sealed class ZipEntryReadStream : Stream
    {
        private readonly ZipArchive _zip;
        private readonly Stream     _inner;
        private          bool       _disposed;

        public ZipEntryReadStream(ZipArchive zip, Stream inner)
        {
            _zip   = zip;
            _inner = inner;
        }

        public override bool CanRead  => _inner.CanRead;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int  Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush()                                      => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin)         => throw new NotSupportedException();
        public override void SetLength(long value)                        => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)  => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                _inner.Dispose();
                _zip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
