using System;
using System.Collections.Generic;

namespace Arkadia.GroupDats;

/// <summary>Severity of a <see cref="GroupDatCreatePresentation"/> (drives the result dialog styling/title).</summary>
public enum GroupDatCreatePresentationKind { Success, Error, Warning }

/// <summary>
/// UI-agnostic view of a finished Group-Create execution: the title/message to show, any cleanup paths the
/// user must act on, and whether the Systems/Library view should be refreshed. Pure data — no Avalonia.
/// </summary>
public sealed record GroupDatCreatePresentation(
    GroupDatCreatePresentationKind Kind,
    string                         Title,
    string                         Message,
    IReadOnlyList<string>          CleanupPaths,
    bool                           ShouldRefresh);

/// <summary>
/// Pure mapping helpers between the executor's typed result and what the UI shows/does. Extracted so the
/// M4 wiring decisions (when to execute, what to display, when to refresh) are unit-testable without any UI.
/// No state, no framework — just deterministic functions.
/// </summary>
public static class GroupDatCreatePresenter
{
    /// <summary>Only a confirmed review of a Create (NewGroup) plan may execute. Cancel/Update never execute.</summary>
    public static bool WillExecute(GroupDatReconciliationMode mode, bool reviewConfirmed)
        => reviewConfirmed && mode == GroupDatReconciliationMode.NewGroup;

    /// <summary>The Systems/Library view is refreshed only when the catalog was actually committed.</summary>
    public static bool ShouldRefresh(GroupDatExecutionResult result)
        => result.OverallStatus == GroupDatExecutionStatus.Committed;

    public static GroupDatCreatePresentation Present(GroupDatExecutionResult result, string groupDisplayName)
    {
        var none = Array.Empty<string>();
        switch (result.OverallStatus)
        {
            case GroupDatExecutionStatus.Committed:
                return new GroupDatCreatePresentation(
                    GroupDatCreatePresentationKind.Success,
                    "Group DAT created",
                    $"Group DAT created successfully.\n\n" +
                    $"Group: {groupDisplayName}\n" +
                    $"Group ID: {result.GroupId}\n" +
                    $"Leaves created: {result.PublishedCount}",
                    none, ShouldRefresh: true);

            case GroupDatExecutionStatus.Cancelled:
                return new GroupDatCreatePresentation(
                    GroupDatCreatePresentationKind.Warning,
                    "Group DAT creation cancelled",
                    "Group DAT creation cancelled.\nNo Group was committed.",
                    none, ShouldRefresh: false);

            case GroupDatExecutionStatus.CleanupRequired:
                return new GroupDatCreatePresentation(
                    GroupDatCreatePresentationKind.Warning,
                    "Manual cleanup required",
                    "Group DAT was not committed, but Arkadia could not clean up all files created by this " +
                    "execution.\n\nManual cleanup is required before retrying. The files listed below belong to " +
                    "this execution and must be removed manually — Arkadia will not delete them for you.",
                    result.CleanupPaths, ShouldRefresh: false);

            case GroupDatExecutionStatus.AbortedNoWrites:
            default:
                var detail = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "" : "\n\n" + result.ErrorMessage;
                return new GroupDatCreatePresentation(
                    GroupDatCreatePresentationKind.Error,
                    "Group DAT not created",
                    $"Group DAT was not created.\nNo catalog changes were committed.{detail}",
                    none, ShouldRefresh: false);
        }
    }
}
