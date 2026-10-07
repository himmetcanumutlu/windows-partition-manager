using System.Diagnostics;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Platform.Windows;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>
/// End-to-end partition operations against a throwaway VHDX: the same code paths the app uses on
/// real disks, exercised where a bug costs nothing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VhdxPartitionTests(ITestOutputHelper output)
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [AdminFact]
    public async Task InitializeCreateFormatAndDelete_OnFreshVhdx()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wpm-test-{Guid.NewGuid():N}.vhdx");
        var provider = new WmiStorageProvider();
        var operations = new WmiStorageOperations();

        using var vhd = VirtualDisk.Create(path, 2 * GiB);
        try
        {
            vhd.Attach();
            var diskNumber = vhd.GetDiskNumber();
            output.WriteLine($"VHDX attached as disk {diskNumber}");

            var disk = await WaitForDiskAsync(provider, diskNumber, _ => true);
            Assert.Equal(PartitionStyle.Raw, disk.Style);
            var raw = Assert.Single(FreeSpaceCalculator.Compute(disk));
            Assert.Equal(disk.SizeBytes, raw.SizeBytes);

            await operations.InitializeDiskAsync(diskNumber, PartitionStyle.Gpt);
            disk = await WaitForDiskAsync(provider, diskNumber, d => d.Style == PartitionStyle.Gpt);
            output.WriteLine($"Initialized GPT; {disk.Partitions.Count} partition(s) created by Windows (MSR expected)");

            // First partition: half a gigabyte of NTFS with an automatic drive letter.
            var region = FreeSpaceCalculator.Compute(disk).MaxBy(r => r.SizeBytes)!;
            var first = PartitionPlanner.Normalize(new CreatePartitionRequest
            {
                DiskNumber = diskNumber,
                OffsetBytes = region.OffsetBytes,
                SizeBytes = 512 * MiB,
                FileSystem = "NTFS",
                Label = "WpmTest",
            });
            var issues = PartitionPlanner.Validate(disk, first, PartitionPlanner.LettersInUse(await provider.GetDisksAsync()));
            Assert.DoesNotContain(issues, i => i.IsError);

            var created = await operations.CreatePartitionAsync(first, new Progress<string>(output.WriteLine));
            output.WriteLine($"Created partition {created.Number} {created.DriveLetter}: {created.Volume?.FileSystem} \"{created.Volume?.Label}\"");

            Assert.Equal(first.OffsetBytes, created.OffsetBytes);
            Assert.Equal(first.SizeBytes, created.SizeBytes);
            Assert.NotNull(created.Volume);
            Assert.Equal("NTFS", created.Volume!.FileSystem);
            Assert.Equal("WpmTest", created.Volume.Label);
            Assert.NotNull(created.DriveLetter);

            // The volume must actually be usable.
            var probe = Path.Combine($"{created.DriveLetter}:\\", "wpm-probe.txt");
            await File.WriteAllTextAsync(probe, "hello from Windows Partition Manager");
            Assert.Equal("hello from Windows Partition Manager", await File.ReadAllTextAsync(probe));

            // Second partition: the rest of the disk as exFAT with an explicit letter.
            disk = await WaitForDiskAsync(provider, diskNumber, d => d.Partitions.Any(p => p.Number == created.Number));
            var rest = FreeSpaceCalculator.Compute(disk).MaxBy(r => r.SizeBytes)!;

            // Our free-space math must agree with Windows' own largest free extent (never exceed it).
            output.WriteLine($"Largest free region: ours {rest.SizeBytes / MiB} MiB, Windows {disk.LargestFreeExtentBytes / MiB} MiB");
            Assert.True(rest.SizeBytes <= disk.LargestFreeExtentBytes, "our free region is larger than what Windows reports");
            Assert.True(disk.LargestFreeExtentBytes - rest.SizeBytes <= 2 * MiB, "our free region is much smaller than what Windows reports");
            var letters = PartitionPlanner.LettersInUse(await provider.GetDisksAsync());
            var freeLetter = "ZYXWVUTSRQPONMLKJIHGFE".First(l => !letters.Contains(l));
            var second = new CreatePartitionRequest
            {
                DiskNumber = diskNumber,
                OffsetBytes = rest.OffsetBytes,
                SizeBytes = rest.SizeBytes,
                FileSystem = "exFAT",
                Label = "WpmTwo",
                DriveLetter = freeLetter,
            };
            Assert.DoesNotContain(PartitionPlanner.Validate(disk, second, letters), i => i.IsError);

            var createdSecond = await operations.CreatePartitionAsync(second, new Progress<string>(output.WriteLine));
            output.WriteLine($"Created partition {createdSecond.Number} {createdSecond.DriveLetter}: {createdSecond.Volume?.FileSystem}");
            Assert.Equal(freeLetter, createdSecond.DriveLetter);
            Assert.Equal("exFAT", createdSecond.Volume?.FileSystem);

            disk = await WaitForDiskAsync(provider, diskNumber, d => d.Partitions.Any(p => p.Number == createdSecond.Number));
            Assert.Empty(FreeSpaceCalculator.Compute(disk));

            // Delete the first one and make sure its space is free again.
            await operations.DeletePartitionAsync(diskNumber, created.Number);
            disk = await WaitForDiskAsync(provider, diskNumber, d => d.Partitions.All(p => p.Number != created.Number));
            var freed = Assert.Single(FreeSpaceCalculator.Compute(disk));
            Assert.Equal(first.OffsetBytes, freed.OffsetBytes);
            Assert.True(freed.SizeBytes >= first.SizeBytes - 1 * MiB);
            output.WriteLine($"Deleted partition {created.Number}; {freed.SizeBytes / MiB} MiB free again");
        }
        finally
        {
            try
            {
                vhd.Detach();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                output.WriteLine($"Detach failed: {ex.Message}");
            }

            vhd.Dispose();
            File.Delete(path);
        }
    }

    [AdminFact]
    public void Planner_RejectsRequestOutsideFreeSpace_OnRealLayout()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wpm-test-{Guid.NewGuid():N}.vhdx");
        using var vhd = VirtualDisk.Create(path, 1 * GiB);
        try
        {
            vhd.Attach();
            var diskNumber = vhd.GetDiskNumber();
            var disk = WaitForDiskAsync(new WmiStorageProvider(), diskNumber, _ => true).GetAwaiter().GetResult();

            // RAW disk: the planner must demand initialization rather than let Windows fail later.
            var issues = PartitionPlanner.Validate(disk, new CreatePartitionRequest { DiskNumber = diskNumber, OffsetBytes = 1 * MiB, SizeBytes = 100 * MiB });
            Assert.Contains(issues, i => i.IsError && i.Message.Contains("Initialize", StringComparison.Ordinal));
        }
        finally
        {
            vhd.Detach();
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
