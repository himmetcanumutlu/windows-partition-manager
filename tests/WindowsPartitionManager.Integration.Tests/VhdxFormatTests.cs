using System.Diagnostics;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Platform.Windows;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>
/// The flash-drive path: an exFAT stick cannot be shrunk, so it is formatted to NTFS in place
/// (keeping its letter), then shrunk, then a second partition is created in the freed space.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VhdxFormatTests(ITestOutputHelper output)
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [AdminFact]
    public async Task ExfatStick_FormatToNtfs_ThenShrinkAndSplit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wpm-format-{Guid.NewGuid():N}.vhdx");
        var provider = new WmiStorageProvider();
        var operations = new WmiStorageOperations();

        using var vhd = VirtualDisk.Create(path, 2 * GiB);
        try
        {
            vhd.Attach();
            var diskNumber = vhd.GetDiskNumber();
            await WaitForDiskAsync(provider, diskNumber, _ => true);

            // Flash drives ship as MBR with one FAT/exFAT partition filling the disk.
            await operations.InitializeDiskAsync(diskNumber, PartitionStyle.Mbr);
            var disk = await WaitForDiskAsync(provider, diskNumber, d => d.Style == PartitionStyle.Mbr);
            var region = FreeSpaceCalculator.Compute(disk).MaxBy(r => r.SizeBytes)!;
            output.WriteLine($"MBR free region: ours {region.SizeBytes:N0} B, Windows largest free extent {disk.LargestFreeExtentBytes:N0} B");
            Assert.True(region.SizeBytes <= disk.LargestFreeExtentBytes, "our MBR free region exceeds what Windows reports");
            Assert.True(disk.LargestFreeExtentBytes - region.SizeBytes < 1 * MiB, "our MBR free region is more than 1 MiB smaller than Windows'");
            var stick = await operations.CreatePartitionAsync(new CreatePartitionRequest
            {
                DiskNumber = diskNumber,
                OffsetBytes = region.OffsetBytes,
                SizeBytes = region.SizeBytes,
                FileSystem = "exFAT",
                Label = "STICK",
            });
            output.WriteLine($"exFAT stick: {stick.DriveLetter}: {stick.SizeBytes / MiB} MiB");
            Assert.Equal("exFAT", stick.Volume?.FileSystem);

            disk = await WaitForDiskAsync(provider, diskNumber, d => d.Partitions.Any(p => p.Number == stick.Number));
            var partition = disk.Partitions.Single(p => p.Number == stick.Number);

            // exFAT cannot be resized: the planner must say so instead of letting Windows fail later.
            Assert.False(ResizePlanner.CanResize(partition));
            var refused = ResizePlanner.Validate(disk, partition, new ResizePartitionRequest { DiskNumber = diskNumber, PartitionNumber = partition.Number, NewSizeBytes = 1 * GiB });
            Assert.Contains(refused, i => i.IsError && i.Message.Contains("NTFS and ReFS", StringComparison.Ordinal));

            // Format in place to NTFS; the letter must survive.
            var format = new FormatPartitionRequest { DiskNumber = diskNumber, PartitionNumber = partition.Number, FileSystem = "NTFS", Label = "STICK-NTFS" };
            var issues = FormatPlanner.Validate(disk, partition, format);
            Assert.DoesNotContain(issues, i => i.IsError);
            Assert.Contains(issues, i => !i.IsError && i.Message.Contains("lost", StringComparison.Ordinal));

            var formatted = await operations.FormatPartitionAsync(format, new Progress<string>(output.WriteLine));
            output.WriteLine($"Formatted: {formatted.DriveLetter}: {formatted.Volume?.FileSystem} \"{formatted.Volume?.Label}\"");
            Assert.Equal(stick.DriveLetter, formatted.DriveLetter);
            Assert.Equal("NTFS", formatted.Volume?.FileSystem);
            Assert.Equal("STICK-NTFS", formatted.Volume?.Label);
            await File.WriteAllTextAsync($@"{formatted.DriveLetter}:\keep.txt", "after format");

            // Now the stick can be shrunk and split like any NTFS volume.
            disk = await WaitForDiskAsync(provider, diskNumber, d => d.Partitions.Single(p => p.Number == stick.Number).Volume?.FileSystem == "NTFS");
            partition = disk.Partitions.Single(p => p.Number == stick.Number);
            Assert.True(ResizePlanner.CanResize(partition));

            var shrunk = await operations.ResizePartitionAsync(
                ResizePlanner.Normalize(new ResizePartitionRequest { DiskNumber = diskNumber, PartitionNumber = partition.Number, NewSizeBytes = 1 * GiB }),
                new Progress<string>(output.WriteLine));
            Assert.InRange(shrunk.SizeBytes, 1 * GiB - 1 * MiB, 1 * GiB + 1 * MiB);
            Assert.Equal("after format", await File.ReadAllTextAsync($@"{shrunk.DriveLetter}:\keep.txt"));

            disk = await WaitForDiskAsync(provider, diskNumber, d => d.Partitions.Single(p => p.Number == stick.Number).SizeBytes == shrunk.SizeBytes);
            var freed = FreeSpaceCalculator.Compute(disk).MaxBy(r => r.SizeBytes)!;
            var second = await operations.CreatePartitionAsync(new CreatePartitionRequest
            {
                DiskNumber = diskNumber,
                OffsetBytes = freed.OffsetBytes,
                SizeBytes = freed.SizeBytes,
                FileSystem = "exFAT",
                Label = "SECOND",
            });
            output.WriteLine($"Second partition on the MBR stick: {second.DriveLetter}: {second.Volume?.FileSystem} {second.SizeBytes / MiB} MiB");
            Assert.Equal("exFAT", second.Volume?.FileSystem);
            Assert.NotNull(second.DriveLetter);
        }
        finally
        {
            try
            {
                vhd.Detach();
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                output.WriteLine($"Detach failed: {e.Message}");
            }

            vhd.Dispose();
            File.Delete(path);
        }
    }

    private static async Task<Disk> WaitForDiskAsync(WmiStorageProvider provider, int diskNumber, Func<Disk, bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var disk = (await provider.GetDisksAsync()).FirstOrDefault(d => d.Number == diskNumber);

            // A test must never act on a real drive, whatever bug made it look at one.
            if (disk is not null && !(disk.BusType.Contains("virtual", StringComparison.OrdinalIgnoreCase) && disk.FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !disk.IsSystem && !disk.IsBoot))
            {
                throw new InvalidOperationException($"Refusing to continue: disk {diskNumber} ({disk.FriendlyName}, {disk.BusType}) is not a throwaway virtual disk.");
            }

            if (disk is not null && condition(disk))
            {
                return disk;
            }

            if (stopwatch.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException($"Disk {diskNumber} did not reach the expected state within 30 s.");
            }

            await Task.Delay(500);
        }
    }
}
