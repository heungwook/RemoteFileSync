using RemoteFileSync.Models;

namespace RemoteFileSync.Tests;

public class CliParserTests
{
    [Theory]
    [InlineData("--host")]
    [InlineData("--port")]
    [InlineData("--folder")]
    [InlineData("--backup-folder")]
    [InlineData("--include")]
    [InlineData("--exclude")]
    [InlineData("--block-size")]
    [InlineData("--max-threads")]
    [InlineData("--log")]
    [InlineData("--bind")]
    [InlineData("--max-delete-percent")]
    public void MissingValueAfterFlag_ThrowsArgumentException(string flag)
    {
        // Previously args[++i] ran off the end and IndexOutOfRangeException escaped the
        // handler in Main, crashing with a raw stack trace instead of printing usage.
        Assert.Throws<ArgumentException>(() => Program.ParseArgs(new[] { "client", flag }));
    }

    [Theory]
    [InlineData("--port", "not-a-number")]
    [InlineData("--block-size", "12.5")]
    [InlineData("--max-threads", "")]
    [InlineData("--max-delete-percent", "abc")]
    public void NonNumericValue_ThrowsArgumentException(string flag, string value)
    {
        // FormatException is not an ArgumentException, so it used to escape too.
        Assert.Throws<ArgumentException>(() => Program.ParseArgs(new[] { "client", flag, value }));
    }

    [Fact]
    public void ParseArgs_UsesInvariantCultureForNumbers()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A locale using ',' as the decimal separator must not change flag parsing.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var options = Program.ParseArgs(new[] { "client", "--host", "h", "--folder", ".", "--port", "1234" });
            Assert.Equal(1234, options.Port);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void ParseArgs_ServerDefaultsToLoopback()
    {
        var options = Program.ParseArgs(new[] { "server", "--folder", "." });
        Assert.Equal("127.0.0.1", options.BindAddress);
    }

    [Fact]
    public void Validate_RejectsNonIpBindAddress()
    {
        var options = Program.ParseArgs(new[] { "server", "--folder", ".", "--bind", "example.com" });
        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("--bind", ex.Message);
    }

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

    [Theory]
    [InlineData("push", SyncMode.Push)]
    [InlineData("PUSH", SyncMode.Push)]
    [InlineData("pull", SyncMode.Pull)]
    [InlineData("Pull", SyncMode.Pull)]
    [InlineData("two-way", SyncMode.TwoWay)]
    [InlineData("TWO-WAY", SyncMode.TwoWay)]
    public void ParseArgs_ModeFlag_IsCaseInsensitive(string value, SyncMode expected)
    {
        var opts = Program.ParseArgs(new[] { "client", "--host", "h", "--folder", ".", "--mode", value });
        Assert.Equal(expected, opts.Mode);
    }

    [Theory]
    [InlineData("bidi")]
    [InlineData("twoway")]
    [InlineData("mirror")]
    [InlineData("")]
    public void ParseArgs_UnknownMode_ThrowsArgumentException(string value)
    {
        // Silently falling back to the Push default would send the user's files in the
        // opposite direction from the one they typed.
        var ex = Assert.Throws<ArgumentException>(
            () => Program.ParseArgs(new[] { "client", "--host", "h", "--folder", ".", "--mode", value }));
        Assert.Contains("--mode", ex.Message);
    }

    [Fact]
    public void ParseArgs_NoModeFlag_DefaultsToPush()
    {
        var opts = Program.ParseArgs(new[] { "client", "--host", "h", "--folder", "." });
        Assert.Equal(SyncMode.Push, opts.Mode);
        Assert.False(opts.Bidirectional);
    }

    [Theory]
    [InlineData("--bidirectional")]
    [InlineData("-b")]
    public void ParseArgs_BidirectionalAlias_SetsTwoWayMode(string flag)
    {
        // Deprecated but still accepted: existing scripts and ExecRFS profiles emit it.
        var opts = Program.ParseArgs(new[] { "client", "--host", "h", "--folder", ".", flag });
        Assert.Equal(SyncMode.TwoWay, opts.Mode);
        Assert.True(opts.Bidirectional);
    }

    [Fact]
    public void ParseArgs_MirrorFlag_SetsMirrorDeletes()
    {
        var opts = Program.ParseArgs(new[] { "client", "--host", "h", "--folder", ".", "--mirror" });
        Assert.True(opts.MirrorDeletes);
    }

    [Fact]
    public void ParseArgs_NoMirrorFlag_DefaultsFalse()
    {
        var opts = Program.ParseArgs(new[] { "client", "--host", "h", "--folder", "." });
        Assert.False(opts.MirrorDeletes);
    }

    [Fact]
    public void ParseArgs_ArchiveFolderAndRetentionFlags()
    {
        var args = new[]
        {
            "client", "--host", "h", "--folder", ".",
            "--archive-folder", @"C:\Archive",
            "--archive-keep-days", "7",
            "--archive-max-size", "512M"
        };
        var opts = Program.ParseArgs(args);

        Assert.Equal(@"C:\Archive", opts.ArchiveFolder);
        Assert.Equal(7, opts.ArchiveKeepDays);
        Assert.Equal(512L * 1024 * 1024, opts.ArchiveMaxBytes);
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1024", 1024L)]
    [InlineData("4k", 4L * 1024)]
    [InlineData("4K", 4L * 1024)]
    [InlineData("4KB", 4L * 1024)]
    [InlineData("20m", 20L * 1024 * 1024)]
    [InlineData("20MB", 20L * 1024 * 1024)]
    [InlineData("2G", 2L * 1024 * 1024 * 1024)]
    [InlineData("2gb", 2L * 1024 * 1024 * 1024)]
    public void ParseArgs_ArchiveMaxSize_AcceptsSuffixes(string value, long expected)
    {
        var opts = Program.ParseArgs(
            new[] { "client", "--host", "h", "--folder", ".", "--archive-max-size", value });
        Assert.Equal(expected, opts.ArchiveMaxBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("M")]
    [InlineData("MB")]
    [InlineData("abc")]
    [InlineData("1.5G")]
    [InlineData("10T")]
    [InlineData("-1M")]
    [InlineData("9999999999999999999G")]
    public void ParseArgs_ArchiveMaxSize_RejectsGarbage(string value)
    {
        // A silently-zero cap reads as "no cap" and lets the archive grow until the disk fills.
        var ex = Assert.Throws<ArgumentException>(
            () => Program.ParseArgs(new[] { "client", "--host", "h", "--folder", ".", "--archive-max-size", value }));
        Assert.Contains("--archive-max-size", ex.Message);
    }

    [Theory]
    [InlineData("--mode")]
    [InlineData("--archive-folder")]
    [InlineData("--archive-keep-days")]
    [InlineData("--archive-max-size")]
    public void ParseArgs_MissingValueAfterNewFlag_ThrowsArgumentException(string flag)
    {
        // Asserting the MESSAGE, not just the type: an unrecognised flag also throws
        // ArgumentException (from the default: arm), so a bare Assert.Throws would pass even
        // if the flag were never wired up, and would keep passing for a flag that read
        // args[++i] directly instead of going through NextValue.
        var ex = Assert.Throws<ArgumentException>(() => Program.ParseArgs(new[] { "client", flag }));
        Assert.Contains("Missing value for", ex.Message);
        Assert.Contains(flag, ex.Message);
    }
}
