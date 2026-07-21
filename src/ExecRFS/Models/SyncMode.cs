namespace ExecRFS.Models;

/// <summary>
/// Sync direction for the GUI profile. Mirrors the CLI's RemoteFileSync.Models.SyncMode
/// (ExecRFS does not reference the CLI project, so the enum is duplicated here). CommandBuilder
/// maps it to the CLI's <c>--mode push|pull|two-way</c> tokens; the numeric values match the CLI
/// wire values purely for readability and are not themselves serialized to the CLI.
/// </summary>
public enum SyncMode
{
    Push = 1,
    Pull = 2,
    TwoWay = 3,
}
