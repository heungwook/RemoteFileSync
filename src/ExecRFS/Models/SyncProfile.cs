using System.Text.Json.Serialization;

namespace ExecRFS.Models;

public class SyncProfile
{
    public string Name { get; set; } = "Untitled";
    public string ServerFolder { get; set; } = "";
    public int ServerPort { get; set; } = 15782;

    /// <summary>
    /// Address the server binds to. Loopback by default — the protocol is unauthenticated,
    /// so exposing it grants any reachable peer read/write/delete in the sync folder.
    /// </summary>
    public string ServerBindAddress { get; set; } = "127.0.0.1";
    public string? ServerBackupFolder { get; set; }
    public int ServerBlockSize { get; set; } = 65536;
    public int ServerMaxThreads { get; set; } = 1;
    public string ClientHost { get; set; } = "";
    public string ClientFolder { get; set; } = "";
    public int ClientPort { get; set; } = 15782;
    public string? ClientBackupFolder { get; set; }

    /// <summary>
    /// Legacy direction flag. Retained only so profiles written before <see cref="Mode"/> existed
    /// still load: an old profile has Bidirectional but no Mode, and <see cref="EffectiveMode"/>
    /// migrates it. New profiles are driven by <see cref="Mode"/> and this stays whatever it was.
    /// </summary>
    public bool Bidirectional { get; set; }

    /// <summary>
    /// Sync direction. Null in a profile written before this field existed; consumers read
    /// <see cref="EffectiveMode"/>, which falls back to <see cref="Bidirectional"/>.
    /// </summary>
    public SyncMode? Mode { get; set; }

    public bool DeleteEnabled { get; set; }

    /// <summary>Propagate deletions as a mirror (also remove peer-only files). CLI: --mirror.</summary>
    public bool MirrorDeletes { get; set; }

    /// <summary>Where overwritten/conflicting copies are archived. CLI: --archive-folder.</summary>
    public string? ArchiveFolder { get; set; }

    /// <summary>Archive retention in days; 0 = keep forever. CLI default is 30. CLI: --archive-keep-days.</summary>
    public int ArchiveKeepDays { get; set; } = 30;

    /// <summary>Archive size cap in bytes; 0 = no cap. CLI: --archive-max-size.</summary>
    public long ArchiveMaxBytes { get; set; }

    /// <summary>
    /// The direction actually used, migrating a legacy profile that only has
    /// <see cref="Bidirectional"/> set. Not serialized.
    /// </summary>
    [JsonIgnore]
    public SyncMode EffectiveMode => Mode ?? (Bidirectional ? SyncMode.TwoWay : SyncMode.Push);
    public int ClientBlockSize { get; set; } = 65536;
    public int ClientMaxThreads { get; set; } = 1;
    public List<string> IncludePatterns { get; set; } = new();
    public List<string> ExcludePatterns { get; set; } = new();
    public string? ServerLogFile { get; set; }
    public string? ClientLogFile { get; set; }
}
