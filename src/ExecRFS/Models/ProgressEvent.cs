using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExecRFS.Models;

public class ProgressEvent
{
    [JsonPropertyName("event")] public string Event { get; set; } = "";
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("port")] public int? Port { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("side")] public string? Side { get; set; }
    [JsonPropertyName("files")] public int? Files { get; set; }
    [JsonPropertyName("bytes")] public long? Bytes { get; set; }
    [JsonPropertyName("transfers")] public int? Transfers { get; set; }
    [JsonPropertyName("deletes")] public int? Deletes { get; set; }
    [JsonPropertyName("skipped")] public int? Skipped { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
    [JsonPropertyName("compressed")] public bool? Compressed { get; set; }
    [JsonPropertyName("thread")] public int? Thread { get; set; }
    [JsonPropertyName("bytes_sent")] public long? BytesSent { get; set; }
    [JsonPropertyName("total_bytes")] public long? TotalBytes { get; set; }
    [JsonPropertyName("success")] public bool? Success { get; set; }
    [JsonPropertyName("backed_up")] public bool? BackedUp { get; set; }
    [JsonPropertyName("files_transferred")] public int? FilesTransferred { get; set; }
    [JsonPropertyName("files_deleted")] public int? FilesDeleted { get; set; }
    [JsonPropertyName("elapsed_ms")] public long? ElapsedMs { get; set; }
    [JsonPropertyName("exit_code")] public int? ExitCode { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("fatal")] public bool? Fatal { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }

    public static ProgressEvent? TryParse(string line)
    {
        try { return JsonSerializer.Deserialize<ProgressEvent>(line); }
        catch { return null; }
    }
}
