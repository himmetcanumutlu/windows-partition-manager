using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class DeleteRulesTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static Disk Disk(string bus, bool system, params Partition[] partitions) => new()
    {
        Number = 1,
        FriendlyName = "Test",
        BusType = bus,
        SizeBytes = 100 * GiB,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 512,
        Style = PartitionStyle.Mbr,
        IsSystem = system,
        IsBoot = system,
        Partitions = partitions,
    };

    private static Partition Part(PartitionKind kind, bool boot = false, ulong used = 0) => new()
    {
        Number = 1,
        OffsetBytes = 1 * MiB,
        SizeBytes = 10 * GiB,
        Kind = kind,
        TypeDescription = kind.ToString(),
        IsBoot = boot,
        IsSystem = boot,
        Volume = used == 0 ? null : new Volume { Path = "v", FileSystem = "exFAT", SizeBytes = 10 * GiB, FreeBytes = 10 * GiB - used, Label = "Stick" },
    };

    [Fact]
    public void ExfatPartitionOnUsbStick_CanBeDeleted_WithDataLossAndUnplugWarnings()
    {
        var partition = Part(PartitionKind.Basic, used: 2 * GiB);
        var disk = Disk("USB", system: false, partition);

        var issues = PartitionPlanner.ValidateDelete(disk, partition);

        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => i.Message.Contains("Back it up", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("Do not unplug", StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsPartition_IsRefused()
    {
        var partition = Part(PartitionKind.Basic, boot: true, used: 1 * GiB);

        var issues = PartitionPlanner.ValidateDelete(Disk("NVMe", system: true, partition), partition);

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("refuses", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PartitionKind.EfiSystem)]
    [InlineData(PartitionKind.MicrosoftReserved)]
    [InlineData(PartitionKind.Recovery)]
    public void BootSupportPartitionsOnSystemDisk_AreRefused(PartitionKind kind)
    {
        var partition = Part(kind);

        var issues = PartitionPlanner.ValidateDelete(Disk("NVMe", system: true, partition), partition);

        Assert.Contains(issues, i => i.IsError);
    }

    [Fact]
    public void RecoveryPartitionOnDataDisk_IsAllowed()
    {
        var partition = Part(PartitionKind.Recovery);

        var issues = PartitionPlanner.ValidateDelete(Disk("SATA", system: false, partition), partition);

        Assert.DoesNotContain(issues, i => i.IsError);
    }

    [Fact]
    public void RemovableDisk_WarnsOnCreateAndResizeToo()
    {
        var partition = Part(PartitionKind.Basic, used: 1 * GiB) with
        {
            Volume = new Volume { Path = "v", FileSystem = "NTFS", SizeBytes = 10 * GiB, FreeBytes = 9 * GiB },
        };
        var disk = Disk("USB", system: false, partition);

        var create = PartitionPlanner.Validate(disk, new CreatePartitionRequest { DiskNumber = 1, OffsetBytes = 10 * GiB + 1 * MiB, SizeBytes = 1 * GiB });
        var resize = ResizePlanner.Validate(disk, partition, new ResizePartitionRequest { DiskNumber = 1, PartitionNumber = 1, NewSizeBytes = 5 * GiB });

        Assert.Contains(create, i => !i.IsError && i.Message.Contains("unplug", StringComparison.Ordinal));
        Assert.Contains(resize, i => !i.IsError && i.Message.Contains("unplug", StringComparison.Ordinal));
        Assert.Empty(SafetyChecks.Disk(Disk("NVMe", system: false)));
    }
}
