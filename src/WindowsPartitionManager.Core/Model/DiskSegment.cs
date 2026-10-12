namespace WindowsPartitionManager.Core.Model;

/// <summary>
/// One contiguous piece of a disk map: either a partition or a free region.
/// Segments are produced in offset order and together cover the usable part of the disk.
/// </summary>
public sealed record DiskSegment(ulong OffsetBytes, ulong SizeBytes, Partition? Partition)
{
    public bool IsFree => Partition is null;

    public ulong EndBytes => OffsetBytes + SizeBytes;
}
