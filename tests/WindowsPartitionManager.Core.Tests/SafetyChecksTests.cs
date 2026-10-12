using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class SafetyChecksTests
{
    [Fact]
    public void OnMainsPower_NoIssues()
    {
        Assert.Empty(SafetyChecks.Power(new PowerStatus(true, 100), touchesSystemVolume: true));
        Assert.Empty(SafetyChecks.Power(null, touchesSystemVolume: true));
        Assert.Empty(SafetyChecks.Power(new PowerStatus(null, null), touchesSystemVolume: false));
    }

    [Fact]
    public void OnBattery_WithCharge_OnlyWarnsForDataVolumes()
    {
        var issues = SafetyChecks.Power(new PowerStatus(false, 80), touchesSystemVolume: false);

        var issue = Assert.Single(issues);
        Assert.False(issue.IsError);
    }

    [Fact]
    public void OnBattery_SystemVolume_IsRefused()
    {
        var issues = SafetyChecks.Power(new PowerStatus(false, 80), touchesSystemVolume: true);

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("system volume", StringComparison.Ordinal));
    }

    [Fact]
    public void OnBattery_LowCharge_IsRefusedForEverything()
    {
        var issues = SafetyChecks.Power(new PowerStatus(false, SafetyChecks.MinimumBatteryPercent - 1), touchesSystemVolume: false);

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("Plug it in", StringComparison.Ordinal));
    }

    [Fact]
    public void DirtyOrUnhealthyVolume_IsRefused()
    {
        var dirty = new Volume { Path = "v", DriveLetter = 'E', FileSystem = "NTFS", IsDirty = true, Health = VolumeHealth.Healthy };
        var broken = new Volume { Path = "v", DriveLetter = 'E', FileSystem = "NTFS", Health = VolumeHealth.FullRepairNeeded };
        var fine = new Volume { Path = "v", DriveLetter = 'E', FileSystem = "NTFS", Health = VolumeHealth.Healthy };

        Assert.Contains(SafetyChecks.VolumeHealth(dirty), i => i.IsError && i.Message.Contains("chkdsk E:", StringComparison.Ordinal));
        Assert.Contains(SafetyChecks.VolumeHealth(broken), i => i.IsError && i.Message.Contains("full repair", StringComparison.Ordinal));
        Assert.Empty(SafetyChecks.VolumeHealth(fine));
    }

    [Fact]
    public void ResizePlanner_RefusesDirtyVolume()
    {
        var partition = new Partition
        {
            Number = 1,
            OffsetBytes = 1024 * 1024,
            SizeBytes = 50UL * 1024 * 1024 * 1024,
            Kind = PartitionKind.Basic,
            Volume = new Volume { Path = "v", FileSystem = "NTFS", SizeBytes = 50UL * 1024 * 1024 * 1024, FreeBytes = 40UL * 1024 * 1024 * 1024, IsDirty = true },
        };
        var disk = new Disk
        {
            Number = 1, FriendlyName = "T", BusType = "SATA", SizeBytes = 100UL * 1024 * 1024 * 1024,
            LogicalSectorSize = 512, PhysicalSectorSize = 4096, Style = PartitionStyle.Gpt, Partitions = [partition],
        };

        var issues = ResizePlanner.Validate(disk, partition, new ResizePartitionRequest { DiskNumber = 1, PartitionNumber = 1, NewSizeBytes = 20UL * 1024 * 1024 * 1024 });

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("dirty", StringComparison.Ordinal));
    }
}
