using PowerDesk.Core.Logging;
using PowerDesk.Core.Storage;

namespace PowerDesk.Tests.Core;

public sealed class JsonStorageServiceTests
{
    private sealed class NullLogger : ILogger
    {
        public string LogFilePath => string.Empty;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }

    private sealed class Sample
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PowerDeskTests", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "settings.json");
        var svc = new JsonStorageService(new NullLogger());

        Assert.True(await svc.SaveAsync(file, new Sample { Name = "x", Count = 3 }));
        var loaded = await svc.LoadAsync(file, () => new Sample());

        Assert.Equal("x", loaded.Name);
        Assert.Equal(3, loaded.Count);
        Assert.False(File.Exists(file + ".tmp"));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsFactoryValue()
    {
        var file = Path.Combine(Path.GetTempPath(), "PowerDeskTests", Guid.NewGuid().ToString("N"), "nope.json");
        var svc = new JsonStorageService(new NullLogger());
        var loaded = await svc.LoadAsync(file, () => new Sample { Name = "default" });
        Assert.Equal("default", loaded.Name);
    }
}
