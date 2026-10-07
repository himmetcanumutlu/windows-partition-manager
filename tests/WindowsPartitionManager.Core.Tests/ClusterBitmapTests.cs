using WindowsPartitionManager.Core.Analysis;

namespace WindowsPartitionManager.Core.Tests;

public class ClusterBitmapTests
{
    [Fact]
    public void CountsAndLastUsed_AreCorrectAcrossByteBoundaries()
    {
        var bits = new byte[4]; // 32 clusters
        bits[0] = 0b1000_0001; // clusters 0 and 7
        bits[2] = 0b0000_0100; // cluster 18
        var bitmap = new ClusterBitmap(bits, 30);

        Assert.True(bitmap.IsUsed(0));
        Assert.True(bitmap.IsUsed(7));
        Assert.True(bitmap.IsUsed(18));
        Assert.False(bitmap.IsUsed(8));
        Assert.Equal(3UL, bitmap.UsedCount);
        Assert.Equal(18UL, bitmap.LastUsedLcn);
        Assert.Equal(2UL, bitmap.CountUsed(1, 19));
        Assert.Equal(0UL, bitmap.CountUsed(19, 30));
        Assert.Equal(1UL, bitmap.CountUsed(7, 8));
    }

    [Fact]
    public void BitsPastClusterCount_AreIgnored()
    {
        var bits = new byte[] { 0xFF };
        var bitmap = new ClusterBitmap(bits, 3);

        Assert.Equal(3UL, bitmap.UsedCount);
        Assert.Equal(2UL, bitmap.LastUsedLcn);
        Assert.False(bitmap.IsUsed(5));
    }

    [Fact]
    public void EmptyBitmap_HasNoLastUsedCluster()
    {
        Assert.Null(new ClusterBitmap(new byte[2], 16).LastUsedLcn);
    }
}
