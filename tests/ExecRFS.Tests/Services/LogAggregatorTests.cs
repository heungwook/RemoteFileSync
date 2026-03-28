using ExecRFS.Services;

namespace ExecRFS.Tests.Services;

public class LogAggregatorTests
{
    [Fact]
    public void AddLine_MergesSources()
    {
        var agg = new LogAggregator();
        agg.AddLine("SRV", "Server started");
        agg.AddLine("CLI", "Client connected");
        Assert.Equal(2, agg.Entries.Count);
        Assert.Equal("SRV", agg.Entries[0].Source);
        Assert.Equal("CLI", agg.Entries[1].Source);
    }

    [Fact]
    public void CircularBuffer_BoundsAt5000()
    {
        var agg = new LogAggregator();
        for (int i = 0; i < 5100; i++) agg.AddLine("SRV", $"Line {i}");
        Assert.Equal(5000, agg.Entries.Count);
        Assert.Contains("Line 5099", agg.Entries[^1].Message);
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        var agg = new LogAggregator();
        agg.AddLine("SRV", "test");
        agg.Clear();
        Assert.Empty(agg.Entries);
    }

    [Fact]
    public void ParseLevel_DetectsErrors()
    {
        var agg = new LogAggregator();
        agg.AddLine("SRV", "[ERR] Something failed");
        Assert.Equal("error", agg.Entries[0].Level);
    }

    [Fact]
    public void OnEntry_FiresForEachLine()
    {
        var agg = new LogAggregator();
        int count = 0;
        agg.OnEntry += _ => count++;
        agg.AddLine("SRV", "test1");
        agg.AddLine("CLI", "test2");
        Assert.Equal(2, count);
    }
}
