using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Tests;

public class DiskLayoutTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [Fact]
    public void Segments_InterleavePartitionsAndFreeSpaceInOffsetOrder()
    {
        var disk = new Disk
        {
            Number = 1,
            FriendlyName = "Test",
            BusType = "NVMe",
            SizeBytes = 100 * GiB,
            LogicalSectorSize = 512,
            PhysicalSectorSize = 4096,
            Style = PartitionStyle.Gpt,
            Partitions =
            [
                new Partition { Number = 1, OffsetBytes = 1 * MiB, SizeBytes = 100 * MiB, Kind = PartitionKind.EfiSystem },
                new Partition { Number = 2, OffsetBytes = 50 * GiB, SizeBytes = 10 * GiB, Kind = PartitionKind.Basic },
            ],
        };

        var segments = DiskLayout.GetSegments(disk);

        Assert.Equal(4, segments.Count);
        Assert.False(segments[0].IsFree);
        Assert.True(segments[1].IsFree);
        Assert.False(segments[2].IsFree);
        Assert.True(segments[3].IsFree);

        for (var i = 1; i < segments.Count; i++)
        {
            Assert.True(segments[i].OffsetBytes >= segments[i - 1].EndBytes, "segments must not overlap");
        }
    }
}
