using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class PartitionPlannerTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static Disk GptDisk(ulong size, params Partition[] partitions) => new()
    {
        Number = 1,
        FriendlyName = "Test",
        BusType = "SATA",
        SizeBytes = size,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 4096,
        Style = PartitionStyle.Gpt,
        Partitions = partitions,
    };

    private static Partition Basic(int number, ulong offset, ulong size, char? letter = null) => new()
    {
        Number = number,
        OffsetBytes = offset,
        SizeBytes = size,
        Kind = PartitionKind.Basic,
        DriveLetter = letter,
    };

    private static CreatePartitionRequest Request(ulong offset, ulong size, string fs = "NTFS", char? letter = 'E') => new()
    {
        DiskNumber = 1,
        OffsetBytes = offset,
        SizeBytes = size,
        FileSystem = fs,
        DriveLetter = letter,
    };

    [Fact]
    public void ValidRequestInFreeSpace_HasNoErrors()
    {
        var disk = GptDisk(100 * GiB, Basic(1, 1 * MiB, 10 * GiB, 'C'));

        var issues = PartitionPlanner.Validate(disk, Request(10 * GiB + 1 * MiB, 20 * GiB), new HashSet<char> { 'C' });

        Assert.DoesNotContain(issues, i => i.IsError);
    }

    [Fact]
    public void RequestOverlappingPartition_IsRejected()
    {
        var disk = GptDisk(100 * GiB, Basic(1, 1 * MiB, 10 * GiB));

        var issues = PartitionPlanner.Validate(disk, Request(5 * GiB, 1 * GiB));

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("not inside unallocated", StringComparison.Ordinal));
    }

    [Fact]
    public void RequestTooLargeForRegion_SaysHowMuchFits()
    {
        var disk = GptDisk(100 * GiB, Basic(1, 1 * MiB, 10 * GiB));

        var issues = PartitionPlanner.Validate(disk, Request(10 * GiB + 1 * MiB, 95 * GiB));

        var issue = Assert.Single(issues, i => i.IsError);
        Assert.Contains("does not fit", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RawDisk_MustBeInitializedFirst()
    {
        var disk = GptDisk(100 * GiB) with { Style = PartitionStyle.Raw };

        var issues = PartitionPlanner.Validate(disk, Request(1 * MiB, 10 * GiB));

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("Initialize", StringComparison.Ordinal));
    }

    [Fact]
    public void MbrDiskWithFourPrimaries_IsFull()
    {
        var disk = GptDisk(100 * GiB,
            Basic(1, 1 * MiB, 1 * GiB), Basic(2, 2 * GiB, 1 * GiB), Basic(3, 4 * GiB, 1 * GiB), Basic(4, 6 * GiB, 1 * GiB))
            with { Style = PartitionStyle.Mbr };

        var issues = PartitionPlanner.Validate(disk, Request(10 * GiB, 1 * GiB));

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("four primary", StringComparison.Ordinal));
    }

    [Fact]
    public void Fat32Above32Gb_IsRejected()
    {
        var disk = GptDisk(100 * GiB);

        var issues = PartitionPlanner.Validate(disk, Request(1 * MiB, 40 * GiB, fs: "FAT32"));

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("FAT32", StringComparison.Ordinal));
    }

    [Fact]
    public void LetterInUse_IsRejected_AndNoLetterOnlyWarns()
    {
        var disk = GptDisk(100 * GiB);

        var taken = PartitionPlanner.Validate(disk, Request(1 * MiB, 10 * GiB, letter: 'D'), new HashSet<char> { 'D' });
        var none = PartitionPlanner.Validate(disk, Request(1 * MiB, 10 * GiB, letter: null));

        Assert.Contains(taken, i => i.IsError && i.Message.Contains("D:", StringComparison.Ordinal));
        Assert.DoesNotContain(none, i => i.IsError);
        Assert.Contains(none, i => !i.IsError && i.Message.Contains("drive letter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Normalize_AlignsStartAndKeepsEnd()
    {
        var request = Request(1 * MiB + 100 * 1024, 10 * GiB);

        var normalized = PartitionPlanner.Normalize(request);

        Assert.Equal(2 * MiB, normalized.OffsetBytes);
        Assert.Equal(request.EndBytes, normalized.EndBytes);
    }

    [Fact]
    public void MaximumSizeAt_ReturnsRestOfRegion()
    {
        var disk = GptDisk(100 * GiB, Basic(1, 1 * MiB, 10 * GiB));

        var max = PartitionPlanner.MaximumSizeAt(disk, 10 * GiB + 1 * MiB);

        Assert.Equal(100 * GiB - 1 * MiB - (10 * GiB + 1 * MiB), max);
        Assert.Equal(0UL, PartitionPlanner.MaximumSizeAt(disk, 5 * GiB));
    }

    [Fact]
    public void LettersInUse_CollectsFromPartitionsAndVolumes()
    {
        var disk = GptDisk(100 * GiB,
            Basic(1, 1 * MiB, 1 * GiB, 'C'),
            Basic(2, 2 * GiB, 1 * GiB) with { Volume = new Volume { Path = "x", DriveLetter = 'd' } });

        var letters = PartitionPlanner.LettersInUse([disk]);

        Assert.Equal(new HashSet<char> { 'C', 'D' }, letters);
    }
}
