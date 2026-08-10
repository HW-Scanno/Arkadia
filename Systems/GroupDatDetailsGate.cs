namespace Arkadia.Systems;

/// <summary>Pure gate for the Group Details "Go to Library" action, so its enablement is unit-testable.</summary>
public static class GroupDatDetailsGate
{
    /// <summary>"Go to Library" is available only once the leaf rows have loaded AND a leaf is selected.</summary>
    public static bool CanGoToLibrary(bool rowsLoaded, bool hasSelection) => rowsLoaded && hasSelection;
}
