namespace WindowsPartitionManager.Core.Analysis;

/// <summary>NTFS volume parameters needed to reason about clusters (from FSCTL_GET_NTFS_VOLUME_DATA).</summary>
public sealed record VolumeGeometry
{
    public required uint BytesPerSector { get; init; }

    public required uint BytesPerCluster { get; init; }

    public required ulong TotalClusters { get; init; }

    public required ulong FreeClusters { get; init; }

    public required uint BytesPerFileRecordSegment { get; init; }

    /// <summary>First cluster of the $MFT data stream (its first extent).</summary>
    public required ulong MftStartLcn { get; init; }

    /// <summary>Bytes of the $MFT that are in use.</summary>
    public required ulong MftValidDataLength { get; init; }

    /// <summary>First cluster of $MFTMirr.</summary>
    public required ulong MftMirrorStartLcn { get; init; }

    public required ulong MftZoneStartLcn { get; init; }

    public required ulong MftZoneEndLcn { get; init; }

    public ulong VolumeSizeBytes => TotalClusters * BytesPerCluster;

    public ulong ClustersToBytes(ulong clusters) => clusters * BytesPerCluster;

    /// <summary>Rounds up so a partial cluster still counts as one.</summary>
    public ulong BytesToClusters(ulong bytes) => (bytes + BytesPerCluster - 1) / BytesPerCluster;
}

/// <summary>A run of clusters on disk. <see cref="EndLcn"/> is exclusive.</summary>
public readonly record struct ClusterExtent(ulong StartLcn, ulong Count)
{
    public ulong EndLcn => StartLcn + Count;

    /// <summary>Clusters of this extent located at or beyond <paramref name="lcn"/>.</summary>
    public ulong CountFrom(ulong lcn)
    {
        if (lcn <= StartLcn)
        {
            return Count;
        }

        return lcn >= EndLcn ? 0 : EndLcn - lcn;
    }
}

/// <summary>One stream of a file that occupies clusters: the main data, an index, or an alternate data stream.</summary>
/// <param name="Name">Empty for the default data stream; "$I30" for a directory index; the stream name otherwise.</param>
/// <param name="AttributeType">NTFS attribute type code, e.g. 0x80 $DATA, 0xA0 $INDEX_ALLOCATION.</param>
/// <param name="IsImmovable">Set when the file system itself refuses to relocate this stream.</param>
public sealed record StreamExtents(string Name, uint AttributeType, bool IsImmovable, IReadOnlyList<ClusterExtent> Extents)
{
    public ulong FurthestEndLcn => Extents.Count == 0 ? 0 : Extents.Max(e => e.EndLcn);
}

/// <summary>Where one file's data lives on the volume.</summary>
public sealed record FileExtents(ulong FileId, string Name, bool IsDirectory, IReadOnlyList<StreamExtents> Streams)
{
    /// <summary>True when any allocated stream is flagged immovable by NTFS.</summary>
    public bool IsImmovable => Streams.Any(s => s.IsImmovable && s.Extents.Count > 0);

    public IEnumerable<ClusterExtent> Extents => Streams.SelectMany(s => s.Extents);

    public ulong FurthestEndLcn => Streams.Count == 0 ? 0 : Streams.Max(s => s.FurthestEndLcn);

    public ulong TotalClusters => Extents.Aggregate(0UL, (sum, e) => sum + e.Count);

    public ulong ClustersFrom(ulong lcn) => Extents.Aggregate(0UL, (sum, e) => sum + e.CountFrom(lcn));
}

/// <summary>Totals from a full pass over the file layout, useful as an integrity check against the bitmap.</summary>
public sealed record VolumeLayoutStats(int FileCount, int StreamCount, int ExtentCount, ulong AllocatedClusters, TimeSpan Elapsed);
