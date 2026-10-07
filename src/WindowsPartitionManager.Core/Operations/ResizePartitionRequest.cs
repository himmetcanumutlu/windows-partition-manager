namespace WindowsPartitionManager.Core.Operations;

/// <summary>Shrink or extend an existing partition (and the file system on it) to a new size.</summary>
public sealed record ResizePartitionRequest
{
    public required int DiskNumber { get; init; }

    public required int PartitionNumber { get; init; }

    public required ulong NewSizeBytes { get; init; }

    public DiskFingerprint? ExpectedDisk { get; init; }

    public PartitionFingerprint? ExpectedPartition { get; init; }
}
