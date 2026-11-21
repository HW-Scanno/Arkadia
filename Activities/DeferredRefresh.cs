using System;
using System.Threading.Tasks;

namespace Arkadia.Activities;

/// <summary>
/// Runs an EXISTING synchronous UI refresh under a REFRESHING scope and lets the status be painted first.
///
/// <para>The refresh keeps running on the calling (UI) thread, unchanged — it is only started one dispatcher turn
/// later. <paramref name="letStatusRender"/> is awaited AFTER the scope begins and BEFORE the refresh starts; the
/// application passes a low-priority dispatcher job there, so the foreground-change notification and the console's
/// visual commit are processed before the dispatcher is blocked. The scope ends on success and on exception alike.</para>
/// </summary>
public static class DeferredRefresh
{
    public static async Task RunAsync(ActivityMonitor monitor, Func<Task> letStatusRender, Action refresh, string? detail = null)
    {
        using var scope = monitor.Begin(ActivityKind.Refreshing, detail);
        await letStatusRender();
        refresh();
    }
}
