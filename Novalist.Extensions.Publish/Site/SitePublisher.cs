using System.Text;
using System.Text.Json;

namespace Novalist.Extensions.Publish.Site;

public static class SitePublisher
{
    private const string Manifest = ".novalist-publish.json";

    public static async Task WriteAsync(string outputPath, IReadOnlyList<SiteFile> files, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(root);
        var names = files.Select(file => file.RelativePath).ToArray();
        ValidateNames(names);
        var owned = await ReadManifestAsync(root, cancellationToken);
        foreach (var name in names)
            if (File.Exists(Path.Combine(root, name)) && !owned.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new IOException("The output folder contains files not owned by this publisher. Choose an empty folder.");

        var stage = Path.Combine(root, ".novalist-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in files)
                await File.WriteAllTextAsync(Path.Combine(stage, file.RelativePath), file.Content,
                    new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(stage, Manifest), JsonSerializer.Serialize(names), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Commit(root, stage, names, owned);
        }
        finally
        {
            // A failed rollback keeps its backup directory available for recovery.
            if (!Directory.Exists(Path.Combine(stage, "backup"))) Directory.Delete(stage, true);
        }
    }

    private static async Task<string[]> ReadManifestAsync(string root, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, Manifest);
        if (!File.Exists(path)) return [];
        try
        {
            var names = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(path, cancellationToken))
                ?? throw new IOException("The publish manifest is empty.");
            ValidateNames(names);
            return names;
        }
        catch (JsonException exception)
        {
            throw new IOException("The publish manifest is unreadable. Choose a new output folder.", exception);
        }
    }

    private static void ValidateNames(IReadOnlyList<string> names)
    {
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count
            || names.Any(name => string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name
                || name.Contains('/') || name.Contains('\\') || name == Manifest
                || !(name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || name == "robots.txt")))
            throw new IOException("The publisher produced an invalid or duplicate output filename.");
    }

    private static void Commit(string root, string stage, string[] names, string[] owned)
    {
        var backup = Path.Combine(stage, "backup");
        Directory.CreateDirectory(backup);
        var moved = new List<string>();
        var written = new List<string>();
        try
        {
            foreach (var name in owned.Append(Manifest))
            {
                var path = Path.Combine(root, name);
                if (!File.Exists(path)) continue;
                File.Move(path, Path.Combine(backup, name));
                moved.Add(name);
            }
            foreach (var name in names.Append(Manifest))
            {
                File.Move(Path.Combine(stage, name), Path.Combine(root, name));
                written.Add(name);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Restore(root, backup, moved, written);
            throw;
        }
        Directory.Delete(backup, true);
    }

    private static void Restore(string root, string backup, List<string> moved, List<string> written)
    {
        try
        {
            foreach (var name in written) File.Delete(Path.Combine(root, name));
            foreach (var name in moved) File.Move(Path.Combine(backup, name), Path.Combine(root, name));
            Directory.Delete(backup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Publishing failed; the previous files remain in the publisher's backup folder.", exception);
        }
    }
}
