namespace WindowsPartitionManager.Core.Model;

/// <summary>What Windows reports about a volume's file system integrity (MSFT_Volume.HealthStatus).</summary>
public enum VolumeHealth
{
    Unknown,
    Healthy,
    ScanNeeded,
    SpotFixNeeded,
    FullRepairNeeded,
}

/// <summary>A formatted file system that Windows mounted from a partition.</summary>
public sealed record Volume
{
    /// <summary>Volume GUID path, e.g. <c>\\?\Volume{...}\</c>.</summary>
    public required string Path { get; init; }

    public char? DriveLetter { get; init; }

    public string? Label { get; init; }

    /// <summary>File system name such as "NTFS", "exFAT", "FAT32", "ReFS" or empty when unknown.</summary>
    public string FileSystem { get; init; } = string.Empty;

    public ulong SizeBytes { get; init; }

    public ulong FreeBytes { get; init; }

    public VolumeHealth Health { get; init; } = VolumeHealth.Unknown;

    /// <summary>The NTFS dirty bit: set when the volume was not cleanly dismounted or errors were seen; chkdsk clears it.</summary>
    public bool IsDirty { get; init; }

    public ulong UsedBytes => SizeBytes >= FreeBytes ? SizeBytes - FreeBytes : 0;

    /// <summary>Fraction of the volume in use, between 0 and 1.</summary>
    public double UsedFraction => SizeBytes == 0 ? 0 : (double)UsedBytes / SizeBytes;
}
