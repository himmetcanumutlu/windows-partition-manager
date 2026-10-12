namespace WindowsPartitionManager.Core.Operations;

/// <summary>What the user asked for: a new, formatted partition inside unallocated space.</summary>
public sealed record CreatePartitionRequest
{
    public required int DiskNumber { get; init; }

    /// <summary>Start of the new partition. Aligned up to 1 MiB by <see cref="PartitionPlanner.Normalize"/>.</summary>
    public required ulong OffsetBytes { get; init; }

    public required ulong SizeBytes { get; init; }

    /// <summary>"NTFS", "exFAT", "FAT32" or "ReFS".</summary>
    public string FileSystem { get; init; } = "NTFS";

    public string Label { get; init; } = string.Empty;

    /// <summary>Drive letter to assign, or null to let Windows pick the next free one.</summary>
    public char? DriveLetter { get; init; }

    /// <summary>Quick format (does not zero every sector). Full formats take hours on large disks.</summary>
    public bool QuickFormat { get; init; } = true;

    /// <summary>The disk as the user saw it; the operation is refused if it no longer matches.</summary>
    public DiskFingerprint? ExpectedDisk { get; init; }

    public ulong EndBytes => OffsetBytes + SizeBytes;
}

public enum IssueSeverity
{
    Warning,
    Error,
}

public sealed record ValidationIssue(IssueSeverity Severity, string Message)
{
    public bool IsError => Severity == IssueSeverity.Error;
}
