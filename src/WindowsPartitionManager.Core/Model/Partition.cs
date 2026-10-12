namespace WindowsPartitionManager.Core.Model;

/// <summary>Coarse classification of a partition, independent of MBR/GPT encoding.</summary>
public enum PartitionKind
{
    Unknown,

    /// <summary>Regular data partition (NTFS, exFAT, FAT32, ReFS...).</summary>
    Basic,

    /// <summary>EFI System Partition.</summary>
    EfiSystem,

    /// <summary>Microsoft Reserved Partition (MSR) on GPT disks.</summary>
    MicrosoftReserved,

    /// <summary>Windows Recovery Environment partition.</summary>
    Recovery,

    /// <summary>MBR extended partition container. Logical drives inside are listed separately.</summary>
    Extended,

    /// <summary>Logical Disk Manager (dynamic disk) partition.</summary>
    Ldm,

    /// <summary>Storage Spaces pool partition.</summary>
    StorageSpaces,

    Linux,

    LinuxSwap,

    Other,
}

/// <summary>A partition table entry, with its mounted volume when one exists.</summary>
public sealed record Partition
{
    public required int Number { get; init; }

    public required ulong OffsetBytes { get; init; }

    public required ulong SizeBytes { get; init; }

    public PartitionKind Kind { get; init; } = PartitionKind.Unknown;

    /// <summary>Short human readable type, e.g. "Basic data", "EFI System".</summary>
    public string TypeDescription { get; init; } = "Unknown";

    public char? DriveLetter { get; init; }

    public bool IsSystem { get; init; }

    public bool IsBoot { get; init; }

    public bool IsHidden { get; init; }

    public bool IsActive { get; init; }

    public Volume? Volume { get; init; }

    /// <summary>Exclusive end offset of the partition.</summary>
    public ulong EndBytes => OffsetBytes + SizeBytes;
}
