namespace WindowsPartitionManager.Core.Analysis;

public enum BlockerKind
{
    /// <summary>pagefile.sys / swapfile.sys, held open exclusively by the memory manager.</summary>
    PageFile,

    /// <summary>hiberfil.sys.</summary>
    HibernationFile,

    /// <summary>Volume Shadow Copy / System Restore storage under System Volume Information.</summary>
    ShadowCopyStorage,

    /// <summary>NTFS metadata ($MFT, $LogFile, $Bitmap, ...). Cannot be moved while mounted.</summary>
    NtfsMetadata,

    /// <summary>Any other stream the file system reports as immovable.</summary>
    FileSystemImmovable,
}

/// <summary>A file that Windows will not relocate during a shrink.</summary>
public sealed record ShrinkBlocker(
    string Path,
    BlockerKind Kind,
    string Reason,
    ulong FurthestEndLcn,
    ulong ClustersBeyondTarget,
    ulong TotalClusters)
{
    /// <summary>Blockers that users can remove themselves (page file, hibernation, restore points).</summary>
    public bool IsRemovable => Kind is BlockerKind.PageFile or BlockerKind.HibernationFile or BlockerKind.ShadowCopyStorage;
}

public sealed record ShrinkReport
{
    public required char DriveLetter { get; init; }

    public required VolumeGeometry Geometry { get; init; }

    public VolumeLayoutStats? LayoutStats { get; init; }

    public required ulong UsedBytes { get; init; }

    /// <summary>The boundary the analysis was run against: the volume size we would like to reach.</summary>
    public required ulong TargetSizeBytes { get; init; }

    public required bool TargetWasExplicit { get; init; }

    /// <summary>Volume size if no cluster is moved at all: everything after the last used cluster.</summary>
    public required ulong MinimumSizeWithoutMovingBytes { get; init; }

    /// <summary>Smallest size Windows itself reports (Disk Management's limit), when available.</summary>
    public ulong? WindowsMinimumSizeBytes { get; init; }

    /// <summary>Smallest size reachable once movable files are relocated but every blocker stays put.</summary>
    public required ulong MinimumSizeWithBlockersBytes { get; init; }

    /// <summary>Smallest size reachable once removable blockers are gone; only NTFS metadata and other immovable streams remain.</summary>
    public required ulong MinimumSizeMetadataOnlyBytes { get; init; }

    /// <summary>Blockers whose data reaches past the target, furthest first.</summary>
    public required IReadOnlyList<ShrinkBlocker> BlockersPastTarget { get; init; }

    /// <summary>Blockers that sit entirely below the target and therefore do not matter for it.</summary>
    public required int BlockersBelowTarget { get; init; }

    public required int MovableFilesBeyondTarget { get; init; }

    public required ulong MovableBytesBeyondTarget { get; init; }

    /// <summary>Used clusters past the target that no enumerated file accounts for.</summary>
    public required ulong UnattributedBytesBeyondTarget { get; init; }

    public required IReadOnlyList<string> Advice { get; init; }

    public bool TargetReachableNow => BlockersPastTarget.Count == 0 && UnattributedBytesBeyondTarget == 0;
}
