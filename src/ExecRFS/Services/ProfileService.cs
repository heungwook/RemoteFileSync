using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExecRFS.Models;

namespace ExecRFS.Services;

public partial class ProfileService
{
    private readonly string _profileDir;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public SyncProfile CurrentProfile { get; set; } = new();

    public ProfileService()
    {
        _profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteFileSync", "profiles");
        Directory.CreateDirectory(_profileDir);
    }

    public ProfileService(string profileDir)
    {
        _profileDir = profileDir;
        Directory.CreateDirectory(_profileDir);
    }

    public List<string> ListProfiles()
        => Directory.GetFiles(_profileDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null && n != "_last-session")
            .Cast<string>().OrderBy(n => n).ToList();

    public SyncProfile Load(string name)
    {
        var path = Path.Combine(_profileDir, SanitizeFileName(name) + ".json");
        if (!File.Exists(path)) throw new FileNotFoundException($"Profile not found: {name}");
        return JsonSerializer.Deserialize<SyncProfile>(File.ReadAllText(path), JsonOpts) ?? new();
    }

    public void Save(SyncProfile profile)
    {
        var path = Path.Combine(_profileDir, SanitizeFileName(profile.Name) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOpts));
    }

    public void Delete(string name)
    {
        var path = Path.Combine(_profileDir, SanitizeFileName(name) + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    public void AutoSave()
    {
        File.WriteAllText(
            Path.Combine(_profileDir, "_last-session.json"),
            JsonSerializer.Serialize(CurrentProfile, JsonOpts));
    }

    public SyncProfile LoadLastSession()
    {
        var path = Path.Combine(_profileDir, "_last-session.json");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<SyncProfile>(File.ReadAllText(path), JsonOpts) ?? new(); }
        catch { return new(); }
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = InvalidCharsRegex().Replace(name.ToLowerInvariant().Replace(' ', '-'), "");
        return string.IsNullOrWhiteSpace(sanitized) ? "untitled" : sanitized;
    }

    [GeneratedRegex("[^a-z0-9\\-_]")]
    private static partial Regex InvalidCharsRegex();
}
