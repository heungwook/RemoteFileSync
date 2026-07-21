using ExecRFS.Models;
using ExecRFS.Services;

namespace ExecRFS.Tests.Services;

public class CommandBuilderTests
{
    [Fact]
    public void Build_ServerMode_EmitsBindAddress()
    {
        // The CLI defaults to loopback. Without --bind here, every GUI-launched server would
        // be unreachable from another machine with no diagnostic.
        var profile = new SyncProfile { ServerFolder = @"D:\Sync", ServerBindAddress = "0.0.0.0" };
        var cmd = CommandBuilder.Build(profile, isServer: true);
        Assert.Contains(@"--bind ""0.0.0.0""", cmd);
    }

    [Fact]
    public void Build_ClientMode_DoesNotEmitBindAddress()
    {
        var profile = new SyncProfile { ClientFolder = @"D:\Sync", ClientHost = "host" };
        var cmd = CommandBuilder.Build(profile, isServer: false);
        Assert.DoesNotContain("--bind", cmd);
    }

    [Fact]
    public void Build_ServerMode_GeneratesCorrectArgs()
    {
        var profile = new SyncProfile { ServerFolder = @"D:\Sync", ServerPort = 15782 };
        var cmd = CommandBuilder.Build(profile, isServer: true);
        Assert.Contains("server", cmd);
        Assert.Contains(@"--folder ""D:\Sync""", cmd);
        Assert.Contains("--port 15782", cmd);
        Assert.DoesNotContain("--host", cmd);
        // Direction is client-driven, so the server command never carries --mode.
        Assert.DoesNotContain("--mode", cmd);
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
        // Bidirectional=true migrates to --mode two-way; the deprecated flag is no longer emitted.
        Assert.Contains("--mode two-way", cmd);
        Assert.DoesNotContain("--bidirectional", cmd);
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

    [Fact]
    public void Build_Client_EmitsModeTwoWay()
    {
        var profile = new SyncProfile { ClientFolder = @"C:\Sync", ClientHost = "h", Mode = SyncMode.TwoWay };
        var cmd = CommandBuilder.Build(profile, isServer: false);
        Assert.Contains("--mode two-way", cmd);
        Assert.DoesNotContain("--bidirectional", cmd);
    }

    [Fact]
    public void Build_Client_EmitsModePull()
    {
        var profile = new SyncProfile { ClientFolder = @"C:\Sync", ClientHost = "h", Mode = SyncMode.Pull };
        Assert.Contains("--mode pull", CommandBuilder.Build(profile, isServer: false));
    }

    [Fact]
    public void Build_Client_OmitsMode_ForPushDefault()
    {
        // Push is the CLI default; omit --mode, matching the file's omit-defaults style.
        var profile = new SyncProfile { ClientFolder = @"C:\Sync", ClientHost = "h", Mode = SyncMode.Push };
        Assert.DoesNotContain("--mode", CommandBuilder.Build(profile, isServer: false));
    }

    [Fact]
    public void Build_Client_MigratesLegacyBidirectionalToModeTwoWay()
    {
        // A profile written before Mode existed: Bidirectional=true, Mode=null -> --mode two-way.
        var profile = new SyncProfile { ClientFolder = @"C:\Sync", ClientHost = "h", Bidirectional = true };
        var cmd = CommandBuilder.Build(profile, isServer: false);
        Assert.Contains("--mode two-way", cmd);
        Assert.DoesNotContain("--bidirectional", cmd);
    }

    [Fact]
    public void Build_Client_EmitsMirrorAndArchiveOptions()
    {
        var profile = new SyncProfile
        {
            ClientFolder = @"C:\Sync", ClientHost = "h",
            MirrorDeletes = true,
            ArchiveFolder = @"C:\Archive",
            ArchiveKeepDays = 7,
            ArchiveMaxBytes = 5242880,
        };
        var cmd = CommandBuilder.Build(profile, isServer: false);
        Assert.Contains("--mirror", cmd);
        Assert.Contains(@"--archive-folder ""C:\Archive""", cmd);
        Assert.Contains("--archive-keep-days 7", cmd);
        Assert.Contains("--archive-max-size 5242880", cmd);
    }

    [Fact]
    public void Build_Client_OmitsArchiveAndMirrorDefaults()
    {
        // No mirror, no archive folder, keep-days at the CLI default (30), no size cap.
        var profile = new SyncProfile { ClientFolder = @"C:\Sync", ClientHost = "h" };
        var cmd = CommandBuilder.Build(profile, isServer: false);
        Assert.DoesNotContain("--mirror", cmd);
        Assert.DoesNotContain("--archive-folder", cmd);
        Assert.DoesNotContain("--archive-keep-days", cmd);
        Assert.DoesNotContain("--archive-max-size", cmd);
    }

    [Fact]
    public void Build_Server_OmitsModeAndMirror_ButEmitsArchive()
    {
        var profile = new SyncProfile
        {
            ServerFolder = @"D:\Sync", Mode = SyncMode.TwoWay,
            MirrorDeletes = true, ArchiveFolder = @"C:\Archive", ArchiveKeepDays = 7, ArchiveMaxBytes = 100,
        };
        var cmd = CommandBuilder.Build(profile, isServer: true);
        // Direction and the mirror bit are derived by the server from the handshake — client-only.
        Assert.DoesNotContain("--mode", cmd);
        Assert.DoesNotContain("--mirror", cmd);
        // Archive is read from the server's OWN options: in a push the server is the archiving side.
        Assert.Contains(@"--archive-folder ""C:\Archive""", cmd);
        Assert.Contains("--archive-keep-days 7", cmd);
        Assert.Contains("--archive-max-size 100", cmd);
    }
}
