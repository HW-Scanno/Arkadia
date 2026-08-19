using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>The pure archive-lifecycle derivation + parent fold. STAGING is intentionally NOT part of this enum —
/// it is verified independent in <see cref="StorageBadgeModelTests"/>.</summary>
public sealed class StorageLifecycleTests
{
    [Fact] // no owned material and nothing unassigned → no badge
    public void Empty_None()
        => Assert.Equal(ArchiveLifecycle.None, StorageLifecycle.Derive(ownedPresentDerived: 0, unassigned: 0, anyLocalVolume: false));

    [Fact] // present derived artifacts not assigned → NEEDS VOLUME
    public void Unassigned_NeedsVolume()
        => Assert.Equal(ArchiveLifecycle.NeedsVolume, StorageLifecycle.Derive(ownedPresentDerived: 10, unassigned: 3, anyLocalVolume: false));

    [Fact] // all owned assigned, a volume still local → READY TO MOVE
    public void OwnedAssignedLocal_ReadyToMove()
        => Assert.Equal(ArchiveLifecycle.ReadyToMove, StorageLifecycle.Derive(ownedPresentDerived: 10, unassigned: 0, anyLocalVolume: true));

    [Fact] // all owned assigned, no local volume → ARCHIVED
    public void OwnedAssignedNoLocal_Archived()
        => Assert.Equal(ArchiveLifecycle.Archived, StorageLifecycle.Derive(ownedPresentDerived: 10, unassigned: 0, anyLocalVolume: false));

    [Fact] // unassigned wins even when a LOCAL volume also exists (earliest unfinished step)
    public void Unassigned_WinsOverLocal()
        => Assert.Equal(ArchiveLifecycle.NeedsVolume, StorageLifecycle.Derive(ownedPresentDerived: 10, unassigned: 1, anyLocalVolume: true));

    [Fact] // a local volume with zero owned artifacts is still None (never a false ARCHIVED/READY TO MOVE)
    public void NoOwnedButLocal_None()
        => Assert.Equal(ArchiveLifecycle.None, StorageLifecycle.Derive(ownedPresentDerived: 0, unassigned: 0, anyLocalVolume: true));

    // ── Parent fold precedence: NEEDS VOLUME > READY TO MOVE > ARCHIVED > None ──

    [Fact]
    public void Fold_AnyNeedsVolume_Wins()
        => Assert.Equal(ArchiveLifecycle.NeedsVolume, StorageLifecycle.Fold(
            new[] { ArchiveLifecycle.Archived, ArchiveLifecycle.ReadyToMove, ArchiveLifecycle.NeedsVolume }));

    [Fact]
    public void Fold_ReadyToMove_OverArchived()
        => Assert.Equal(ArchiveLifecycle.ReadyToMove, StorageLifecycle.Fold(
            new[] { ArchiveLifecycle.Archived, ArchiveLifecycle.ReadyToMove, ArchiveLifecycle.None }));

    [Fact]
    public void Fold_Archived_WhenAllArchivedOrNone()
        => Assert.Equal(ArchiveLifecycle.Archived, StorageLifecycle.Fold(
            new[] { ArchiveLifecycle.None, ArchiveLifecycle.Archived, ArchiveLifecycle.None }));

    [Fact]
    public void Fold_AllNone_None()
        => Assert.Equal(ArchiveLifecycle.None, StorageLifecycle.Fold(
            new[] { ArchiveLifecycle.None, ArchiveLifecycle.None }));

    [Fact]
    public void Fold_Empty_None()
        => Assert.Equal(ArchiveLifecycle.None, StorageLifecycle.Fold(System.Array.Empty<ArchiveLifecycle>()));
}
