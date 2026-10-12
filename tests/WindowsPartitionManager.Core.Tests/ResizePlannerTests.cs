using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class ResizePlannerTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static Disk GptDisk(params Partition[] partitions) => new()
    {
        Number = 1,
        FriendlyName = "Test",
        BusType = "SATA",
        SizeBytes = 100 * GiB,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 4096,
        Style = PartitionStyle.Gpt,
        Partitions = partitions,
    };

    private static Partition Ntfs(int number, ulong offset, ulong size, ulong used, string fs = "NTFS") => new()
    {
        Number = number,
        OffsetBytes = offset,
        SizeBytes = size,
        Kind = PartitionKind.Basic,
        TypeDescription = "Basic data",
        DriveLetter = 'D',
        Volume = new Volume { Path = "v", FileSystem = fs, SizeBytes = size, FreeBytes = size - used, DriveLetter = 'D' },
    };

    private static ResizePartitionRequest Request(ulong newSize) => new() { DiskNumber = 1, PartitionNumber = 1, NewSizeBytes = newSize };

    [Fact]
    public void Shrink_WithinDataAndWindowsLimits_IsAllowed()
    {
        var partition = Ntfs(1, 1 * MiB, 50 * GiB, used: 10 * GiB);
        var disk = GptDisk(partition);

        var issues = ResizePlanner.Validate(disk, partition, Request(20 * GiB), new SupportedSize(15 * GiB, 99 * GiB));

        Assert.DoesNotContain(issues, i => i.IsError);
    }

    [Fact]
    public void Shrink_BelowUsedData_IsRejected()
    {
        var partition = Ntfs(1, 1 * MiB, 50 * GiB, used: 30 * GiB);

        var issues = ResizePlanner.Validate(GptDisk(partition), partition, Request(20 * GiB));

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("cannot shrink below", StringComparison.Ordinal));
    }

    [Fact]
    public void Shrink_BelowWindowsMinimum_ExplainsUnmovableFiles()
    {
        var partition = Ntfs(1, 1 * MiB, 50 * GiB, used: 10 * GiB);

        var issues = ResizePlanner.Validate(GptDisk(partition), partition, Request(20 * GiB), new SupportedSize(30 * GiB, 99 * GiB));

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("unmovable", StringComparison.Ordinal));
    }

    [Fact]
    public void Shrink_LeavingLittleHeadroom_Warns()
    {
        var partition = Ntfs(1, 1 * MiB, 50 * GiB, used: 10 * GiB);

        var issues = ResizePlanner.Validate(GptDisk(partition), partition, Request(10 * GiB + 100 * MiB));

        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => !i.IsError && i.Message.Contains("free", StringComparison.Ordinal));
    }

    [Fact]
    public void Extend_IntoFollowingFreeSpace_IsAllowed_ButNotBeyond()
    {
        var partition = Ntfs(1, 1 * MiB, 50 * GiB, used: 10 * GiB);
        var disk = GptDisk(partition); // free: 50 GiB + 1 MiB .. 100 GiB - 1 MiB

        var ok = ResizePlanner.Validate(disk, partition, Request(80 * GiB));
        var tooBig = ResizePlanner.Validate(disk, partition, Request(100 * GiB));

        Assert.DoesNotContain(ok, i => i.IsError);
        Assert.Contains(tooBig, i => i.IsError && i.Message.Contains("free right after", StringComparison.Ordinal));
        Assert.Equal(100 * GiB - 1 * MiB - 1 * MiB, ResizePlanner.MaximumSize(disk, partition));
    }

    [Fact]
    public void Extend_WhenAnotherPartitionFollows_IsRejected()
    {
        var first = Ntfs(1, 1 * MiB, 50 * GiB, used: 10 * GiB);
        var second = Ntfs(2, 50 * GiB + 1 * MiB, 10 * GiB, used: 1 * GiB);

        var issues = ResizePlanner.Validate(GptDisk(first, second), first, Request(55 * GiB));

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("no unallocated space directly after", StringComparison.Ordinal));
    }

    [Fact]
    public void Fat32Volume_CannotBeResized()
    {
        var partition = Ntfs(1, 1 * MiB, 20 * GiB, used: 1 * GiB, fs: "FAT32");

        Assert.False(ResizePlanner.CanResize(partition));
        Assert.Contains(ResizePlanner.Validate(GptDisk(partition), partition, Request(10 * GiB)), i => i.IsError && i.Message.Contains("NTFS and ReFS", StringComparison.Ordinal));
    }

    [Fact]
    public void SystemPartition_OnlyWarns()
    {
        var partition = Ntfs(1, 1 * MiB, 50 * GiB, used: 10 * GiB) with { IsBoot = true };

        var issues = ResizePlanner.Validate(GptDisk(partition), partition, Request(30 * GiB));

        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => !i.IsError && i.Message.Contains("system volume", StringComparison.Ordinal));
    }

    [Fact]
    public void Normalize_AlignsDownToMegabyte()
    {
        var normalized = ResizePlanner.Normalize(Request(10 * GiB + 700 * 1024));

        Assert.Equal(10 * GiB, normalized.NewSizeBytes);
    }
}
