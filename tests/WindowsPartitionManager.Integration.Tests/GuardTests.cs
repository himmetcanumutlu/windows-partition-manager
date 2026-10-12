using System.Runtime.Versioning;
using System.Text;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Integration.Tests.Support;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>
/// The operations layer must refuse bad or stale requests on its own, before Windows is asked to
/// do anything, and leave the partition table byte-for-byte unchanged when it does.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GuardTests
{
    private const ulong MiB = TestVhd.MiB;
    private const ulong GiB = TestVhd.GiB;

    private readonly ITestOutputHelper _output;

    public GuardTests(ITestOutputHelper output)
    {
        _output = output;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [AdminFact]
    public async Task BadAndStaleRequests_AreRefused_AndThePartitionTableStaysByteIdentical()
    {
        using var vhd = TestVhd.Create(4 * GiB, _output, "guard");
        var ops = vhd.Operations;
        var n = vhd.DiskNumber;

        await vhd.WaitAsync(_ => true);
        await ops.InitializeDiskAsync(n, PartitionStyle.Gpt);
        var disk = await vhd.WaitAsync(d => d.Style == PartitionStyle.Gpt);
        var start = FreeSpaceCalculator.Compute(disk)[0].OffsetBytes;

        var a = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = n, OffsetBytes = start, SizeBytes = 1 * GiB, FileSystem = "NTFS", Label = "A" });
        var b = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = n, OffsetBytes = a.EndBytes, SizeBytes = 1 * GiB, FileSystem = "NTFS", Label = "B" });
        var c = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = n, OffsetBytes = b.EndBytes, SizeBytes = 1 * GiB, FileSystem = "NTFS", Label = "C" });
        var files = TestData.WriteRandomFiles($@"{a.DriveLetter}:\", "keep", 100 * (long)MiB, seed: 5);
        disk = await vhd.WaitAsync(x => x.Partitions.Count >= 4);
        var fingerprintDisk = DiskFingerprint.Of(disk);
        var fingerprintA = PartitionFingerprint.Of(disk.Partitions.Single(p => p.Number == a.Number));
        var fingerprintB = PartitionFingerprint.Of(disk.Partitions.Single(p => p.Number == b.Number));
        var fingerprintC = PartitionFingerprint.Of(disk.Partitions.Single(p => p.Number == c.Number));
        _output.WriteLine("Before: " + TestVhd.LayoutOf(disk));

        // The primary GPT (protective MBR + header + entries) and the backup at the end of the disk.
        var backupOffset = disk.SizeBytes - (64 * 1024);
        async Task<byte[]> PartitionTableAsync() => [.. await vhd.ReadRawAsync(0, 64 * 1024), .. await vhd.ReadRawAsync(backupOffset, 64 * 1024)];
        var tableBefore = await PartitionTableAsync();

        async Task ExpectRefusedAsync(string what, Func<Task> action, string expectedText)
        {
            var ex = await Assert.ThrowsAsync<StorageOperationException>(action);
            _output.WriteLine($"{what}: refused -> {ex.Message}");
            Assert.Contains("Refused before touching the disk", ex.Message, StringComparison.Ordinal);
            Assert.Contains(expectedText, ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True((await PartitionTableAsync()).AsSpan().SequenceEqual(tableBefore), $"{what}: the partition table changed");
        }

        // Requests that skip the planner entirely.
        await ExpectRefusedAsync("create overlapping A", () => ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = n, OffsetBytes = a.OffsetBytes + (100 * MiB), SizeBytes = 200 * MiB }), "unallocated");
        await ExpectRefusedAsync("create larger than the free space", () => ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = n, OffsetBytes = c.EndBytes, SizeBytes = 3 * GiB }), "free");
        await ExpectRefusedAsync("shrink below the data", () => ops.ResizePartitionAsync(new ResizePartitionRequest { DiskNumber = n, PartitionNumber = a.Number, NewSizeBytes = 50 * MiB }), "cannot shrink below");
        await ExpectRefusedAsync("extend into B", () => ops.ResizePartitionAsync(new ResizePartitionRequest { DiskNumber = n, PartitionNumber = a.Number, NewSizeBytes = 2 * GiB }), "no unallocated space directly after");
        await ExpectRefusedAsync("delete a partition that does not exist", () => ops.DeletePartitionAsync(n, 99), "not found");
        await ExpectRefusedAsync("format MSR", () => ops.FormatPartitionAsync(new FormatPartitionRequest { DiskNumber = n, PartitionNumber = disk.Partitions.Single(p => p.Kind == PartitionKind.MicrosoftReserved).Number }), "basic data");

        // A screen that went stale: the user saw another disk / another partition layout.
        var otherDisk = fingerprintDisk with { FriendlyName = "Some USB stick", SizeBytes = 64 * GiB };
        await ExpectRefusedAsync("delete on a replaced disk", () => ops.DeletePartitionAsync(new DeletePartitionRequest { DiskNumber = n, PartitionNumber = b.Number, ExpectedDisk = otherDisk, ExpectedPartition = fingerprintB }), "Refresh");
        await ExpectRefusedAsync("format with a stale size", () => ops.FormatPartitionAsync(new FormatPartitionRequest { DiskNumber = n, PartitionNumber = b.Number, ExpectedDisk = fingerprintDisk, ExpectedPartition = fingerprintB with { SizeBytes = 2 * GiB } }), "Refresh");

        // Now delete B for real, then try to act on what the screen still shows.
        await ops.DeletePartitionAsync(new DeletePartitionRequest { DiskNumber = n, PartitionNumber = b.Number, ExpectedDisk = fingerprintDisk, ExpectedPartition = fingerprintB });
        disk = await vhd.WaitAsync(x => x.Partitions.All(p => p.OffsetBytes != fingerprintB.OffsetBytes));
        _output.WriteLine("After deleting B: " + TestVhd.LayoutOf(disk));
        var cNow = disk.Partitions.Single(p => p.OffsetBytes == fingerprintC.OffsetBytes);
        _output.WriteLine(cNow.Number == fingerprintC.Number
            ? $"Windows kept partition numbers stable: C is still #{cNow.Number}"
            : $"Windows RENUMBERED partitions: C moved from #{fingerprintC.Number} to #{cNow.Number}");
        tableBefore = await PartitionTableAsync();

        await ExpectRefusedAsync("delete B again from a stale screen", () => ops.DeletePartitionAsync(new DeletePartitionRequest { DiskNumber = n, PartitionNumber = fingerprintB.Number, ExpectedDisk = fingerprintDisk, ExpectedPartition = fingerprintB }), "Refresh");
        await ExpectRefusedAsync("format B again from a stale screen", () => ops.FormatPartitionAsync(new FormatPartitionRequest { DiskNumber = n, PartitionNumber = fingerprintB.Number, ExpectedDisk = fingerprintDisk, ExpectedPartition = fingerprintB }), "Refresh");

        // The stale row for A is still valid: same disk, same partition, so it is accepted.
        var shrinkA = ResizePlanner.Normalize(new ResizePartitionRequest { DiskNumber = n, PartitionNumber = a.Number, NewSizeBytes = 512 * MiB, ExpectedDisk = fingerprintDisk, ExpectedPartition = fingerprintA });
        var shrunk = await ops.ResizePartitionAsync(shrinkA);
        Assert.Equal(512 * MiB, shrunk.SizeBytes);
        Assert.Empty(TestData.Verify(a.DriveLetter!.Value, files));
        Assert.Equal(0, vhd.Chkdsk(a.DriveLetter!.Value));
    }
}
