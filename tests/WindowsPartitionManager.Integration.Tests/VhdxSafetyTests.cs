using System.Diagnostics;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Platform.Windows;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>The safeguards must actually fire on a real volume, not just in unit tests.</summary>
[SupportedOSPlatform("windows")]
public sealed class VhdxSafetyTests(ITestOutputHelper output)
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [AdminFact]
    public async Task DirtyVolume_IsRefusedByPlannerAndByOperations_AndEverythingIsLogged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wpm-safety-{Guid.NewGuid():N}.vhdx");
        var provider = new WmiStorageProvider();
        var operations = new WmiStorageOperations();
        var logSizeBefore = File.Exists(OperationLog.Path) ? new FileInfo(OperationLog.Path).Length : 0;

        using var vhd = VirtualDisk.Create(path, 1 * GiB);
        try
        {
            vhd.Attach();
            var diskNumber = vhd.GetDiskNumber();
            await WaitForDiskAsync(provider, diskNumber, _ => true);
            await operations.InitializeDiskAsync(diskNumber, PartitionStyle.Gpt);
            var disk = await WaitForDiskAsync(provider, diskNumber, d => d.Style == PartitionStyle.Gpt);

            var region = FreeSpaceCalculator.Compute(disk).MaxBy(r => r.SizeBytes)!;
            var created = await operations.CreatePartitionAsync(new CreatePartitionRequest
            {
                DiskNumber = diskNumber,
                OffsetBytes = region.OffsetBytes,
                SizeBytes = region.SizeBytes,
                FileSystem = "NTFS",
                Label = "Dirty",
            });
            Assert.NotNull(created.DriveLetter);
            Assert.False(created.Volume!.IsDirty);

            // Mark the volume dirty the way an unclean shutdown would (fsutil is part of Windows).
            var fsutil = Process.Start(new ProcessStartInfo("fsutil.exe", $"dirty set {created.DriveLetter}:") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            output.WriteLine((await fsutil.StandardOutput.ReadToEndAsync()).Trim());
            await fsutil.WaitForExitAsync();
            Assert.Equal(0, fsutil.ExitCode);

            var live = VolumeDirtyBit.Query(created.DriveLetter!.Value, out var error);
            output.WriteLine($"Live dirty query: {live?.ToString() ?? "null"} (win32 error {error})");
            Assert.True(live, "FSCTL_IS_VOLUME_DIRTY should report the bit fsutil just set");

            disk = await WaitForDiskAsync(provider, diskNumber, d => d.Partitions.Any(p => p.Number == created.Number && p.Volume?.IsDirty == true));
            var partition = disk.Partitions.Single(p => p.Number == created.Number);
            output.WriteLine($"Volume {partition.DriveLetter}: dirty={partition.Volume!.IsDirty} health={partition.Volume.Health}");

            // 1. The planner refuses.
            var request = new ResizePartitionRequest { DiskNumber = diskNumber, PartitionNumber = partition.Number, NewSizeBytes = 512 * MiB };
            var issues = ResizePlanner.Validate(disk, partition, request);
            Assert.Contains(issues, i => i.IsError && i.Message.Contains("dirty", StringComparison.Ordinal));

            // 2. The operations layer refuses on its own, even if a caller skipped validation.
            var ex = await Assert.ThrowsAsync<StorageOperationException>(() => operations.ResizePartitionAsync(request));
            output.WriteLine($"Operations refused: {ex.Message}");
            Assert.Contains("dirty", ex.Message, StringComparison.Ordinal);

            // The partition is untouched.
            disk = await WaitForDiskAsync(provider, diskNumber, _ => true);
            Assert.Equal(partition.SizeBytes, disk.Partitions.Single(p => p.Number == created.Number).SizeBytes);

            // 3. Every attempt, successful or not, is in the log.
            var log = await File.ReadAllTextAsync(OperationLog.Path);
            var fresh = log[(int)Math.Min(logSizeBefore, log.Length)..];
            output.WriteLine(fresh.Trim());
            Assert.Contains("Initialize", fresh, StringComparison.Ordinal);
            Assert.Contains("CreatePartition", fresh, StringComparison.Ordinal);
            Assert.Contains("-> OK", fresh, StringComparison.Ordinal);
            Assert.Contains("Resize", fresh, StringComparison.Ordinal);
            Assert.Contains("-> FAILED", fresh, StringComparison.Ordinal);
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

    [AdminFact]
    public void PowerStatus_IsReadable()
    {
        var status = new PowerStatusProvider().GetPowerStatus();

        Assert.NotNull(status);
        output.WriteLine($"AC: {status!.OnAcPower}, battery: {status.BatteryPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"} %");
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
