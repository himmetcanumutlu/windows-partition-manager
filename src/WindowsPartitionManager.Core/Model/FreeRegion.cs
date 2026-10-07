namespace WindowsPartitionManager.Core.Model;

/// <summary>A contiguous unallocated area of a disk where a new partition could be created.</summary>
public sealed record FreeRegion(ulong OffsetBytes, ulong SizeBytes)
{
    /// <summary>Exclusive end offset of the region.</summary>
    public ulong EndBytes => OffsetBytes + SizeBytes;
}
