using System.Globalization;
using System.Text;
using WindowsPartitionManager.Core.Formatting;

namespace WindowsPartitionManager.Core.Analysis;

/// <summary>Plain-text rendering of a <see cref="ShrinkReport"/>, shared by the CLI and the UI.</summary>
public static class ShrinkReportFormatter
{
    public static string ToText(ShrinkReport report, int maxBlockers = 25)
    {
        ArgumentNullException.ThrowIfNull(report);

        var g = report.Geometry;
        var volumeSize = g.VolumeSizeBytes;
        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture, $"Shrink analysis for {report.DriveLetter}:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Volume size        {ByteSize.Format(volumeSize),12}   ({g.TotalClusters:N0} clusters of {g.BytesPerCluster:N0} bytes)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Used               {ByteSize.Format(report.UsedBytes),12}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  $MFT               starts at {ByteSize.Format(g.ClustersToBytes(g.MftStartLcn))}, {ByteSize.Format(g.MftValidDataLength)} in use");
        if (report.LayoutStats is { } stats)
        {
            var accounted = g.ClustersToBytes(stats.AllocatedClusters);
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Files scanned      {stats.FileCount,12:N0}   {stats.ExtentCount:N0} extents, {ByteSize.Format(accounted)} accounted for, in {stats.Elapsed.TotalSeconds:F1} s");
        }

        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"  Target size        {ByteSize.Format(report.TargetSizeBytes),12}   {(report.TargetWasExplicit ? "(requested)" : "(used data + 10 % headroom)")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Shrinkable by      {Shrink(volumeSize, report.TargetSizeBytes),12}   if the target is reached");
        sb.AppendLine();

        sb.AppendLine("How far the volume can shrink:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Without moving anything       to {ByteSize.Format(report.MinimumSizeWithoutMovingBytes),10}   (frees {Shrink(volumeSize, report.MinimumSizeWithoutMovingBytes)})");
        if (report.WindowsMinimumSizeBytes is { } win)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Windows Disk Management       to {ByteSize.Format(win),10}   (frees {Shrink(volumeSize, win)})");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"  After moving movable files    to {ByteSize.Format(report.MinimumSizeWithBlockersBytes),10}   (frees {Shrink(volumeSize, report.MinimumSizeWithBlockersBytes)})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  After removing blockers too   to {ByteSize.Format(report.MinimumSizeMetadataOnlyBytes),10}   (frees {Shrink(volumeSize, report.MinimumSizeMetadataOnlyBytes)}; only immovable NTFS data remains)");
        sb.AppendLine();

        if (report.BlockersPastTarget.Count == 0)
        {
            sb.AppendLine("No unmovable files past the target.");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Unmovable files past the target ({report.BlockersPastTarget.Count}), furthest first:");
            foreach (var blocker in report.BlockersPastTarget.Take(maxBlockers))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{blocker.Kind}] {blocker.Path}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"      ends at {ByteSize.Format(g.ClustersToBytes(blocker.FurthestEndLcn))}, {ByteSize.Format(g.ClustersToBytes(blocker.ClustersBeyondTarget))} of {ByteSize.Format(g.ClustersToBytes(blocker.TotalClusters))} past the target");
                sb.AppendLine(CultureInfo.InvariantCulture, $"      {blocker.Reason}");
            }

            if (report.BlockersPastTarget.Count > maxBlockers)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ... and {report.BlockersPastTarget.Count - maxBlockers} more.");
            }
        }

        if (report.BlockersBelowTarget > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Unmovable files below the target: {report.BlockersBelowTarget:N0} (they do not affect this target).");
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Movable files past the target: {report.MovableFilesBeyondTarget:N0} ({ByteSize.Format(report.MovableBytesBeyondTarget)}); Windows moves these itself.");
        if (report.UnattributedBytesBeyondTarget > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Unattributed used space past the target: {ByteSize.Format(report.UnattributedBytesBeyondTarget)}.");
        }

        sb.AppendLine();
        sb.AppendLine("What to do:");
        foreach (var line in report.Advice)
        {
            sb.AppendLine("  - " + line);
        }

        return sb.ToString();
    }

    private static string Shrink(ulong volumeSize, ulong newSize)
        => ByteSize.Format(volumeSize > newSize ? volumeSize - newSize : 0);
}
