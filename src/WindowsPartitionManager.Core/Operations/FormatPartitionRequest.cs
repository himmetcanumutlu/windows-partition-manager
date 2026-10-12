using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>Reformat an existing partition in place. Everything on it is lost.</summary>
public sealed record FormatPartitionRequest
{
    public required int DiskNumber { get; init; }

    public required int PartitionNumber { get; init; }

    /// <summary>"NTFS", "exFAT", "FAT32" or "ReFS".</summary>
    public string FileSystem { get; init; } = "NTFS";

    public string Label { get; init; } = string.Empty;

    public bool QuickFormat { get; init; } = true;

    public DiskFingerprint? ExpectedDisk { get; init; }

    public PartitionFingerprint? ExpectedPartition { get; init; }
}

public static class FormatPlanner
{
    public static bool CanFormat(Disk disk, Partition partition)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);
        return partition.Kind == PartitionKind.Basic && !partition.IsBoot && !partition.IsSystem && !disk.IsReadOnly && !disk.IsOffline;
    }

    public static IReadOnlyList<ValidationIssue> Validate(Disk disk, Partition partition, FormatPartitionRequest request)
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

        if (partition.IsBoot || partition.IsSystem)
        {
            Error("This partition holds Windows or its boot files. Windows Partition Manager refuses to format it.");
        }
        else if (partition.Kind != PartitionKind.Basic)
        {
            Error($"Only basic data partitions can be formatted; this one is '{partition.TypeDescription}'.");
        }

        if (!PartitionPlanner.SupportedFileSystems.Contains(request.FileSystem, StringComparer.OrdinalIgnoreCase))
        {
            Error($"Unsupported file system '{request.FileSystem}'. Use one of: {string.Join(", ", PartitionPlanner.SupportedFileSystems)}.");
        }
        else if (request.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && partition.SizeBytes > PartitionPlanner.Fat32MaximumBytes)
        {
            Error("Windows cannot format FAT32 volumes larger than 32 GB. Use exFAT or NTFS.");
        }

        var labelLimit = request.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ? 11 : 32;
        if (request.Label.Length > labelLimit)
        {
            Error($"The label is too long for {request.FileSystem} (maximum {labelLimit} characters).");
        }

        if (partition.Volume is { } volume && volume.UsedBytes > 0)
        {
            Warn($"The volume holds {ByteSize.Format(volume.UsedBytes)} of data" + (volume.Label is { Length: > 0 } l ? $" (\"{l}\")" : string.Empty) + ". Everything on it is lost. Back it up first.");
        }
        else
        {
            Warn("Everything on the partition is lost.");
        }

        if (!request.QuickFormat)
        {
            Warn("A full format writes every sector and can take hours on large partitions.");
        }

        if (request.FileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase) || request.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
        {
            Warn($"Windows cannot shrink or extend {request.FileSystem} volumes later. Choose NTFS if you may want to resize this partition.");
        }

        issues.AddRange(SafetyChecks.Disk(disk));
        return issues;
    }
}
