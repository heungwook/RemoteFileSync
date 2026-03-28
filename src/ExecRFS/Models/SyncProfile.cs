namespace ExecRFS.Models;

public class SyncProfile
{
    public string Name { get; set; } = "Untitled";
    public string ServerFolder { get; set; } = "";
    public int ServerPort { get; set; } = 15782;
    public string? ServerBackupFolder { get; set; }
    public int ServerBlockSize { get; set; } = 65536;
    public int ServerMaxThreads { get; set; } = 1;
    public string ClientHost { get; set; } = "";
    public string ClientFolder { get; set; } = "";
    public int ClientPort { get; set; } = 15782;
    public string? ClientBackupFolder { get; set; }
    public bool Bidirectional { get; set; }
    public bool DeleteEnabled { get; set; }
    public int ClientBlockSize { get; set; } = 65536;
    public int ClientMaxThreads { get; set; } = 1;
    public List<string> IncludePatterns { get; set; } = new();
    public List<string> ExcludePatterns { get; set; } = new();
    public string? ServerLogFile { get; set; }
    public string? ClientLogFile { get; set; }
}
