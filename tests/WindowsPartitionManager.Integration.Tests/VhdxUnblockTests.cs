using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Platform.Windows;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>
/// The shadow-copy part of the unblock wizard against a real VSS snapshot on a throwaway VHDX.
/// Hibernation and page file settings are only read here: changing them would alter the
/// developer's machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VhdxUnblockTests(ITestOutputHelper output)
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [AdminFact]
    public async Task ShadowCopies_AreCountedAndDeleted_OnVhdxVolume()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wpm-vss-{Guid.NewGuid():N}.vhdx");
        var provider = new WmiStorageProvider();
        var operations = new WmiStorageOperations();
        var unblock = new UnblockOperations();

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
                Label = "Shadow",
            });
            var letter = created.DriveLetter!.Value;
            await File.WriteAllTextAsync($@"{letter}:\before.txt", "snapshot me");

            Assert.Equal(0, unblock.CountShadowCopies(letter));

            // Take a VSS snapshot the way System Restore / Previous Versions would.
            var scope = new ManagementScope(@"\\.\root\cimv2");
            scope.Connect();
            using var shadowClass = new ManagementClass(scope, new ManagementPath("Win32_ShadowCopy"), null);
            using var inParams = shadowClass.GetMethodParameters("Create");
            inParams["Volume"] = $@"{letter}:\";
            inParams["Context"] = "ClientAccessible";
            using var outParams = shadowClass.InvokeMethod("Create", inParams, null);
            var returnValue = Convert.ToUInt32(outParams["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
            output.WriteLine($"Win32_ShadowCopy.Create -> {returnValue}, id {outParams["ShadowID"]}");
            Assert.Equal(0u, returnValue);

            var before = unblock.CountShadowCopies(letter);
            output.WriteLine($"Shadow copies on {letter}: before delete: {before}");
            Assert.True(before >= 1);

            // The shrink analysis must now see the shadow storage as unmovable (it lives under System Volume Information).
            using (var volume = await new NtfsVolumeInspector().OpenAsync(letter))
            {
                var report = await Core.Analysis.ShrinkAnalyzer.AnalyzeAsync(volume, targetSizeBytes: 64 * MiB);
                var steps = UnblockPlanner.Plan(report);
                output.WriteLine(Core.Analysis.ShrinkReportFormatter.ToText(report));
                output.WriteLine(UnblockPlanner.Summarize(steps));

                // The diff area is small and may sit anywhere; it must be classified as a blocker somewhere on the volume.
                var seen = report.BlockersPastTarget.Any(b => b.Kind == Core.Analysis.BlockerKind.ShadowCopyStorage) || report.BlockersBelowTarget > 0;
                Assert.True(seen, "shadow copy storage should be reported as unmovable");
                if (report.BlockersPastTarget.Any(b => b.Kind == Core.Analysis.BlockerKind.ShadowCopyStorage))
                {
                    Assert.Contains(steps, s => s.Action == UnblockAction.DeleteShadowCopies);
                }
            }

            var deleted = await unblock.DeleteShadowCopiesAsync(letter);
            output.WriteLine($"Deleted {deleted}");
            Assert.True(deleted >= 1);
            Assert.Equal(0, unblock.CountShadowCopies(letter));
            Assert.Equal("snapshot me", await File.ReadAllTextAsync($@"{letter}:\before.txt"));
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
    public void HibernationAndPageFileSettings_AreReadable()
    {
        var unblock = new UnblockOperations();

        var hibernation = unblock.IsHibernationEnabled();
        var automatic = unblock.IsPageFileAutomatic();
        var pageFiles = unblock.GetPageFiles();

        output.WriteLine($"Hibernation enabled: {hibernation}");
        output.WriteLine($"Automatic page file management: {automatic}");
        foreach (var pf in pageFiles)
        {
            output.WriteLine($"Page file on {pf.Volume}: {pf.InitialMb}-{pf.MaximumMb} MB");
        }

        // No assertion on values: they describe the developer's machine. The calls must simply work.
        Assert.NotNull(pageFiles);
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
