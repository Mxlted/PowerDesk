using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PowerDesk.Core.Logging;

namespace PowerDesk.Core.Storage;

/// <summary>
/// Atomic JSON read/write. On write, content is staged to <c>file.tmp</c> then File.Replace-d into place.
/// On read, if the primary file is missing or unreadable we fall back to the <c>.tmp</c> if present, then defaults.
/// </summary>
public sealed class JsonStorageService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonStorageService(ILogger log) => _log = log;

    public async Task<T> LoadAsync<T>(string path, Func<T> factory)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = TryRead<T>(path);
            if (result is not null) return result;

            var tmp = path + ".tmp";
            result = TryRead<T>(tmp);
            if (result is not null)
            {
                _log.Warn($"Recovered settings from tmp: {tmp}");
                return result;
            }
            return factory();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync<T>(string path, T value)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                var json = JsonSerializer.Serialize(value, Options);
                await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);

                if (File.Exists(path))
                {
                    var backup = path + ".bak";
                    try { File.Replace(tmp, path, backup, ignoreMetadataErrors: true); }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(tmp, path, overwrite: true);
                        File.Delete(tmp);
                    }
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Save failed for {path}: {ex.Message}", ex);
            }
        }
        finally { _gate.Release(); }
    }

    private T? TryRead<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return default;
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex)
        {
            _log.Warn($"Read failed for {path}: {ex.Message}");
            return default;
        }
    }
}
