using ExecRFS.Models;
using ExecRFS.Services;

namespace ExecRFS.Tests.Services;

public class CommandBuilderTests
{
    [Fact]
    public void Build_ServerMode_GeneratesCorrectArgs()
    {
        var profile = new SyncProfile { ServerFolder = @"D:\Sync", ServerPort = 15782 };
        var cmd = CommandBuilder.Build(profile, isServer: true);
        Assert.Contains("server", cmd);
        Assert.Contains(@"--folder ""D:\Sync""", cmd);
        Assert.Contains("--port 15782", cmd);
        Assert.DoesNotContain("--host", cmd);
        Assert.DoesNotContain("--bidirectional", cmd);
    }

    [Fact]
    public void Build_ClientMode_AllOptions()
    {
        var profile = new SyncProfile
        {
            ClientHost = "10.0.1.50", ClientFolder = @"C:\Sync", ClientPort = 20000,
            Bidirectional = true, DeleteEnabled = true,
            ClientBlockSize = 262144, ClientMaxThreads = 4,
            ClientBackupFolder = @"C:\Backups",
            IncludePatterns = new() { "*.cs", "*.csproj" },
            ExcludePatterns = new() { "*.tmp" },
            ClientLogFile = @"C:\Logs\sync.log"
        };
        var cmd = CommandBuilder.Build(profile, isServer: false);
        Assert.Contains("client", cmd);
        Assert.Contains(@"--host ""10.0.1.50""", cmd);
        Assert.Contains("--bidirectional", cmd);
        Assert.Contains("--delete", cmd);
        Assert.Contains("--block-size 262144", cmd);
        Assert.Contains("--max-threads 4", cmd);
        Assert.Contains(@"--backup-folder ""C:\Backups""", cmd);
        Assert.Contains(@"--include ""*.cs""", cmd);
        Assert.Contains(@"--exclude ""*.tmp""", cmd);
        Assert.Contains(@"--log ""C:\Logs\sync.log""", cmd);
    }

    [Fact]
    public void Build_DefaultValues_Omitted()
    {
        var profile = new SyncProfile { ServerFolder = @"D:\Sync" };
        var cmd = CommandBuilder.Build(profile, isServer: true);
        Assert.DoesNotContain("--block-size", cmd);
        Assert.DoesNotContain("--max-threads", cmd);
        Assert.DoesNotContain("--backup-folder", cmd);
    }

    [Fact]
    public void BuildForProcess_AppendsJsonProgress()
    {
        var profile = new SyncProfile { ServerFolder = @"D:\Sync" };
        var cmd = CommandBuilder.BuildForProcess(profile, isServer: true);
        Assert.Contains("--json-progress", cmd);
    }
}
