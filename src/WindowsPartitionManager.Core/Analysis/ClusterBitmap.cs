using System.Numerics;

namespace WindowsPartitionManager.Core.Analysis;

/// <summary>Allocation bitmap of a volume: one bit per cluster, set when the cluster is in use.</summary>
public sealed class ClusterBitmap
{
    private readonly byte[] _bits;

    public ClusterBitmap(byte[] bits, ulong clusterCount)
    {
        ArgumentNullException.ThrowIfNull(bits);
        if ((ulong)bits.Length * 8 < clusterCount)
        {
            throw new ArgumentException("Bitmap is smaller than the cluster count.", nameof(bits));
        }

        _bits = bits;
        ClusterCount = clusterCount;
    }

    public ulong ClusterCount { get; }

    public bool IsUsed(ulong lcn)
    {
        if (lcn >= ClusterCount)
        {
            return false;
        }

        return (_bits[lcn >> 3] & (1 << (int)(lcn & 7))) != 0;
    }

    /// <summary>Number of clusters in use within [from, to).</summary>
    public ulong CountUsed(ulong from, ulong to)
    {
        to = Math.Min(to, ClusterCount);
        if (from >= to)
        {
            return 0;
        }

        ulong count = 0;
        var lcn = from;

        // Leading partial byte.
        while (lcn < to && (lcn & 7) != 0)
        {
            if (IsUsed(lcn))
            {
                count++;
            }

            lcn++;
        }

        // Whole bytes.
        while (lcn + 8 <= to)
        {
            count += (ulong)BitOperations.PopCount(_bits[lcn >> 3]);
            lcn += 8;
        }

        // Trailing partial byte.
        while (lcn < to)
        {
            if (IsUsed(lcn))
            {
                count++;
            }

            lcn++;
        }

        return count;
    }

    public ulong UsedCount => CountUsed(0, ClusterCount);

    /// <summary>Highest cluster in use, or null when the volume is empty.</summary>
    public ulong? LastUsedLcn
    {
        get
        {
            var lastByte = (long)((ClusterCount + 7) / 8) - 1;
            for (var i = lastByte; i >= 0; i--)
            {
                if (_bits[i] == 0)
                {
                    continue;
                }

                for (var bit = 7; bit >= 0; bit--)
                {
                    var lcn = (ulong)i * 8 + (ulong)bit;
                    if (lcn < ClusterCount && (_bits[i] & (1 << bit)) != 0)
                    {
                        return lcn;
                    }
                }
            }

            return null;
        }
    }
}
