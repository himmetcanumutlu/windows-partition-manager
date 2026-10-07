using System.Runtime.Versioning;
using System.Text;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Analysis;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Integration.Tests.Support;
using WindowsPartitionManager.Platform.Windows;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>
/// Dress rehearsals for running the program on a real disk. The VHDX copies the layout of the
/// developer's own SSD (system NTFS, Recovery, data NTFS, EFI at the end, free tail), fills the
/// volumes with thousands of hashed files placed so that a shrink must move them, plants random
/// byte markers in every place the program must not touch, and checks all of it after each step.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RehearsalTests
{
    private const ulong MiB = TestVhd.MiB;
    private const ulong GiB = TestVhd.GiB;

    private readonly ITestOutputHelper _output;

    public RehearsalTests(ITestOutputHelper output)
    {
        _output = output;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [AdminFact]
    public async Task ReplicaOfTheRealDisk_ShrinkCreateDeleteExtend_KeepsEveryByte()
    {
        using var vhd = TestVhd.Create(6 * GiB, _output, "rehearsal");
        var ops = vhd.Operations;
        var diskNumber = vhd.DiskNumber;

        await vhd.WaitAsync(_ => true);
        await ops.InitializeDiskAsync(diskNumber, PartitionStyle.Gpt);
        var disk = await vhd.WaitAsync(d => d.Style == PartitionStyle.Gpt);
        var start = FreeSpaceCalculator.Compute(disk)[0].OffsetBytes;

        // Same order as the real SSD: [C: system NTFS][Recovery][D: data NTFS][EFI][free tail].
        var c = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = diskNumber, OffsetBytes = start, SizeBytes = 3 * GiB, FileSystem = "NTFS", Label = "C-REPLICA" });
        var recoveryOffset = c.EndBytes;
        await vhd.CreateRawPartitionAsync(recoveryOffset, 512 * MiB, TestVhd.GptRecovery);
        var d = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = diskNumber, OffsetBytes = recoveryOffset + (512 * MiB), SizeBytes = 1 * GiB, FileSystem = "NTFS", Label = "D-REPLICA" });
        var efiOffset = d.EndBytes;
        await vhd.CreateRawPartitionAsync(efiOffset, 100 * MiB, TestVhd.GptEfiSystem);

        disk = await vhd.WaitAsync(x => x.Partitions.Count >= 5);
        _output.WriteLine("Layout: " + TestVhd.LayoutOf(disk));
        var cLetter = c.DriveLetter!.Value;
        var dLetter = d.DriveLetter!.Value;
        var msr = disk.Partitions.Single(p => p.Kind == PartitionKind.MicrosoftReserved);
        var tail = FreeSpaceCalculator.Compute(disk).Single(r => r.OffsetBytes >= efiOffset);

        // Random markers in every place no operation on C: may touch.
        var markers = new List<(string Name, ulong Offset, byte[] Data)>
        {
            ("MSR", msr.OffsetBytes + (1 * MiB) - (msr.OffsetBytes % 512), TestData.Marker(1)),
            ("Recovery start", recoveryOffset, TestData.Marker(2)),
            ("Recovery end", recoveryOffset + (511 * MiB), TestData.Marker(3)),
            ("EFI start", efiOffset, TestData.Marker(4)),
            ("EFI end", efiOffset + (99 * MiB), TestData.Marker(5)),
            ("Free tail start", tail.OffsetBytes, TestData.Marker(6)),
            ("Free tail end", tail.EndBytes - (1 * MiB), TestData.Marker(7)),
        };
        foreach (var marker in markers)
        {
            await vhd.WriteRawAsync(marker.Offset, marker.Data);
        }

        var dFiles = TestData.WriteRandomFiles($@"{dLetter}:\", "docs", 120 * (long)MiB, seed: 11);
        var cFiles = TestData.WriteFilesAtEndOfVolume(cLetter, 700 * (long)MiB, seed: 42);
        _output.WriteLine($"C-replica: {cFiles.Count} files, D-replica: {dFiles.Count} files");

        disk = await vhd.WaitAsync(_ => true);
        var others = OtherPartitions(disk, c.Number);

        async Task VerifyEverythingAsync(string stage, int? extraPartition = null)
        {
            var problems = new List<string>();
            var now = await vhd.WaitAsync(_ => true);
            var nowOthers = OtherPartitions(now, c.Number, extraPartition);
            if (nowOthers != others)
            {
                problems.Add($"other partitions changed: before [{others}] after [{nowOthers}]");
            }

            foreach (var (name, offset, data) in markers)
            {
                var current = await vhd.ReadRawAsync(offset, data.Length);
                if (!current.AsSpan().SequenceEqual(data))
                {
                    problems.Add($"marker '{name}' at {offset} was modified");
                }
            }

            problems.AddRange(TestData.Verify(cLetter, cFiles).Select(f => "C: " + f));
            problems.AddRange(TestData.Verify(dLetter, dFiles).Select(f => "D: " + f));

            if (vhd.Chkdsk(cLetter) != 0)
            {
                problems.Add("chkdsk reported problems on C-replica");
            }

            if (vhd.Chkdsk(dLetter) != 0)
            {
                problems.Add("chkdsk reported problems on D-replica");
            }

            _output.WriteLine($"[{stage}] {(problems.Count == 0 ? "everything intact" : string.Join("; ", problems.Take(10)))}");
            Assert.True(problems.Count == 0, $"{stage}: {string.Join("; ", problems.Take(10))}");
        }

        await VerifyEverythingAsync("before");

        // 1. The analysis must see that the data sits at the end and a shrink has to move it.
        using (var volume = await new NtfsVolumeInspector().OpenAsync(cLetter))
        {
            var report = await ShrinkAnalyzer.AnalyzeAsync(volume, targetSizeBytes: 1 * GiB);
            _output.WriteLine($"Analysis: used {report.UsedBytes / MiB} MiB, last used byte at {report.MinimumSizeWithoutMovingBytes / MiB} MiB, " +
                              $"limit with blockers {report.MinimumSizeWithBlockersBytes / MiB} MiB, movable past target {report.MovableBytesBeyondTarget / MiB} MiB in {report.MovableFilesBeyondTarget} files");
            Assert.True(report.MinimumSizeWithoutMovingBytes > 2 * GiB, "test data should sit near the end of the volume");
            Assert.True(report.MovableFilesBeyondTarget > 100, "the shrink should have to move many files");
        }

        // 2. Shrink as far as Windows allows (plus a little headroom).
        var partition = (await vhd.WaitAsync(_ => true)).Partitions.Single(p => p.Number == c.Number);
        var limits = await vhd.Provider.GetSupportedSizeAsync(diskNumber, c.Number);
        Assert.NotNull(limits);
        var target = Math.Max(1 * GiB, limits!.MinimumBytes + (64 * MiB));
        var shrink = ResizePlanner.Normalize(new ResizePartitionRequest { DiskNumber = diskNumber, PartitionNumber = c.Number, NewSizeBytes = target });
        disk = await vhd.WaitAsync(_ => true);
        Assert.DoesNotContain(ResizePlanner.Validate(disk, partition, shrink, limits), i => i.IsError);
        _output.WriteLine($"Shrinking C-replica {partition.SizeBytes / MiB} -> {shrink.NewSizeBytes / MiB} MiB (Windows minimum {limits.MinimumBytes / MiB} MiB)");
        var shrunk = await ops.ResizePartitionAsync(shrink);
        Assert.Equal(shrink.NewSizeBytes, shrunk.SizeBytes);
        await VerifyEverythingAsync("after shrink");

        // 3. A new partition in the freed space, used, verified.
        disk = await vhd.WaitAsync(x => x.Partitions.Single(p => p.Number == c.Number).SizeBytes == shrunk.SizeBytes);
        var freed = FreeSpaceCalculator.Compute(disk).Single(r => r.OffsetBytes >= shrunk.OffsetBytes && r.EndBytes <= recoveryOffset);
        var created = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = diskNumber, OffsetBytes = freed.OffsetBytes, SizeBytes = freed.SizeBytes, FileSystem = "NTFS", Label = "NEW" });
        _output.WriteLine($"New partition {created.DriveLetter}: {created.SizeBytes / MiB} MiB at {created.OffsetBytes / MiB} MiB");
        var newFiles = TestData.WriteRandomFiles($@"{created.DriveLetter}:\", "new", 200 * (long)MiB, seed: 99);
        Assert.Empty(TestData.Verify(created.DriveLetter!.Value, newFiles));
        Assert.Equal(0, vhd.Chkdsk(created.DriveLetter!.Value));
        await VerifyEverythingAsync("after create", created.Number);

        // 4. Delete it again.
        await ops.DeletePartitionAsync(diskNumber, created.Number);
        disk = await vhd.WaitAsync(x => x.Partitions.All(p => p.Number != created.Number));
        await VerifyEverythingAsync("after delete");

        // 5. Extend C-replica back to its original size.
        partition = disk.Partitions.Single(p => p.Number == c.Number);
        var extend = ResizePlanner.Normalize(new ResizePartitionRequest { DiskNumber = diskNumber, PartitionNumber = c.Number, NewSizeBytes = ResizePlanner.MaximumSize(disk, partition) });
        var extended = await ops.ResizePartitionAsync(extend);
        _output.WriteLine($"Extended C-replica back to {extended.SizeBytes / MiB} MiB (originally {c.SizeBytes / MiB} MiB)");
        Assert.InRange(extended.SizeBytes, c.SizeBytes - (1 * MiB), c.SizeBytes);
        await VerifyEverythingAsync("after extend");
    }

    [SurpriseRemovalFact]
    public async Task ShrinkInterruptedBySurpriseRemoval_LeavesFilesAndFileSystemIntact()
    {
        using var vhd = TestVhd.Create(4 * GiB, _output, "interrupt");
        var ops = vhd.Operations;
        var diskNumber = vhd.DiskNumber;

        await vhd.WaitAsync(_ => true);
        await ops.InitializeDiskAsync(diskNumber, PartitionStyle.Gpt);
        var disk = await vhd.WaitAsync(x => x.Style == PartitionStyle.Gpt);
        var region = FreeSpaceCalculator.Compute(disk).MaxBy(r => r.SizeBytes)!;
        var created = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = diskNumber, OffsetBytes = region.OffsetBytes, SizeBytes = region.SizeBytes, FileSystem = "NTFS", Label = "CUT" });
        var letter = created.DriveLetter!.Value;

        var files = TestData.WriteFilesAtEndOfVolume(letter, 1400 * (long)MiB, seed: 7);
        _output.WriteLine($"{files.Count} files written near the end of {letter}:");

        var limits = await vhd.Provider.GetSupportedSizeAsync(diskNumber, created.Number);
        var target = Math.Max(1600 * MiB, limits!.MinimumBytes + (64 * MiB));
        var request = ResizePlanner.Normalize(new ResizePartitionRequest { DiskNumber = diskNumber, PartitionNumber = created.Number, NewSizeBytes = target });
        _output.WriteLine($"Shrinking {created.SizeBytes / MiB} -> {request.NewSizeBytes / MiB} MiB, then pulling the disk out mid-way");

        var resize = ops.ResizePartitionAsync(request);
        await Task.Delay(1200);
        var finishedBeforeCut = resize.IsCompleted;
        vhd.DetachAbruptly();
        try
        {
            var result = await resize;
            _output.WriteLine($"Resize returned {result.SizeBytes / MiB} MiB");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _output.WriteLine($"Resize failed as expected after the cut: {ex.GetType().Name}: {ex.Message}");
        }

        await Task.Delay(2000);
        vhd.Reattach();
        disk = await vhd.WaitAsync(x => x.Partitions.Any(p => p.Kind == PartitionKind.Basic && p.Volume is not null), 120);
        var after = disk.Partitions.Single(p => p.Kind == PartitionKind.Basic);
        await vhd.EnsureDriveLetterAsync(after.Number);
        disk = await vhd.WaitAsync(x => x.Partitions.Single(p => p.Number == after.Number).DriveLetter is not null, 60);
        after = disk.Partitions.Single(p => p.Number == after.Number);
        letter = after.DriveLetter!.Value;
        _output.WriteLine($"After reattach: partition {after.SizeBytes / MiB} MiB, file system {after.Volume?.SizeBytes / MiB} MiB, dirty={after.Volume?.IsDirty}, health={after.Volume?.Health}");

        var bad = TestData.Verify(letter, files);
        _output.WriteLine(bad.Count == 0 ? $"All {files.Count} files intact" : $"{bad.Count} damaged: {string.Join("; ", bad.Take(5))}");
        var chkdsk = vhd.Chkdsk(letter);

        Assert.False(finishedBeforeCut, "the shrink finished before the cut; the interruption was not exercised (use more data)");
        Assert.Empty(bad);
        Assert.Equal(0, chkdsk);
        Assert.True(after.Volume!.SizeBytes <= after.SizeBytes, "file system must never be larger than its partition");
    }

    /// <summary>Offset, size and type of every partition except the one being worked on (and an optional new one).</summary>
    private static string OtherPartitions(Disk disk, int workedOn, int? alsoIgnore = null)
        => string.Join("; ", disk.Partitions
            .Where(p => p.Number != workedOn && p.Number != alsoIgnore)
            .OrderBy(p => p.OffsetBytes)
            .Select(p => $"{p.TypeDescription} @{p.OffsetBytes} +{p.SizeBytes}"));
}
