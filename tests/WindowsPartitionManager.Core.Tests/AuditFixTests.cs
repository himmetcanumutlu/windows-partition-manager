using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

/// <summary>Regression tests for the findings of the safety audit.</summary>
public class AuditFixTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static Disk Disk(int number = 0, string name = "ADATA LEGEND 850 LITE", string? serial = "SN1", ulong size = 100 * GiB, bool system = false, params Partition[] partitions) => new()
    {
        Number = number,
        FriendlyName = name,
        SerialNumber = serial,
        BusType = "NVMe",
        SizeBytes = size,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 4096,
        Style = PartitionStyle.Gpt,
        IsSystem = system,
        IsBoot = system,
        Partitions = partitions,
    };

    private static Partition Part(int number, ulong offset, ulong size, PartitionKind kind = PartitionKind.Basic) => new()
    {
        Number = number,
        OffsetBytes = offset,
        SizeBytes = size,
        Kind = kind,
        TypeDescription = kind.ToString(),
    };

    [Fact]
    public void LayoutGuard_AcceptsTheSameDiskAndPartition()
    {
        var partition = Part(3, 10 * GiB, 5 * GiB);
        var disk = Disk(partitions: partition);

        Assert.Null(LayoutGuard.Check(disk, DiskFingerprint.Of(disk), partition, PartitionFingerprint.Of(partition)));
    }

    [Fact]
    public void LayoutGuard_RefusesADifferentDiskUnderTheSameNumber()
    {
        // A USB stick was replaced: disk 1 is now another device.
        var seen = Disk(number: 1, name: "Generic Flash Disk", serial: "AAA", size: 120 * GiB);
        var now = Disk(number: 1, name: "SanDisk Ultra", serial: "BBB", size: 64 * GiB);

        var issue = LayoutGuard.Check(now, DiskFingerprint.Of(seen));

        Assert.NotNull(issue);
        Assert.True(issue!.IsError);
        Assert.Contains("Refresh", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutGuard_RefusesAPartitionThatMovedOrWasRenumbered()
    {
        var seen = Part(3, 10 * GiB, 5 * GiB);
        var renumbered = Part(3, 20 * GiB, 5 * GiB); // number 3 now points at another partition
        var disk = Disk(partitions: renumbered);

        Assert.NotNull(LayoutGuard.Check(disk, DiskFingerprint.Of(disk), renumbered, PartitionFingerprint.Of(seen)));
        Assert.NotNull(LayoutGuard.Check(disk, DiskFingerprint.Of(disk), null, PartitionFingerprint.Of(seen)));
        Assert.NotNull(LayoutGuard.Check(null, DiskFingerprint.Of(disk)));
    }

    [Theory]
    [InlineData(PartitionKind.Other)]
    [InlineData(PartitionKind.Linux)]
    [InlineData(PartitionKind.Unknown)]
    [InlineData(PartitionKind.Ldm)]
    public void SystemDisk_OnlyBasicDataPartitionsCanBeDeleted(PartitionKind kind)
    {
        var oem = Part(6, 50 * GiB, 1 * GiB, kind);
        var data = Part(3, 10 * GiB, 5 * GiB);
        var disk = Disk(system: true, partitions: [oem, data]);

        Assert.Contains(PartitionPlanner.ValidateDelete(disk, oem), i => i.IsError);
        Assert.DoesNotContain(PartitionPlanner.ValidateDelete(disk, data), i => i.IsError);
    }

    [Theory]
    [InlineData("1.500GB")]
    [InlineData("1,500GB")]
    [InlineData("2.000 TB")]
    public void ByteSize_RefusesAmbiguousThousandsSeparators(string text)
    {
        Assert.False(ByteSize.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.5TB", 1536UL * 1024 * 1024 * 1024)]
    [InlineData("1,5TB", 1536UL * 1024 * 1024 * 1024)]
    [InlineData("1.25GB", 1280UL * 1024 * 1024)]
    [InlineData("1500GB", 1500UL * 1024 * 1024 * 1024)]
    public void ByteSize_StillAcceptsUnambiguousDecimals(string text, ulong expected)
    {
        Assert.True(ByteSize.TryParse(text, out var bytes));
        Assert.Equal(expected, bytes);
    }
}
