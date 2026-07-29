using System.IO.Compression;
using System.Text.RegularExpressions;
using Novalist.Sdk.Hooks;

namespace Novalist.Extensions.Formats.Services;

/// <summary>
/// Checks a written EPUB against the structural rules a shop will reject it for.
///
/// This is not EPUBCheck and does not claim to be. EPUBCheck is a large Java
/// program that implements the whole specification, and shipping a
/// reimplementation of it would be a promise nobody could keep. What this does
/// is catch the handful of faults that actually happen and that a writer can do
/// something about, and say plainly that passing it is not the same as passing
/// a validator.
/// </summary>
public sealed partial class EpubPreflight : IExportPostProcessor
{
    public IReadOnlyList<string> Formats => ["Epub", "epub"];
    public string DisplayName => "EPUB preflight";

    [GeneratedRegex(@"<dc:(title|language|identifier)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MetadataRegex();

    [GeneratedRegex(@"<item\b[^>]*\bmedia-type\s*=\s*""image/", RegexOptions.IgnoreCase)]
    private static partial Regex ImageItemRegex();

    [GeneratedRegex(@"<img\b[^>]*\balt\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex ImgWithAltRegex();

    [GeneratedRegex(@"<img\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImgRegex();

    public async Task<ExportCheckResult> CheckAsync(
        string outputPath, string formatKey, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        var notes = new List<string>();

        if (!File.Exists(outputPath))
            return Fail("The file was not written.");

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(outputPath);
        }
        catch (InvalidDataException)
        {
            // An EPUB is a zip. If it will not open as one, nothing else here
            // can say anything useful about it.
            return Fail("The file is not a readable EPUB container.");
        }

        using (archive)
        {
            var names = archive.Entries.Select(e => e.FullName).ToList();

            // The mimetype entry has to be first and stored uncompressed. Readers
            // that sniff rather than parse rely on it sitting at a fixed offset.
            var first = archive.Entries.FirstOrDefault();
            if (first == null || first.FullName != "mimetype")
                problems.Add("The mimetype entry is missing or is not the first entry in the container.");
            else if (first.CompressedLength != first.Length)
                problems.Add("The mimetype entry is compressed; it has to be stored.");

            if (!names.Contains("META-INF/container.xml"))
                problems.Add("META-INF/container.xml is missing, so a reader cannot find the book.");

            var opfName = names.FirstOrDefault(n => n.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
            if (opfName == null)
            {
                problems.Add("No package document (.opf) in the container.");
            }
            else
            {
                var opf = await ReadAsync(archive, opfName, cancellationToken);
                foreach (var required in new[] { "title", "language", "identifier" })
                {
                    if (!MetadataRegex().Matches(opf).Any(
                            m => m.Groups[1].Value.Equals(required, StringComparison.OrdinalIgnoreCase)))
                        problems.Add($"The package document has no dc:{required}. Shops require it.");
                }

                if (!names.Any(n => n.EndsWith("nav.xhtml", StringComparison.OrdinalIgnoreCase))
                    && !names.Any(n => n.EndsWith("toc.ncx", StringComparison.OrdinalIgnoreCase)))
                    problems.Add("No table of contents (nav.xhtml or toc.ncx).");

                if (ImageItemRegex().IsMatch(opf) && !opf.Contains("cover-image", StringComparison.OrdinalIgnoreCase))
                    notes.Add("No cover image is marked as the cover. Most shops want one.");
            }

            // Alt text is the accessibility fault that most often gets a book
            // sent back, and the writer is the only one who can fix it.
            var missingAlt = 0;
            foreach (var entry in archive.Entries.Where(
                         e => e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
                              || e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
            {
                var html = await ReadAsync(archive, entry.FullName, cancellationToken);
                missingAlt += ImgRegex().Count(html) - ImgWithAltRegex().Count(html);
            }
            if (missingAlt > 0)
                problems.Add($"{missingAlt} image(s) have no alt text. A reader using a screen reader gets nothing for those.");

            notes.Add($"{names.Count} entries, {new FileInfo(outputPath).Length / 1024} KB.");
            notes.Add("This is a structural check, not EPUBCheck. Run a validator before a paid submission.");
        }

        return new ExportCheckResult
        {
            Ok = problems.Count == 0,
            Problems = problems,
            Notes = notes
        };
    }

    private static async Task<string> ReadAsync(
        ZipArchive archive, string name, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(name);
        if (entry == null) return string.Empty;
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static ExportCheckResult Fail(string problem)
        => new() { Ok = false, Problems = [problem] };
}
