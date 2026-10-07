using System.Text.Json;

namespace Novalist.Extensions.Speech;

internal static class SpeechModelCache
{
    public static bool IsReady(string root, bool useMlx)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "models-ready.json")));
            var cache = document.RootElement;
            if (cache.GetProperty("backend").GetString() != (useMlx ? "mlx" : "torch")) return false;
            var models = cache.GetProperty("models").EnumerateObject().ToArray();
            return models.Length == 2 && models.All(model => SnapshotExists(model.Value));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static bool SnapshotExists(JsonElement model)
    {
        var path = model.GetProperty("path").GetString();
        if (string.IsNullOrWhiteSpace(path)) return false;
        var files = model.GetProperty("files").EnumerateObject().ToArray();
        return files.Length > 0 && files.All(file =>
        {
            var info = new FileInfo(Path.Combine(path, file.Name));
            return info.Exists && info.Length == file.Value.GetInt64();
        });
    }
}
