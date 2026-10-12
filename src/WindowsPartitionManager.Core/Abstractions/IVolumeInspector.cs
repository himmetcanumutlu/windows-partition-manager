using WindowsPartitionManager.Core.Analysis;

namespace WindowsPartitionManager.Core.Abstractions;

/// <summary>Opens a mounted NTFS volume for low-level, read-only inspection.</summary>
public interface IVolumeInspector
{
    Task<IVolumeInspection> OpenAsync(char driveLetter, CancellationToken cancellationToken = default);
}

public sealed record InspectProgress(string Stage, long Done, long Total);

/// <summary>A read-only session on one NTFS volume.</summary>
public interface IVolumeInspection : IDisposable
{
    char DriveLetter { get; }

    VolumeGeometry Geometry { get; }

    ClusterBitmap Bitmap { get; }

    /// <summary>Totals of the layout pass; null until <see cref="GetFilesAsync"/> has run once.</summary>
    VolumeLayoutStats? LayoutStats { get; }

    /// <summary>
    /// Returns every file that matters for a shrink decision: files with an allocated extent ending
    /// beyond <paramref name="cutoffLcn"/>, files the file system flags as immovable anywhere on the
    /// volume, and files whose path satisfies <paramref name="includePath"/> (the caller's policy
    /// rules, e.g. page file or shadow copy storage). Files that match none are left out.
    /// </summary>
    Task<IReadOnlyList<FileExtents>> GetFilesAsync(
        ulong cutoffLcn,
        Func<string, bool>? includePath = null,
        IProgress<InspectProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Full path of a file relative to the volume root, e.g. <c>Windows\explorer.exe</c>.</summary>
    string GetPath(ulong fileId);
}

/// <summary>Asks Windows how far it would let a partition shrink or grow (Storage API GetSupportedSize).</summary>
public interface ISupportedSizeProvider
{
    Task<SupportedSize?> GetSupportedSizeAsync(int diskNumber, int partitionNumber, CancellationToken cancellationToken = default);
}

public sealed record SupportedSize(ulong MinimumBytes, ulong MaximumBytes);
