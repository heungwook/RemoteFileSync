using System.Text;
using ExecRFS.Models;

namespace ExecRFS.Services;

public static class CommandBuilder
{
    public static string Build(SyncProfile profile, bool isServer)
    {
        var sb = new StringBuilder("RemoteFileSync.exe ");
        sb.Append(isServer ? "server" : "client");
        if (!isServer) sb.Append($" --host \"{profile.ClientHost}\"");
        sb.Append($" --folder \"{(isServer ? profile.ServerFolder : profile.ClientFolder)}\"");
        sb.Append($" --port {(isServer ? profile.ServerPort : profile.ClientPort)}");
        // Without this the CLI's loopback default silently makes a GUI-launched server
        // unreachable from any other machine, with no diagnostic.
        if (isServer && !string.IsNullOrWhiteSpace(profile.ServerBindAddress))
            sb.Append($" --bind \"{profile.ServerBindAddress}\"");
        var backupFolder = isServer ? profile.ServerBackupFolder : profile.ClientBackupFolder;
        if (!string.IsNullOrWhiteSpace(backupFolder)) sb.Append($" --backup-folder \"{backupFolder}\"");
        // Direction, mirror and archive are client-driven (like --delete), so they emit only on
        // the client branch. --mode replaces the deprecated --bidirectional; EffectiveMode
        // migrates an old profile that only set Bidirectional. Push is the CLI default, so it is
        // omitted, matching the omit-defaults style of the flags above.
        if (!isServer && profile.EffectiveMode != SyncMode.Push)
            sb.Append($" --mode {ModeToken(profile.EffectiveMode)}");
        if (!isServer && profile.DeleteEnabled) sb.Append(" --delete");
        if (!isServer && profile.MirrorDeletes) sb.Append(" --mirror");
        if (!isServer && !string.IsNullOrWhiteSpace(profile.ArchiveFolder))
            sb.Append($" --archive-folder \"{profile.ArchiveFolder}\"");
        if (!isServer && profile.ArchiveKeepDays != 30)
            sb.Append($" --archive-keep-days {profile.ArchiveKeepDays}");
        if (!isServer && profile.ArchiveMaxBytes > 0)
            sb.Append($" --archive-max-size {profile.ArchiveMaxBytes}");
        var blockSize = isServer ? profile.ServerBlockSize : profile.ClientBlockSize;
        if (blockSize != 65536) sb.Append($" --block-size {blockSize}");
        var maxThreads = isServer ? profile.ServerMaxThreads : profile.ClientMaxThreads;
        if (maxThreads > 1) sb.Append($" --max-threads {maxThreads}");
        foreach (var p in profile.IncludePatterns) sb.Append($" --include \"{p}\"");
        foreach (var p in profile.ExcludePatterns) sb.Append($" --exclude \"{p}\"");
        sb.Append(" --verbose");
        var logFile = isServer ? profile.ServerLogFile : profile.ClientLogFile;
        if (!string.IsNullOrWhiteSpace(logFile)) sb.Append($" --log \"{logFile}\"");
        return sb.ToString();
    }

    private static string ModeToken(SyncMode mode) => mode switch
    {
        SyncMode.Pull => "pull",
        SyncMode.TwoWay => "two-way",
        _ => "push",
    };

    public static string BuildForProcess(SyncProfile profile, bool isServer)
        => Build(profile, isServer) + " --json-progress";

    public static string BuildBoth(SyncProfile profile)
        => $"REM === Server Command ===\n{Build(profile, true)}\n\nREM === Client Command ===\n{Build(profile, false)}";
}
