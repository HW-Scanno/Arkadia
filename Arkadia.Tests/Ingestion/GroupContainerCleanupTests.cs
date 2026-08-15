using System;
using System.IO;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Successful-container cleanup for the Group runtime bridge (M9C2B). Deletes only containers recorded as
/// successfully extracted; failed/skipped containers (absent from the list) are never touched. Best-effort with
/// error recording. Temp files only. (The caller only invokes this on a NON-aborted run — that gate is tested
/// at the bridge level.)
/// </summary>
public sealed class GroupContainerCleanupTests : IDisposable
{
    private readonly string _incoming;

    public GroupContainerCleanupTests()
    {
        _incoming = Path.Combine(Path.GetTempPath(), "ark-ccl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_incoming);
    }

    public void Dispose() { try { Directory.Delete(_incoming, true); } catch { } }

    private string Touch(string name)
    {
        var p = Path.Combine(_incoming, name);
        File.WriteAllText(p, "archive");
        return p;
    }

    [Fact] // 24: successfully-extracted container is deleted; a container NOT in the list is left alone
    public void Run_DeletesSuccessfulContainers_LeavesOthers()
    {
        var ok  = Touch("ok.zip");
        var bad = Touch("bad.7z");   // failed/skipped → absent from ExtractedArchiveInfos

        var extracted = new[]
        {
            new ExtractedArchiveInfo(ok, Path.Combine(_incoming, "ok"), Array.Empty<string>()),
        };

        var r = GroupContainerCleanup.Run(extracted, _incoming);

        Assert.Equal(1, r.Deleted);
        Assert.Equal(0, r.Errors);
        Assert.False(File.Exists(ok));    // successful container removed
        Assert.True(File.Exists(bad));    // failed/skipped container preserved
    }

    [Fact] // 25 (implicit): no extracted records → nothing deleted
    public void Run_NoExtracted_NoDeletion()
    {
        var bad = Touch("bad.rar");
        var r = GroupContainerCleanup.Run(Array.Empty<ExtractedArchiveInfo>(), _incoming);
        Assert.Equal(0, r.Deleted);
        Assert.True(File.Exists(bad));
    }

    [Fact] // best-effort: a delete failure is recorded, not thrown (member-outcome independent)
    public void Run_DeleteFailure_RecordedNotThrown()
    {
        var locked = Touch("locked.zip");
        var extracted = new[] { new ExtractedArchiveInfo(locked, Path.Combine(_incoming, "locked"), Array.Empty<string>()) };

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var r = GroupContainerCleanup.Run(extracted, _incoming);
            Assert.Equal(0, r.Deleted);
            Assert.Equal(1, r.Errors);
        }
        Assert.True(File.Exists(locked));
    }
}
