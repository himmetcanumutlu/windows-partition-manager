using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Tests;

public class FreeSpaceCalculatorTests
{
    private const ulong KiB = 1024;
    private const ulong MiB = 1024 * KiB;
    private const ulong GiB = 1024 * MiB;
    private const ulong Sector = 512;

    private static Disk MakeDisk(PartitionStyle style, ulong size, params Partition[] partitions) => new()
    {
        Number = 0,
        FriendlyName = "Test disk",
        BusType = "SATA",
        SizeBytes = size,
        LogicalSectorSize = (uint)Sector,
        PhysicalSectorSize = 4096,
        Style = style,
        Partitions = partitions,
    };

    private static Partition MakePartition(int number, ulong offset, ulong size, PartitionKind kind = PartitionKind.Basic) => new()
    {
        Number = number,
        OffsetBytes = offset,
        SizeBytes = size,
        Kind = kind,
    };

    [Fact]
    public void EmptyGptDisk_HasOneRegionBetweenHeaderAndBackupTable()
    {
        var disk = MakeDisk(PartitionStyle.Gpt, 100 * GiB);

        var regions = FreeSpaceCalculator.Compute(disk);

        // The 33 backup-table sectors are reserved, then the end is aligned down to 1 MiB like Windows does.
        var region = Assert.Single(regions);
        Assert.Equal(1 * MiB, region.OffsetBytes);
        Assert.Equal(100 * GiB - 1 * MiB, region.EndBytes);
    }

    [Fact]
    public void EmptyMbrDisk_RegionReachesDiskEnd()
    {
        var disk = MakeDisk(PartitionStyle.Mbr, 100 * GiB);

        var region = Assert.Single(FreeSpaceCalculator.Compute(disk));

        // Windows keeps the last 1 MiB of an MBR disk free for a possible dynamic-disk conversion.
        Assert.Equal(1 * MiB, region.OffsetBytes);
        Assert.Equal(100 * GiB - 1 * MiB, region.EndBytes);
    }

    [Fact]
    public void RawDisk_IsEntirelyFree()
    {
        var disk = MakeDisk(PartitionStyle.Raw, 100 * GiB);

        var region = Assert.Single(FreeSpaceCalculator.Compute(disk));

        Assert.Equal(0UL, region.OffsetBytes);
        Assert.Equal(100 * GiB, region.SizeBytes);
    }

    [Fact]
    public void GapBetweenPartitions_IsReported()
    {
        var disk = MakeDisk(
            PartitionStyle.Gpt,
            100 * GiB,
            MakePartition(1, 1 * MiB, 10 * GiB),
            MakePartition(2, 20 * GiB, 10 * GiB));

        var regions = FreeSpaceCalculator.Compute(disk);

        Assert.Equal(2, regions.Count);
        Assert.Equal(10 * GiB + 1 * MiB, regions[0].OffsetBytes);
        Assert.Equal(20 * GiB, regions[0].EndBytes);
        Assert.Equal(30 * GiB, regions[1].OffsetBytes);
        Assert.Equal(100 * GiB - 1 * MiB, regions[1].EndBytes);
    }

    [Fact]
    public void SliverSmallerThanMinimum_IsIgnored()
    {
        var disk = MakeDisk(
            PartitionStyle.Gpt,
            100 * GiB,
            MakePartition(1, 1 * MiB, 10 * GiB),
            MakePartition(2, 10 * GiB + 1 * MiB + 512 * KiB, 90 * GiB - 2 * MiB));

        var regions = FreeSpaceCalculator.Compute(disk);

        Assert.All(regions, r => Assert.True(r.SizeBytes >= FreeSpaceCalculator.DefaultMinimumRegionSize));
        Assert.DoesNotContain(regions, r => r.OffsetBytes < 50 * GiB);
    }

    [Fact]
    public void UnalignedPartitionEnd_NextRegionStartsOnAlignmentBoundary()
    {
        var disk = MakeDisk(
            PartitionStyle.Gpt,
            100 * GiB,
            MakePartition(1, 1 * MiB, 10 * GiB + 100 * KiB));

        var region = Assert.Single(FreeSpaceCalculator.Compute(disk));

        Assert.Equal(10 * GiB + 2 * MiB, region.OffsetBytes);
    }

    [Fact]
    public void FullDisk_HasNoFreeSpace()
    {
        var end = 100 * GiB - 33 * Sector;
        var disk = MakeDisk(
            PartitionStyle.Gpt,
            100 * GiB,
            MakePartition(1, 1 * MiB, end - 1 * MiB));

        Assert.Empty(FreeSpaceCalculator.Compute(disk));
    }

    [Fact]
    public void ExtendedContainer_IsIgnoredInFavourOfLogicalDrives()
    {
        // The extended container spans the rest of the disk; the logical drive inside it starts
        // 1 MiB later because the EBR sector sits in between. That sliver must not be reported.
        var disk = MakeDisk(
            PartitionStyle.Mbr,
            100 * GiB,
            MakePartition(1, 1 * MiB, 10 * GiB),
            MakePartition(2, 10 * GiB + 1 * MiB, 90 * GiB - 1 * MiB, PartitionKind.Extended),
            MakePartition(3, 10 * GiB + 2 * MiB, 20 * GiB));

        var region = Assert.Single(FreeSpaceCalculator.Compute(disk));

        Assert.Equal(30 * GiB + 2 * MiB, region.OffsetBytes);
        Assert.Equal(100 * GiB - 1 * MiB, region.EndBytes);
    }

    [Fact]
    public void GapAtLeastMinimumSize_IsReported()
    {
        var gap = FreeSpaceCalculator.DefaultMinimumRegionSize;
        var disk = MakeDisk(
            PartitionStyle.Gpt,
            100 * GiB,
            MakePartition(1, 1 * MiB, 10 * GiB),
            MakePartition(2, 10 * GiB + 1 * MiB + gap, 50 * GiB));

        var regions = FreeSpaceCalculator.Compute(disk);

        Assert.Equal(2, regions.Count);
        Assert.Equal(gap, regions[0].SizeBytes);
    }

    [Fact]
    public void OutOfOrderPartitions_AreSortedByOffset()
    {
        var disk = MakeDisk(
            PartitionStyle.Gpt,
            100 * GiB,
            MakePartition(2, 50 * GiB, 10 * GiB),
            MakePartition(1, 1 * MiB, 10 * GiB));

        var regions = FreeSpaceCalculator.Compute(disk);

        Assert.Equal(2, regions.Count);
        Assert.Equal(10 * GiB + 1 * MiB, regions[0].OffsetBytes);
        Assert.Equal(60 * GiB, regions[1].OffsetBytes);
    }

    [Theory]
    [InlineData(0UL, 1UL * MiB, 0UL)]
    [InlineData(1UL, 1UL * MiB, 1UL * MiB)]
    [InlineData(1UL * MiB, 1UL * MiB, 1UL * MiB)]
    [InlineData(1UL * MiB + 1, 1UL * MiB, 2UL * MiB)]
    public void AlignUp_RoundsToNextBoundary(ulong value, ulong alignment, ulong expected)
    {
        Assert.Equal(expected, FreeSpaceCalculator.AlignUp(value, alignment));
    }
}
