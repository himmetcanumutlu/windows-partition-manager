using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>Validation for shrinking and extending partitions, before anything is written.</summary>
public static class ResizePlanner
{
    public const ulong Alignment = FreeSpaceCalculator.DefaultAlignment;

    /// <summary>Below this much free space after a shrink, NTFS gets slow and fragmentation-prone.</summary>
    private const double ComfortableHeadroom = 0.05;

    private static readonly string[] ResizableFileSystems = ["NTFS", "ReFS"];

    public static bool CanResize(Partition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        return partition.Kind == PartitionKind.Basic
               && partition.Volume is { } volume
               && ResizableFileSystems.Contains(volume.FileSystem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Aligns the new size down to 1 MiB, as Windows would.</summary>
    public static ResizePartitionRequest Normalize(ResizePartitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var aligned = request.NewSizeBytes - (request.NewSizeBytes % Alignment);
        return aligned == request.NewSizeBytes ? request : request with { NewSizeBytes = aligned };
    }

    /// <summary>The largest size the partition can grow to: its own size plus the free region that starts right after it.</summary>
    public static ulong MaximumSize(Disk disk, Partition partition)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);

        var nextStart = FreeSpaceCalculator.AlignUp(partition.EndBytes, Alignment);
        var following = FreeSpaceCalculator.Compute(disk).FirstOrDefault(r => r.OffsetBytes == nextStart);
        return following is null ? partition.SizeBytes : following.EndBytes - partition.OffsetBytes;
    }

    /// <summary>Smallest size we would even attempt: the data has to fit.</summary>
    public static ulong MinimumSize(Partition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        var used = partition.Volume?.UsedBytes ?? partition.SizeBytes;
        return Math.Max(PartitionPlanner.MinimumSizeBytes, FreeSpaceCalculator.AlignUp(used, Alignment));
    }

    public static IReadOnlyList<ValidationIssue> Validate(Disk disk, Partition partition, ResizePartitionRequest request, SupportedSize? windowsLimits = null)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ValidationIssue>();
        void Error(string message) => issues.Add(new ValidationIssue(IssueSeverity.Error, message));
        void Warn(string message) => issues.Add(new ValidationIssue(IssueSeverity.Warning, message));

        if (request.DiskNumber != disk.Number || request.PartitionNumber != partition.Number)
        {
            Error("The request does not match the given disk and partition.");
            return issues;
        }

        if (disk.IsReadOnly)
        {
            Error("The disk is read-only.");
        }

        if (disk.IsOffline)
        {
            Error("The disk is offline.");
        }

        if (partition.Kind != PartitionKind.Basic)
        {
            Error($"Only basic data partitions can be resized; this one is '{partition.TypeDescription}'.");
            return issues;
        }

        if (partition.Volume is not { } volume)
        {
            Error("The partition has no mounted file system, so Windows cannot resize it safely.");
            return issues;
        }

        if (!ResizableFileSystems.Contains(volume.FileSystem, StringComparer.OrdinalIgnoreCase))
        {
            Error($"Windows can only shrink or extend NTFS and ReFS volumes; this one is {(volume.FileSystem.Length == 0 ? "unknown" : volume.FileSystem)}.");
            return issues;
        }

        issues.AddRange(SafetyChecks.VolumeHealth(volume));

        if (request.NewSizeBytes % Alignment != 0)
        {
            Error($"The new size must be a multiple of {ByteSize.Format(Alignment)}; call Normalize first.");
        }

        if (request.NewSizeBytes == partition.SizeBytes)
        {
            Error("The partition already has that size.");
            return issues;
        }

        if (request.NewSizeBytes < partition.SizeBytes)
        {
            ValidateShrink(partition, volume, request, windowsLimits, Error, Warn);
        }
        else
        {
            ValidateExtend(disk, partition, request, windowsLimits, Error);
        }

        issues.AddRange(SafetyChecks.Disk(disk));

        if (partition.IsBoot || partition.IsSystem)
        {
            Warn("This is the Windows system volume. Resizing it is supported, but back up important data first and do not power off during the operation.");
        }

        return issues;
    }

    private static void ValidateShrink(Partition partition, Volume volume, ResizePartitionRequest request, SupportedSize? limits, Action<string> error, Action<string> warn)
    {
        var floor = MinimumSize(partition);
        if (request.NewSizeBytes < PartitionPlanner.MinimumSizeBytes)
        {
            error($"The partition must stay at least {ByteSize.Format(PartitionPlanner.MinimumSizeBytes)}.");
            return;
        }

        if (request.NewSizeBytes < floor)
        {
            error($"The volume holds {ByteSize.Format(volume.UsedBytes)} of data; it cannot shrink below {ByteSize.Format(floor)}.");
            return;
        }

        if (limits is { } l && request.NewSizeBytes < l.MinimumBytes)
        {
            error($"Windows will not shrink this volume below {ByteSize.Format(l.MinimumBytes)} because of unmovable files. Run the shrink analysis to see which files, and how to unblock them.");
            return;
        }

        var freeAfter = request.NewSizeBytes - volume.UsedBytes;
        if (freeAfter < (ulong)(request.NewSizeBytes * ComfortableHeadroom))
        {
            warn($"Only {ByteSize.Format(freeAfter)} would stay free on the volume; NTFS works best with at least 5 % free.");
        }
    }

    private static void ValidateExtend(Disk disk, Partition partition, ResizePartitionRequest request, SupportedSize? limits, Action<string> error)
    {
        var max = MaximumSize(disk, partition);
        if (max == partition.SizeBytes)
        {
            error("There is no unallocated space directly after this partition, so it cannot be extended. Space must be contiguous and come after the partition.");
            return;
        }

        if (request.NewSizeBytes > max)
        {
            error($"At most {ByteSize.Format(max - partition.SizeBytes)} is free right after this partition; the request needs {ByteSize.Format(request.NewSizeBytes - partition.SizeBytes)} more.");
            return;
        }

        if (limits is { } l && request.NewSizeBytes > l.MaximumBytes)
        {
            error($"Windows reports a maximum size of {ByteSize.Format(l.MaximumBytes)} for this partition.");
        }
    }
}
