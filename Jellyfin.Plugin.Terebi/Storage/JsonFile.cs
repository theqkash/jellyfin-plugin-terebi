using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Storage;

/// <summary>
/// One JSON document on disk with atomic writes (temp file + move) and a lock for writers.
/// </summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static T Load<T>(string path, Func<T> fallback, ILogger? logger = null)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                var value = JsonSerializer.Deserialize<T>(stream, Options);
                if (value is not null)
                {
                    return value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Terebi: could not read {Path}, starting empty", path);
        }

        return fallback();
    }

    public static async Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        await Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await using (var stream = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            Lock.Release();
        }
    }
}
