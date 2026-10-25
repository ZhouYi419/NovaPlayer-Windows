namespace NovaPlayer.Infrastructure.FileSystem;

public sealed record ImportedVideo(string Path, long Size, DateTime ModifiedAt);
public sealed record ImportResult(IReadOnlyList<ImportedVideo> Videos, int Skipped, bool Truncated);

public static class MediaImportService
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".flv", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg" };

    public static Task<ImportResult> ImportAsync(IEnumerable<string> inputs, CancellationToken token) => Task.Run(() =>
    {
        var result = new List<ImportedVideo>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        bool truncated = false;
        foreach (var input in inputs)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                IEnumerable<string> files = Directory.Exists(input)
                    ? Directory.EnumerateFiles(input, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                    : [input];
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    if (result.Count >= 10000) { truncated = true; break; }
                    if (!Extensions.Contains(Path.GetExtension(file))) { skipped++; continue; }
                    try
                    {
                        var info = new FileInfo(Path.GetFullPath(file));
                        if (!info.Exists) { skipped++; continue; }
                        if (known.Add(info.FullName)) result.Add(new(info.FullName, info.Length, info.LastWriteTimeUtc));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { skipped++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { skipped++; }
            if (truncated) break;
        }
        return new ImportResult(result.OrderBy(v => Path.GetFileName(v.Path), StringComparer.OrdinalIgnoreCase).ToArray(), skipped, truncated);
    }, token);
}
