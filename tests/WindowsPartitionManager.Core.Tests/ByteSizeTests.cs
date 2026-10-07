using WindowsPartitionManager.Core.Formatting;

namespace WindowsPartitionManager.Core.Tests;

public class ByteSizeTests
{
    [Theory]
    [InlineData("500GB", 500UL * 1024 * 1024 * 1024)]
    [InlineData("1.5 TB", 1536UL * 1024 * 1024 * 1024)]
    [InlineData("2048M", 2048UL * 1024 * 1024)]
    [InlineData("123456", 123456UL)]
    [InlineData("1,5tb", 1536UL * 1024 * 1024 * 1024)]
    public void TryParse_AcceptsCommonForms(string text, ulong expected)
    {
        Assert.True(ByteSize.TryParse(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12XB")]
    [InlineData("-5GB")]
    public void TryParse_RejectsGarbage(string text)
    {
        Assert.False(ByteSize.TryParse(text, out _));
    }

    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(1023UL, "1023 B")]
    [InlineData(1024UL, "1.00 KB")]
    [InlineData(1536UL * 1024 * 1024 * 1024, "1.50 TB")]
    [InlineData(199UL * 1024 * 1024 * 1024, "199 GB")]
    public void Format_MatchesWindowsStyle(ulong bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }
}
