using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>Snapshot of the machine's power source.</summary>
/// <param name="OnAcPower">True when plugged in, false on battery, null when unknown (desktop without a battery reports true).</param>
/// <param name="BatteryPercent">0..100, or null when unknown.</param>
public sealed record PowerStatus(bool? OnAcPower, int? BatteryPercent);

/// <summary>
/// Pre-flight rules shared by every write operation. None of these protect against a bug in
/// Windows; they keep us from starting an operation in conditions where a power loss or a
/// pre-existing file system problem would turn a recoverable interruption into a bad day.
/// </summary>
public static class SafetyChecks
{
    /// <summary>Below this charge we refuse to write to any partition table.</summary>
    public const int MinimumBatteryPercent = 30;

    public static IReadOnlyList<ValidationIssue> Power(PowerStatus? status, bool touchesSystemVolume)
    {
        if (status is null || status.OnAcPower != false)
        {
            return [];
        }

        var percent = status.BatteryPercent;
        var charge = percent is { } p ? $" ({p} %)" : string.Empty;

        if (percent is { } low && low < MinimumBatteryPercent)
        {
            return [new ValidationIssue(IssueSeverity.Error, $"The computer is running on battery{charge}. Plug it in before changing partitions.")];
        }

        if (touchesSystemVolume)
        {
            return [new ValidationIssue(IssueSeverity.Error, $"The computer is running on battery{charge}. Resizing the Windows system volume is refused until it is plugged in.")];
        }

        return [new ValidationIssue(IssueSeverity.Warning, $"The computer is running on battery{charge}. A power loss mid-operation is recoverable, but plugging in first avoids the question.")];
    }

    /// <summary>Warnings about the disk itself: removable media can be yanked mid-operation.</summary>
    public static IReadOnlyList<ValidationIssue> Disk(Model.Disk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        if (disk.BusType.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
            disk.BusType.Equals("SD", StringComparison.OrdinalIgnoreCase) ||
            disk.BusType.Equals("MMC", StringComparison.OrdinalIgnoreCase))
        {
            return [new ValidationIssue(IssueSeverity.Warning, $"Disk {disk.Number} is a removable {disk.BusType} device. Do not unplug it until the operation has finished.")];
        }

        return [];
    }

    public static IReadOnlyList<ValidationIssue> VolumeHealth(Volume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var letter = volume.DriveLetter is { } l ? $"{l}:" : "the volume";
        var issues = new List<ValidationIssue>();

        if (volume.IsDirty)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, $"{letter} is marked dirty: Windows saw a problem or an unclean shutdown. Run 'chkdsk {letter} /f', reboot if asked, then try again."));
        }

        if (volume.Health is Model.VolumeHealth.ScanNeeded or Model.VolumeHealth.SpotFixNeeded or Model.VolumeHealth.FullRepairNeeded)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, $"Windows reports that {letter} needs repair ({Describe(volume.Health)}). Run 'chkdsk {letter} /f' first; resizing a damaged file system is refused."));
        }

        return issues;
    }

    private static string Describe(VolumeHealth health) => health switch
    {
        Model.VolumeHealth.ScanNeeded => "scan needed",
        Model.VolumeHealth.SpotFixNeeded => "spot fix needed",
        Model.VolumeHealth.FullRepairNeeded => "full repair needed",
        _ => health.ToString(),
    };
}
