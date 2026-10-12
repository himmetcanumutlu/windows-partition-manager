using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Layout;

/// <summary>Builds the ordered partition / free-space segments that a disk map displays.</summary>
public static class DiskLayout
{
    public static IReadOnlyList<DiskSegment> GetSegments(Disk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        var segments = new List<DiskSegment>();

        foreach (var partition in disk.Partitions)
        {
            if (partition.Kind == PartitionKind.Extended)
            {
                continue;
            }

            segments.Add(new DiskSegment(partition.OffsetBytes, partition.SizeBytes, partition));
        }

        foreach (var free in FreeSpaceCalculator.Compute(disk))
        {
            segments.Add(new DiskSegment(free.OffsetBytes, free.SizeBytes, null));
        }

        segments.Sort((a, b) => a.OffsetBytes.CompareTo(b.OffsetBytes));
        return segments;
    }
}
