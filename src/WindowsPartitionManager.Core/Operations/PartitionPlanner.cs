using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>
/// Checks a <see cref="CreatePartitionRequest"/> against the current disk layout before anything
/// is written. Pure logic over the model so every rule is unit-testable.
/// </summary>
public static class PartitionPlanner
{
    public const ulong Alignment = FreeSpaceCalculator.DefaultAlignment;

    /// <summary>Smallest partition we agree to create; anything less is useless and some file systems refuse it.</summary>
    public const ulong MinimumSizeBytes = 8 * 1024 * 1024;

    /// <summary>Windows' own format tool refuses FAT32 volumes above 32 GB.</summary>
    public const ulong Fat32MaximumBytes = 32UL * 1024 * 1024 * 1024;

    private const int MbrPrimaryPartitionLimit = 4;

    public static readonly IReadOnlyList<string> SupportedFileSystems = ["NTFS", "exFAT", "FAT32", "ReFS"];

    /// <summary>
    /// Aligns the start up to 1 MiB and trims the size so the end does not move, mirroring what
    /// Disk Management does silently. Returns the request unchanged when it is already aligned.
    /// </summary>
    public static CreatePartitionRequest Normalize(CreatePartitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var alignedOffset = FreeSpaceCalculator.AlignUp(request.OffsetBytes, Alignment);
        if (alignedOffset == request.OffsetBytes)
        {
            return request;
        }

        var shift = alignedOffset - request.OffsetBytes;
        var size = request.SizeBytes > shift ? request.SizeBytes - shift : 0;
        return request with { OffsetBytes = alignedOffset, SizeBytes = size };
    }

    /// <summary>The free region of <paramref name="disk"/> that contains the request, or null.</summary>
    public static FreeRegion? FindContainingRegion(Disk disk, CreatePartitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(request);

        return FreeSpaceCalculator.Compute(disk)
            .FirstOrDefault(r => r.OffsetBytes <= request.OffsetBytes && request.EndBytes <= r.EndBytes);
    }

    /// <summary>Largest size that fits at the request's offset inside its free region, or 0.</summary>
    public static ulong MaximumSizeAt(Disk disk, ulong offsetBytes)
    {
        ArgumentNullException.ThrowIfNull(disk);

        var aligned = FreeSpaceCalculator.AlignUp(offsetBytes, Alignment);
        var region = FreeSpaceCalculator.Compute(disk).FirstOrDefault(r => r.OffsetBytes <= aligned && aligned < r.EndBytes);
        return region is null ? 0 : region.EndBytes - aligned;
    }

    public static IReadOnlyList<ValidationIssue> Validate(Disk disk, CreatePartitionRequest request, IReadOnlySet<char>? lettersInUse = null)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ValidationIssue>();
        void Error(string message) => issues.Add(new ValidationIssue(IssueSeverity.Error, message));
        void Warn(string message) => issues.Add(new ValidationIssue(IssueSeverity.Warning, message));

        if (request.DiskNumber != disk.Number)
        {
            Error($"Request targets disk {request.DiskNumber} but the layout is for disk {disk.Number}.");
            return issues;
        }

        if (disk.Style is PartitionStyle.Raw or PartitionStyle.Unknown)
        {
            Error("The disk has no partition table. Initialize it (GPT) before creating partitions.");
        }

        if (disk.IsReadOnly)
        {
            Error("The disk is read-only.");
        }

        if (disk.IsOffline)
        {
            Error("The disk is offline. Bring it online first.");
        }

        if (disk.Style == PartitionStyle.Mbr && disk.Partitions.Count(p => p.Kind != PartitionKind.Extended) >= MbrPrimaryPartitionLimit)
        {
            Error("An MBR disk can hold at most four primary partitions. Convert the disk to GPT or delete a partition.");
        }

        if (request.OffsetBytes % Alignment != 0)
        {
            Error($"The start offset must be aligned to {ByteSize.Format(Alignment)}; call Normalize first.");
        }

        if (request.SizeBytes < MinimumSizeBytes)
        {
            Error($"The partition must be at least {ByteSize.Format(MinimumSizeBytes)}.");
        }

        if (issues.Count == 0 || !issues.Any(i => i.IsError && i.Message.Contains("partition table", StringComparison.Ordinal)))
        {
            if (FindContainingRegion(disk, request) is null)
            {
                var max = MaximumSizeAt(disk, request.OffsetBytes);
                Error(max == 0
                    ? $"Offset {ByteSize.Format(request.OffsetBytes)} is not inside unallocated space."
                    : $"The partition does not fit: at most {ByteSize.Format(max)} is free at that offset, {ByteSize.Format(request.SizeBytes)} requested.");
            }
        }

        // Windows' own number is the final word: CreatePartition fails with "not enough free space" above it.
        if (disk.LargestFreeExtentBytes > 0 && request.SizeBytes > disk.LargestFreeExtentBytes)
        {
            Error($"Windows reports at most {ByteSize.Format(disk.LargestFreeExtentBytes)} of contiguous free space on this disk; {ByteSize.Format(request.SizeBytes)} requested.");
        }

        if (!SupportedFileSystems.Contains(request.FileSystem, StringComparer.OrdinalIgnoreCase))
        {
            Error($"Unsupported file system '{request.FileSystem}'. Use one of: {string.Join(", ", SupportedFileSystems)}.");
        }
        else if (request.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && request.SizeBytes > Fat32MaximumBytes)
        {
            Error("Windows cannot format FAT32 volumes larger than 32 GB. Use exFAT or NTFS.");
        }

        var labelLimit = request.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ? 11 : 32;
        if (request.Label.Length > labelLimit)
        {
            Error($"The label is too long for {request.FileSystem} (maximum {labelLimit} characters).");
        }

        if (request.DriveLetter is { } letter)
        {
            var upper = char.ToUpperInvariant(letter);
            if (upper < 'C' || upper > 'Z')
            {
                Error("Drive letter must be between C and Z.");
            }
            else if (lettersInUse is not null && lettersInUse.Contains(upper))
            {
                Error($"Drive letter {upper}: is already in use.");
            }
        }
        else
        {
            Warn("No drive letter chosen; Windows will assign the next free one.");
        }

        if (!request.QuickFormat)
        {
            Warn("A full format writes every sector and can take hours on large partitions.");
        }

        issues.AddRange(SafetyChecks.Disk(disk));
        return issues;
    }

    /// <summary>
    /// Rules for deleting a partition. Partitions Windows needs to boot are refused outright;
    /// everything else is allowed with a data-loss warning, since deletion is the only way to
    /// repartition a FAT32 or exFAT drive that cannot be shrunk.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> ValidateDelete(Disk disk, Partition partition)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);

        var issues = new List<ValidationIssue>();
        void Error(string message) => issues.Add(new ValidationIssue(IssueSeverity.Error, message));
        void Warn(string message) => issues.Add(new ValidationIssue(IssueSeverity.Warning, message));

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
            Error("This partition holds Windows or its boot files. Windows Partition Manager refuses to delete it.");
        }
        else if ((disk.IsSystem || disk.IsBoot) && partition.Kind != PartitionKind.Basic)
        {
            Error($"'{partition.TypeDescription}' on the Windows disk may be needed to start, repair or restore the computer. Only basic data partitions can be deleted there.");
        }

        if (partition.Kind == PartitionKind.Extended)
        {
            Error("Delete the logical drives inside the extended partition first.");
        }

        if (partition.Volume is { } volume && volume.UsedBytes > 0)
        {
            Warn($"The volume holds {ByteSize.Format(volume.UsedBytes)} of data" + (volume.Label is { Length: > 0 } l ? $" (\"{l}\")" : string.Empty) + ". Everything on it is lost. Back it up first.");
        }
        else
        {
            Warn("Everything on the partition is lost.");
        }

        issues.AddRange(SafetyChecks.Disk(disk));
        return issues;
    }

    /// <summary>Drive letters currently assigned to any partition or volume on the given disks.</summary>
    public static IReadOnlySet<char> LettersInUse(IEnumerable<Disk> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);

        var letters = new HashSet<char>();
        foreach (var disk in disks)
        {
            foreach (var partition in disk.Partitions)
            {
                if (partition.DriveLetter is { } l)
                {
                    letters.Add(char.ToUpperInvariant(l));
                }

                if (partition.Volume?.DriveLetter is { } v)
                {
                    letters.Add(char.ToUpperInvariant(v));
                }
            }
        }

        return letters;
    }
}
