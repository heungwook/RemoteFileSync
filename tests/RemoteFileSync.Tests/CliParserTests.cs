using RemoteFileSync.Models;

namespace RemoteFileSync.Tests;

public class CliParserTests
{
    [Fact]
    public void ParseArgs_ServerMode_MinimalArgs()
    {
        var args = new[] { "server", "--folder", @"C:\Sync" };
        var result = Program.ParseArgs(args);
        Assert.True(result.IsServer);
        Assert.Equal(@"C:\Sync", result.Folder);
        Assert.Equal(15782, result.Port);
    }

    [Fact]
    public void ParseArgs_ClientMode_AllOptions()
    {
        var args = new[]
        {
            "client", "--host", "192.168.1.100", "--port", "9999",
            "--folder", @"C:\Sync", "--bidirectional",
            "--include", "*.docx", "--include", "*.xlsx",
            "--exclude", "*.tmp",
            "--block-size", "131072", "--max-threads", "4",
            "--verbose", "--log", @"C:\Logs\sync.log",
            "--backup-folder", @"C:\Backup"
        };
        var result = Program.ParseArgs(args);
        Assert.False(result.IsServer);
        Assert.Equal("192.168.1.100", result.Host);
        Assert.Equal(9999, result.Port);
        Assert.Equal(@"C:\Sync", result.Folder);
        Assert.True(result.Bidirectional);
        Assert.Equal(new[] { "*.docx", "*.xlsx" }, result.IncludePatterns);
        Assert.Equal(new[] { "*.tmp" }, result.ExcludePatterns);
        Assert.Equal(131072, result.BlockSize);
        Assert.Equal(4, result.MaxThreads);
        Assert.True(result.Verbose);
        Assert.Equal(@"C:\Logs\sync.log", result.LogFile);
        Assert.Equal(@"C:\Backup", result.BackupFolder);
    }

    [Fact]
    public void ParseArgs_ShortFlags()
    {
        var args = new[] { "client", "-h", "10.0.0.1", "-p", "8080",
            "-f", @"C:\Data", "-b", "-v", "-t", "2", "-bs", "4096" };
        var result = Program.ParseArgs(args);
        Assert.Equal("10.0.0.1", result.Host);
        Assert.Equal(8080, result.Port);
        Assert.Equal(@"C:\Data", result.Folder);
        Assert.True(result.Bidirectional);
        Assert.True(result.Verbose);
        Assert.Equal(2, result.MaxThreads);
        Assert.Equal(4096, result.BlockSize);
    }

    [Fact]
    public void ParseArgs_NoArgs_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Program.ParseArgs(Array.Empty<string>()));
    }

    [Fact]
    public void ParseArgs_InvalidMode_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Program.ParseArgs(new[] { "watch" }));
    }

    [Fact]
    public void ParseArgs_ServerMode_IgnoresHost()
    {
        var args = new[] { "server", "--folder", @"C:\Sync", "--host", "ignored" };
        var result = Program.ParseArgs(args);
        Assert.True(result.IsServer);
        Assert.Equal("ignored", result.Host);
    }

    [Fact]
    public void ParseArgs_DeleteLongFlag_SetsDeleteEnabled()
    {
        var args = new[] { "client", "--host", "localhost", "--folder", @"C:\Sync", "--delete" };
        var opts = Program.ParseArgs(args);
        Assert.True(opts.DeleteEnabled);
    }

    [Fact]
    public void ParseArgs_DeleteShortFlag_SetsDeleteEnabled()
    {
        var args = new[] { "client", "--host", "localhost", "--folder", @"C:\Sync", "-d" };
        var opts = Program.ParseArgs(args);
        Assert.True(opts.DeleteEnabled);
    }

    [Fact]
    public void ParseArgs_NoDeleteFlag_DefaultsFalse()
    {
        var args = new[] { "client", "--host", "localhost", "--folder", @"C:\Sync" };
        var opts = Program.ParseArgs(args);
        Assert.False(opts.DeleteEnabled);
    }
}
