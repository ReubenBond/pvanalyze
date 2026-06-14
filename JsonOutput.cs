using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PVAnalyze;

public static class JsonOutput
{
    public static async Task WriteAsync<T>(T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), value, typeInfo, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync().ConfigureAwait(false);
    }

    public static async Task WriteToFileAsync<T>(T value, string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken).ConfigureAwait(false);
    }
}
