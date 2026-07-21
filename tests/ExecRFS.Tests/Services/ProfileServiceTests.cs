using ExecRFS.Models;
using ExecRFS.Services;

namespace ExecRFS.Tests.Services;

public class ProfileServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProfileService _service;

    public ProfileServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"execrfs_test_{Guid.NewGuid()}");
        _service = new ProfileService(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var profile = new SyncProfile
        {
            Name = "Test Profile", ServerFolder = @"D:\Sync",
            ClientHost = "10.0.1.50", Bidirectional = true,
            IncludePatterns = new() { "*.cs", "*.csproj" }
        };
        _service.Save(profile);
        var loaded = _service.Load("Test Profile");
        Assert.Equal("Test Profile", loaded.Name);
        Assert.Equal(@"D:\Sync", loaded.ServerFolder);
        Assert.True(loaded.Bidirectional);
        Assert.Equal(2, loaded.IncludePatterns.Count);
    }

    [Fact]
    public void ListProfiles_ReturnsNames()
    {
        _service.Save(new SyncProfile { Name = "Alpha" });
        _service.Save(new SyncProfile { Name = "Beta" });
        var names = _service.ListProfiles();
        Assert.Equal(2, names.Count);
        Assert.Contains("alpha", names);
        Assert.Contains("beta", names);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        _service.Save(new SyncProfile { Name = "ToDelete" });
        Assert.Single(_service.ListProfiles());
        _service.Delete("ToDelete");
        Assert.Empty(_service.ListProfiles());
    }

    [Fact]
    public void AutoSave_And_LoadLastSession()
    {
        _service.CurrentProfile = new SyncProfile { Name = "Current", ClientHost = "192.168.1.1" };
        _service.AutoSave();
        var loaded = _service.LoadLastSession();
        Assert.Equal("Current", loaded.Name);
        Assert.Equal("192.168.1.1", loaded.ClientHost);
    }

    [Fact]
    public void Load_LegacyProfileWithoutMode_MigratesBidirectionalToTwoWay()
    {
        // A profile written before the Mode/Archive fields existed — only Bidirectional is present.
        File.WriteAllText(Path.Combine(_tempDir, "legacy.json"),
            "{ \"Name\": \"Legacy\", \"Bidirectional\": true }");

        var loaded = _service.Load("Legacy");

        // Migrated to an explicit Mode so the profile is self-describing going forward.
        Assert.Equal(SyncMode.TwoWay, loaded.Mode);
        Assert.Equal(SyncMode.TwoWay, loaded.EffectiveMode);
        // New fields take their defaults on an old profile.
        Assert.Equal(30, loaded.ArchiveKeepDays);
        Assert.False(loaded.MirrorDeletes);
        Assert.Null(loaded.ArchiveFolder);
    }

    [Fact]
    public void Load_LegacyProfileWithoutBidirectional_MigratesToPush()
    {
        File.WriteAllText(Path.Combine(_tempDir, "old.json"), "{ \"Name\": \"Old\" }");

        var loaded = _service.Load("Old");

        Assert.Equal(SyncMode.Push, loaded.Mode);
    }

    [Fact]
    public void SaveAndLoad_ModeAndArchiveFields_RoundTrip()
    {
        var profile = new SyncProfile
        {
            Name = "Modern", Mode = SyncMode.Pull, MirrorDeletes = true,
            ArchiveFolder = @"C:\Archive", ArchiveKeepDays = 7, ArchiveMaxBytes = 1048576,
        };
        _service.Save(profile);

        var loaded = _service.Load("Modern");
        Assert.Equal(SyncMode.Pull, loaded.Mode);
        Assert.True(loaded.MirrorDeletes);
        Assert.Equal(@"C:\Archive", loaded.ArchiveFolder);
        Assert.Equal(7, loaded.ArchiveKeepDays);
        Assert.Equal(1048576L, loaded.ArchiveMaxBytes);
    }
}
