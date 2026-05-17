using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

public sealed class DerivedArtifactSatisfactionCheckerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DerivedArtifactRecord Artifact(string sha1 = "aabbccdd11223344aabbccdd11223344aabbccdd") =>
        new() { HashedDerivedSha1 = sha1, DerivedSizeBytes = 1024 };

    private static string NonExistentPath() =>
        Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.chd");

    private static string EmptyTempFile()
    {
        var path = Path.GetTempFileName();
        // GetTempFileName creates a 0-byte file — exactly what we need.
        return path;
    }

    private static string NonEmptyTempFile()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03 });
        return path;
    }

    // ── Test 1 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ReleaseNotPresent_ReturnsFalse()
    {
        var result = DerivedArtifactSatisfactionChecker.Check(
            releaseStatus:        "pending",
            artifacts:            [Artifact()],
            expectedPhysicalPath: NonExistentPath());

        Assert.False(result.IsSatisfied);
        Assert.Contains("not 'present'", result.Reason);
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────

    [Fact]
    public void NoArtifactRecord_ReturnsFalse()
    {
        var result = DerivedArtifactSatisfactionChecker.Check(
            releaseStatus:        "present",
            artifacts:            [],
            expectedPhysicalPath: NonExistentPath());

        Assert.False(result.IsSatisfied);
        Assert.Contains("no derived_artifacts row", result.Reason);
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────

    [Fact]
    public void FileMissing_ReturnsFalse()
    {
        var result = DerivedArtifactSatisfactionChecker.Check(
            releaseStatus:        "present",
            artifacts:            [Artifact()],
            expectedPhysicalPath: NonExistentPath());

        Assert.False(result.IsSatisfied);
        Assert.Contains("physical file missing", result.Reason);
    }

    // ── Test 4 ────────────────────────────────────────────────────────────────

    [Fact]
    public void FileEmpty_ReturnsFalse()
    {
        var path = EmptyTempFile();
        try
        {
            var result = DerivedArtifactSatisfactionChecker.Check(
                releaseStatus:        "present",
                artifacts:            [Artifact()],
                expectedPhysicalPath: path);

            Assert.False(result.IsSatisfied);
            Assert.Contains("empty", result.Reason);
        }
        finally { File.Delete(path); }
    }

    // ── Test 5 ────────────────────────────────────────────────────────────────

    [Fact]
    public void DbHasNoSha1_ReturnsFalse()
    {
        var path = NonEmptyTempFile();
        try
        {
            var artifact = Artifact(sha1: "");  // no SHA1 in DB
            var result = DerivedArtifactSatisfactionChecker.Check(
                releaseStatus:        "present",
                artifacts:            [artifact],
                expectedPhysicalPath: path);

            Assert.False(result.IsSatisfied);
            Assert.Contains("no expected SHA1", result.Reason);
        }
        finally { File.Delete(path); }
    }

    // ── Test 6 ────────────────────────────────────────────────────────────────

    [Fact]
    public void HashMismatch_ReturnsFalse()
    {
        var path = NonEmptyTempFile();
        try
        {
            var result = DerivedArtifactSatisfactionChecker.Check(
                releaseStatus:        "present",
                artifacts:            [Artifact(sha1: "aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111")],
                expectedPhysicalPath: path,
                hasher:               _ => "bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222");

            Assert.False(result.IsSatisfied);
            Assert.Contains("hash mismatch", result.Reason);
        }
        finally { File.Delete(path); }
    }

    // ── Test 7 ────────────────────────────────────────────────────────────────

    [Fact]
    public void AllChecksPassed_ReturnsTrue()
    {
        const string hash = "aabbccdd11223344aabbccdd11223344aabbccdd";
        var path = NonEmptyTempFile();
        try
        {
            var result = DerivedArtifactSatisfactionChecker.Check(
                releaseStatus:        "present",
                artifacts:            [Artifact(sha1: hash)],
                expectedPhysicalPath: path,
                hasher:               _ => hash);

            Assert.True(result.IsSatisfied);
            Assert.NotNull(result.Artifact);
        }
        finally { File.Delete(path); }
    }

    // ── Test 8 ────────────────────────────────────────────────────────────────

    [Fact]
    public void HashComparison_IsCaseInsensitive()
    {
        const string lower = "abcdef1234567890abcdef1234567890abcdef12";
        const string upper = "ABCDEF1234567890ABCDEF1234567890ABCDEF12";
        var path = NonEmptyTempFile();
        try
        {
            var result = DerivedArtifactSatisfactionChecker.Check(
                releaseStatus:        "present",
                artifacts:            [Artifact(sha1: upper)],
                expectedPhysicalPath: path,
                hasher:               _ => lower);

            Assert.True(result.IsSatisfied, "Hash comparison must be case-insensitive");
        }
        finally { File.Delete(path); }
    }
}
