using System;
using System.IO;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// MOVE-DB-COMMIT-FIX: the catalog location of a moved volume is committed right after a SUCCESSFUL copy + verify
/// (+ source cleanup) and BEFORE the summary popup is awaited. MainWindow cannot run headless, so the ordering and
/// the guard conditions are pinned on the source of the two methods involved; the real behaviour was also checked in
/// an isolated app instance (see the delivery report).
/// </summary>
public sealed class MoveVolumeCommitOrderTests
{
    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    // Line endings are normalized on read: the markers and assertions below are written with "\n", and the working
    // copy may be LF or CRLF depending on Git's settings (core.autocrlf) — the result must be the same either way.
    private static readonly string Code = File.ReadAllText(RepoFile("MainWindow.axaml.cs")).Replace("\r\n", "\n");

    private static string Between(string startMarker, string endMarker)
    {
        var s = Code.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(s >= 0, startMarker);
        var e = Code.IndexOf(endMarker, s + startMarker.Length, StringComparison.Ordinal);
        Assert.True(e > s, endMarker);
        return Code.Substring(s, e - s);
    }

    private static string CopyMoveEngine() => Between("RunCopyMoveAsync(\n", "    /// <summary>\n    /// Runs the Apply step");
    private static string MoveHandler()   => Between("private async void OnMoveVolume(", "// ── Volume Resize");

    [Fact]
    public void Engine_CommitsAfterCleanup_AndBeforeTheSummaryDialogIsFinalizedAndAwaited()
    {
        var e = CopyMoveEngine();
        var cleanup  = e.IndexOf("Directory.Delete(srcFolder, recursive: true)", StringComparison.Ordinal);
        var commit   = e.IndexOf("commitAfterMove(cleanupError)", StringComparison.Ordinal);
        var finalize = e.IndexOf("progDialog.SetCompleted(", StringComparison.Ordinal);
        var awaitDlg = e.IndexOf("await dlgTask;", StringComparison.Ordinal);

        Assert.True(cleanup  > 0);
        Assert.True(commit   > cleanup,  "commit must follow the source cleanup (original order preserved)");
        Assert.True(finalize > commit,   "the summary is shown only after the commit");
        Assert.True(awaitDlg > finalize, "the dialog is awaited last");
        Assert.True(awaitDlg > commit);
    }

    [Fact]
    public void Engine_RunsTheCommitOnlyWhenCopyAndVerifySucceeded()
    {
        var e = CopyMoveEngine();
        Assert.Contains("if (errorMessage is null && commitAfterMove is not null)", e);
        // the verify/copy failure path never reaches the commit: it is inside the guarded block only
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(e, @"commitAfterMove\("));
    }

    [Fact]
    public void Engine_ReportsACommitFailure_InTheDialogAndRethrowsItAfterTheDialogLikeBefore()
    {
        var e = CopyMoveEngine();
        Assert.Contains("progDialog.SetFailed(\n                \"Files were copied and verified, but the catalog location could not be updated: \"", e.Replace("\r\n", "\n"));
        var awaitDlg = e.IndexOf("await dlgTask;", StringComparison.Ordinal);
        var rethrow  = e.IndexOf("ExceptionDispatchInfo.Capture(commitFailure).Throw()", StringComparison.Ordinal);
        Assert.True(rethrow > awaitDlg);                                   // surfaces to OnMoveVolume's catch → "Move Volume Error"
    }

    [Fact]
    public void Handler_HasExactlyOneLocationCommit_InsideTheCallback_AndNoneAfterTheEngineReturns()
    {
        var h = MoveHandler().Replace("\r\n", "\n");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(h, @"_catalog\.SetCurrentLocation\("));

        var callbackStart = h.IndexOf("void CommitMovedLocation(", StringComparison.Ordinal);
        var commit        = h.IndexOf("_catalog.SetCurrentLocation(", StringComparison.Ordinal);
        var engineCall    = h.IndexOf("await RunCopyMoveAsync(", StringComparison.Ordinal);
        var failGuard     = h.IndexOf("if (!success) return;", StringComparison.Ordinal);
        Assert.True(callbackStart > 0 && commit > callbackStart && commit < engineCall);   // commit lives in the callback
        Assert.True(failGuard > engineCall);
        Assert.DoesNotContain("SetCurrentLocation", h.Substring(failGuard));               // nothing registers a path after a failed move
        Assert.Contains("CommitMovedLocation);", h);                                       // callback is handed to the engine
    }

    [Fact]
    public void Callback_KeepsTheOriginalOrder_PruneThenCommit_WithTheSameRecord()
    {
        var h = MoveHandler().Replace("\r\n", "\n");
        var cb = h.Substring(h.IndexOf("void CommitMovedLocation(", StringComparison.Ordinal));
        var prune  = cb.IndexOf("PruneEmptyDirectories(platformDir)", StringComparison.Ordinal);
        var commit = cb.IndexOf("_catalog.SetCurrentLocation(", StringComparison.Ordinal);
        Assert.True(prune > 0 && commit > prune);
        Assert.Contains("if (sourceCleanupError is null)", cb);                            // prune only after a clean source removal
        Assert.Contains("LocationType = dest.DestinationType == Volumes.DestinationType.Workspace", cb);
        Assert.Contains("? \"workspace\" : \"disk\"", cb);
        Assert.Contains("DiskId       = dstDiskId", cb);
        Assert.Contains("IsCurrent    = true", cb);
    }

    [Fact]
    public void UiRefreshAndSummary_StillFollowTheEngine_Unchanged()
    {
        var h = MoveHandler();
        var failGuard = h.IndexOf("if (!success) return;", StringComparison.Ordinal);
        var tail = h.Substring(failGuard);
        Assert.Contains("RefreshVolumes();", tail);
        Assert.Contains("ConvergeSystemsAfterVolumeMutation(entry, assignmentChanged: false)", tail);
        Assert.Contains("new MoveCompleteDialog(", tail);
    }

    [Fact]
    public void EngineIsUsedOnlyByMoveVolume()
    {
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Code, @"await RunCopyMoveAsync\("));
    }

    [Fact]
    public void VolumePathResolver_IsUntouched_WorkspaceFirst()
    {
        var src = File.ReadAllText(RepoFile("DataLayer/VolumePathResolver.cs"));
        var ws   = src.IndexOf("// 1. Workspace path", StringComparison.Ordinal);
        var disk = src.IndexOf("// 2. Mounted disk path", StringComparison.Ordinal);
        Assert.True(ws > 0 && disk > ws);
    }

    [Fact]
    public void NoJournalStagingRecoveryOrLockWasIntroduced()
    {
        var e = CopyMoveEngine() + MoveHandler();
        foreach (var banned in new[] { "journal", "Journal", ".arkadia-move", "recovery", "Recover", "lock (", "Mutex", "SemaphoreSlim" })
            Assert.DoesNotContain(banned, e);
    }
}
