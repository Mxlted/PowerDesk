using PowerDesk.Core.Logging;
using PowerDesk.Core.Models;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;

namespace PowerDesk.Tests.Core;

public sealed class ServicesTests
{
    [Fact]
    public void StatusService_SetUpdatesMessageAndKind()
    {
        var status = new StatusService();
        var changed = new List<string>();
        status.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        status.Set("Working", StatusKind.Warning);

        Assert.Equal("Working", status.Message);
        Assert.Equal(StatusKind.Warning, status.Kind);
        Assert.Contains(nameof(StatusService.Message), changed);
        Assert.Contains(nameof(StatusService.Kind), changed);
    }

    [Fact]
    public void RecentActions_InsertsNewestFirstAndCapsAtFifty()
    {
        var svc = new RecentActionsService();
        for (var i = 0; i < 60; i++) svc.Add("Mod", $"action {i}");

        Assert.Equal(50, svc.Items.Count);
        Assert.Equal("action 59", svc.Items[0].Description);
        Assert.Equal("action 10", svc.Items[^1].Description);
        Assert.Equal("Mod", svc.Items[0].Module);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", svc.Items[0].TimestampDisplay);
    }

    [Fact]
    public void ModuleRegistry_IgnoresDuplicateIdsAndFindsById()
    {
        var reg = new ModuleRegistry();
        reg.Register(new FakeModule("A"));
        reg.Register(new FakeModule("A"));
        reg.Register(new FakeModule("B"));

        Assert.Equal(2, reg.Modules.Count);
        Assert.NotNull(reg.FindById("B"));
        Assert.Null(reg.FindById("C"));
    }

    [Fact]
    public async Task AppSettings_RoundTripsThroughStorageIncludingEnum()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PowerDeskTests", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "settings.json");
        var storage = new JsonStorageService(new NullLogger());

        var settings = new AppSettings
        {
            Theme = AppTheme.OledDark,
            StartMinimized = true,
            MinimizeToTrayOnClose = false,
            GlobalHotkeysEnabled = false,
            LastPage = "HashDesk",
        };
        Assert.True(await storage.SaveAsync(file, settings));
        var loaded = await storage.LoadAsync(file, () => new AppSettings());

        Assert.Equal(AppTheme.OledDark, loaded.Theme);
        Assert.True(loaded.StartMinimized);
        Assert.False(loaded.MinimizeToTrayOnClose);
        Assert.False(loaded.GlobalHotkeysEnabled);
        Assert.Equal("HashDesk", loaded.LastPage);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Storage_CorruptFile_FallsBackToTmpThenDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PowerDeskTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        var storage = new JsonStorageService(new NullLogger());

        await File.WriteAllTextAsync(file, "{ this is not json");
        var loaded = await storage.LoadAsync(file, () => new AppSettings { LastPage = "fallback" });
        Assert.Equal("fallback", loaded.LastPage);

        await File.WriteAllTextAsync(file + ".tmp", "{\"lastPage\":\"from-tmp\"}");
        loaded = await storage.LoadAsync(file, () => new AppSettings { LastPage = "fallback" });
        Assert.Equal("from-tmp", loaded.LastPage);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Storage_SaveOverwritesExistingAtomically()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PowerDeskTests", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "settings.json");
        var storage = new JsonStorageService(new NullLogger());

        Assert.True(await storage.SaveAsync(file, new AppSettings { LastPage = "one" }));
        Assert.True(await storage.SaveAsync(file, new AppSettings { LastPage = "two" }));
        var loaded = await storage.LoadAsync(file, () => new AppSettings());

        Assert.Equal("two", loaded.LastPage);
        Assert.False(File.Exists(file + ".tmp"));
        Assert.False(File.Exists(file + ".bak"));
        Directory.Delete(dir, recursive: true);
    }

    private sealed class FakeModule : IPowerDeskModule
    {
        public FakeModule(string id) => Id = id;
        public string Id { get; }
        public string DisplayName => Id;
        public string Description => "";
        public string IconKey => "";
        public string IconGeometry => "";
        public bool RequiresAdminForFullControl => false;
        public System.Windows.Controls.UserControl MainView => throw new NotSupportedException();
        public Task InitializeAsync() => Task.CompletedTask;
        public Task ShutdownAsync() => Task.CompletedTask;
    }

    private sealed class NullLogger : ILogger
    {
        public string LogFilePath => string.Empty;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
