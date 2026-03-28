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
        var backupFolder = isServer ? profile.ServerBackupFolder : profile.ClientBackupFolder;
        if (!string.IsNullOrWhiteSpace(backupFolder)) sb.Append($" --backup-folder \"{backupFolder}\"");
        if (!isServer && profile.Bidirectional) sb.Append(" --bidirectional");
        if (!isServer && profile.DeleteEnabled) sb.Append(" --delete");
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

    public static string BuildForProcess(SyncProfile profile, bool isServer)
        => Build(profile, isServer) + " --json-progress";

    public static string BuildBoth(SyncProfile profile)
        => $"REM === Server Command ===\n{Build(profile, true)}\n\nREM === Client Command ===\n{Build(profile, false)}";
}
