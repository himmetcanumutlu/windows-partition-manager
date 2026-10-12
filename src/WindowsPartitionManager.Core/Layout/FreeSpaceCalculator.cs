using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Layout;

/// <summary>
/// Computes the unallocated regions of a disk from its partition table.
/// Pure function over the model, so it can be unit tested without touching real hardware.
/// </summary>
public static class FreeSpaceCalculator
{
    /// <summary>Partition alignment used by Windows on all modern disks (1 MiB).</summary>
    public const ulong DefaultAlignment = 1024 * 1024;

    /// <summary>
    /// Gaps smaller than this are hidden. Alignment leaves 1 MiB slivers between partitions
    /// (and before every MBR logical drive, where the EBR lives); Disk Management hides them
    /// too, and nothing useful can be created in a region this small.
    /// </summary>
    public const ulong DefaultMinimumRegionSize = 8 * 1024 * 1024;

    /// <summary>Sectors reserved at the start of a GPT disk: protective MBR + header + 32 table sectors.</summary>
    private const ulong GptPrimarySectors = 34;

    /// <summary>Sectors reserved at the end of a GPT disk: 32 backup table sectors + backup header.</summary>
    private const ulong GptBackupSectors = 33;

    /// <summary>
    /// Windows keeps the last 1 MiB of an MBR basic disk free (room for the LDM database should the
    /// disk ever be converted to dynamic) and refuses partitions that reach into it.
    /// </summary>
    private const ulong MbrTailReserve = 1024 * 1024;

    public static IReadOnlyList<FreeRegion> Compute(
        Disk disk,
        ulong alignment = DefaultAlignment,
        ulong minimumRegionSize = DefaultMinimumRegionSize)
    {
        ArgumentNullException.ThrowIfNull(disk);
        if (alignment == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be positive.");
        }

        if (disk.SizeBytes == 0)
        {
            return [];
        }

        // A disk without a partition table is entirely free, but must be initialized first.
        if (disk.Style is PartitionStyle.Raw or PartitionStyle.Unknown)
        {
            return [new FreeRegion(0, disk.SizeBytes)];
        }

        var usableStart = AlignUp(FirstUsableByte(disk), alignment);
        var usableEnd = LastUsableByteExclusive(disk);
        if (usableEnd <= usableStart)
        {
            return [];
        }

        var regions = new List<FreeRegion>();
        var cursor = usableStart;

        foreach (var partition in disk.Partitions.OrderBy(p => p.OffsetBytes))
        {
            // The extended container overlaps the logical drives inside it; only the logical
            // drives matter for free space, so the container is skipped.
            if (partition.Kind == PartitionKind.Extended)
            {
                continue;
            }

            if (partition.OffsetBytes > cursor)
            {
                AddIfLargeEnough(regions, cursor, partition.OffsetBytes - cursor, minimumRegionSize);
            }

            var nextStart = AlignUp(partition.EndBytes, alignment);
            if (nextStart > cursor)
            {
                cursor = nextStart;
            }
        }

        if (usableEnd > cursor)
        {
            AddIfLargeEnough(regions, cursor, usableEnd - cursor, minimumRegionSize);
        }

        return regions;
    }

    private static void AddIfLargeEnough(List<FreeRegion> regions, ulong offset, ulong size, ulong minimumRegionSize)
    {
        if (size >= minimumRegionSize)
        {
            regions.Add(new FreeRegion(offset, size));
        }
    }

    private static ulong FirstUsableByte(Disk disk) => disk.Style switch
    {
        PartitionStyle.Gpt => GptPrimarySectors * disk.LogicalSectorSize,
        PartitionStyle.Mbr => disk.LogicalSectorSize, // the MBR sector itself
        _ => 0,
    };

    /// <summary>
    /// End of the space partitions may use. On GPT the backup table occupies the last 33 sectors;
    /// the result is then aligned down to 1 MiB because Windows itself never lets a partition end
    /// in the final partial megabyte (CreatePartition rejects it), and this keeps our free-region
    /// sizes equal to what Windows reports as the largest free extent.
    /// </summary>
    private static ulong LastUsableByteExclusive(Disk disk)
    {
        var sector = disk.LogicalSectorSize == 0 ? 512UL : disk.LogicalSectorSize;
        var end = disk.SizeBytes - (disk.SizeBytes % sector);

        if (disk.Style == PartitionStyle.Gpt)
        {
            var backup = GptBackupSectors * sector;
            end = end > backup ? end - backup : 0;
        }
        else if (disk.Style == PartitionStyle.Mbr)
        {
            end = end > MbrTailReserve ? end - MbrTailReserve : 0;
        }

        return end - (end % DefaultAlignment);
    }

    public static ulong AlignUp(ulong value, ulong alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }
}
