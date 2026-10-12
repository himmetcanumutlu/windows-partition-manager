using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Formatting;

namespace WindowsPartitionManager.Core.Analysis;

/// <summary>
/// Explains why a volume cannot be shrunk to a given size: which files Windows refuses to move,
/// where they sit, and what would unblock them.
/// </summary>
public static class ShrinkAnalyzer
{
    /// <summary>Default target when none is given: the used data plus 1/10 headroom (integer math, no rounding surprises).</summary>
    private const ulong DefaultHeadroomDivisor = 10;

    public static async Task<ShrinkReport> AnalyzeAsync(
        IVolumeInspection volume,
        ulong? targetSizeBytes = null,
        SupportedSize? windowsSupportedSize = null,
        IProgress<InspectProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var geometry = volume.Geometry;
        var bitmap = volume.Bitmap;
        var clusterSize = geometry.BytesPerCluster;

        progress?.Report(new InspectProgress("Reading allocation bitmap", 0, 1));
        var usedClusters = bitmap.UsedCount;
        var lastUsedLcn = bitmap.LastUsedLcn ?? 0;

        var targetClusters = targetSizeBytes is { } explicitTarget
            ? Math.Min(geometry.BytesToClusters(explicitTarget), geometry.TotalClusters)
            : Math.Min(usedClusters + (usedClusters + DefaultHeadroomDivisor - 1) / DefaultHeadroomDivisor, geometry.TotalClusters);
        targetClusters = Math.Max(targetClusters, 1);

        var files = await volume.GetFilesAsync(targetClusters, IsUnmovableByPolicy, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new InspectProgress("Classifying files", 0, 1));

        var blockers = new List<ShrinkBlocker>();
        var movableFiles = 0;
        ulong movableClustersBeyond = 0;
        ulong attributedClustersBeyond = 0;

        // Limits are global: a blocker anywhere on the volume caps how far it can shrink.
        var mftEnd = geometry.MftStartLcn + geometry.BytesToClusters(geometry.MftValidDataLength);
        var mirrorEnd = geometry.MftMirrorStartLcn + Math.Max(1, geometry.BytesToClusters(4UL * geometry.BytesPerFileRecordSegment));
        var metadataLimit = Math.Max(mftEnd, mirrorEnd);
        var blockerLimit = metadataLimit;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = volume.GetPath(file.FileId);
            var beyond = file.ClustersFrom(targetClusters);
            attributedClustersBeyond += beyond;

            var classification = Classify(path, file);
            if (classification is null)
            {
                if (beyond > 0)
                {
                    movableFiles++;
                    movableClustersBeyond += beyond;
                }

                continue;
            }

            var (kind, reason) = classification.Value;
            var end = file.FurthestEndLcn;
            if (end == 0)
            {
                continue; // resident or empty: occupies no clusters of its own
            }

            var blocker = new ShrinkBlocker(path, kind, reason, end, beyond, file.TotalClusters);
            blockers.Add(blocker);

            blockerLimit = Math.Max(blockerLimit, end);
            if (!blocker.IsRemovable)
            {
                metadataLimit = Math.Max(metadataLimit, end);
            }
        }

        // Whatever gets moved, the data itself still has to fit.
        blockerLimit = Math.Max(blockerLimit, usedClusters);
        metadataLimit = Math.Max(metadataLimit, usedClusters);

        var usedBeyond = bitmap.CountUsed(targetClusters, geometry.TotalClusters);
        var unattributed = usedBeyond > attributedClustersBeyond ? usedBeyond - attributedClustersBeyond : 0;

        var pastTarget = blockers
            .Where(b => b.FurthestEndLcn > targetClusters)
            .OrderByDescending(b => b.FurthestEndLcn)
            .ToList();

        var report = new ShrinkReport
        {
            DriveLetter = volume.DriveLetter,
            Geometry = geometry,
            LayoutStats = volume.LayoutStats,
            UsedBytes = usedClusters * clusterSize,
            TargetSizeBytes = targetClusters * clusterSize,
            TargetWasExplicit = targetSizeBytes.HasValue,
            MinimumSizeWithoutMovingBytes = (lastUsedLcn + 1) * clusterSize,
            WindowsMinimumSizeBytes = windowsSupportedSize?.MinimumBytes,
            MinimumSizeWithBlockersBytes = blockerLimit * clusterSize,
            MinimumSizeMetadataOnlyBytes = metadataLimit * clusterSize,
            BlockersPastTarget = pastTarget,
            BlockersBelowTarget = blockers.Count - pastTarget.Count,
            MovableFilesBeyondTarget = movableFiles,
            MovableBytesBeyondTarget = movableClustersBeyond * clusterSize,
            UnattributedBytesBeyondTarget = unattributed * clusterSize,
            Advice = [],
        };

        return report with { Advice = BuildAdvice(report) };
    }

    /// <summary>
    /// Adds Windows' own shrink limit to a finished report. Querying that limit is slow, so callers
    /// can run it in parallel with the analysis and merge it afterwards.
    /// </summary>
    public static ShrinkReport WithWindowsLimit(ShrinkReport report, SupportedSize? windowsSupportedSize)
    {
        ArgumentNullException.ThrowIfNull(report);

        var updated = report with { WindowsMinimumSizeBytes = windowsSupportedSize?.MinimumBytes };
        return updated with { Advice = BuildAdvice(updated) };
    }

    /// <summary>Paths Windows will not relocate regardless of what the file system flags say.</summary>
    public static bool IsUnmovableByPolicy(string path) => ClassifyByPath(path) is not null;

    /// <summary>Returns null when Windows can relocate the file during a shrink.</summary>
    public static (BlockerKind Kind, string Reason)? Classify(string path, FileExtents file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (ClassifyByPath(path) is { } byPath)
        {
            return byPath;
        }

        if (file.IsImmovable)
        {
            return (BlockerKind.FileSystemImmovable, "NTFS reports this file's data as immovable.");
        }

        return null;
    }

    private static (BlockerKind Kind, string Reason)? ClassifyByPath(string path)
    {
        var separator = path.LastIndexOf('\\');
        var name = separator < 0 ? path : path[(separator + 1)..];
        var isRoot = separator < 0;

        if (isRoot)
        {
            if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
            {
                return (BlockerKind.PageFile, "Page file; the memory manager keeps it open and Windows will not move it.");
            }

            if (name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase))
            {
                return (BlockerKind.HibernationFile, "Hibernation file; cannot be moved while hibernation is enabled.");
            }
        }

        if (name.StartsWith('$') && (isRoot || path.StartsWith("$Extend\\", StringComparison.OrdinalIgnoreCase)))
        {
            return (BlockerKind.NtfsMetadata, "NTFS metadata; cannot be moved while the volume is mounted.");
        }

        if (path.StartsWith("System Volume Information\\", StringComparison.OrdinalIgnoreCase))
        {
            return (BlockerKind.ShadowCopyStorage, "System Restore / shadow copy storage; Windows treats it as unmovable.");
        }

        return null;
    }

    private static List<string> BuildAdvice(ShrinkReport report)
    {
        var advice = new List<string>();
        var kinds = report.BlockersPastTarget.Select(b => b.Kind).ToHashSet();

        if (report.WindowsMinimumSizeBytes is { } windowsMin && windowsMin <= report.TargetSizeBytes && report.BlockersPastTarget.Count == 0)
        {
            advice.Add($"Windows itself reports it can shrink this volume to {ByteSize.Format(windowsMin)}, which already reaches the target. Disk Management should accept it.");
        }

        if (kinds.Contains(BlockerKind.PageFile))
        {
            advice.Add("Move the page file to another volume or disable it temporarily (System Properties > Advanced > Performance > Virtual memory), reboot, shrink, then restore it.");
        }

        if (kinds.Contains(BlockerKind.HibernationFile))
        {
            advice.Add("Turn hibernation off with 'powercfg /h off' (as administrator), reboot, shrink, then 'powercfg /h on' if you want it back. This also disables Fast Startup.");
        }

        if (kinds.Contains(BlockerKind.ShadowCopyStorage))
        {
            advice.Add($"Delete restore points / shadow copies on {report.DriveLetter}: ('vssadmin delete shadows /for={report.DriveLetter}: /all' as administrator) or turn System Protection off for this drive, shrink, then turn it back on.");
        }

        if (kinds.Contains(BlockerKind.NtfsMetadata) || kinds.Contains(BlockerKind.FileSystemImmovable))
        {
            advice.Add($"Immovable NTFS data ends at {ByteSize.Format(report.MinimumSizeMetadataOnlyBytes)}; the volume cannot be shrunk below that while mounted. Moving metadata is a planned offline feature.");
        }

        if (report.MovableFilesBeyondTarget > 0)
        {
            advice.Add($"{report.MovableFilesBeyondTarget:N0} movable file(s) ({ByteSize.Format(report.MovableBytesBeyondTarget)}) sit past the target; Windows relocates these automatically during a shrink, but consolidating first with 'defrag {report.DriveLetter}: /X' makes the shrink faster and more reliable.");
        }

        if (report.UnattributedBytesBeyondTarget > 0)
        {
            advice.Add($"{ByteSize.Format(report.UnattributedBytesBeyondTarget)} of used space past the target belongs to no enumerated file; treat the result as approximate.");
        }

        if (advice.Count == 0)
        {
            advice.Add("Nothing blocks this target. Windows should be able to shrink the volume to the requested size.");
        }

        return advice;
    }
}
