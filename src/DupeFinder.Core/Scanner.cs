using System.Security.Cryptography;

namespace DupeFinder.Core;

public enum ScanState { Pending, Hashing, Complete, Failed, Deleted }

public sealed class Entry(string path, string relativePath, bool isDirectory)
{
    public string Path { get; } = path;
    public string RelativePath { get; } = relativePath;
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsDirectory { get; } = isDirectory;
    public List<Entry> Children { get; } = [];
    public ScanState State { get; set; }
    public string? Error { get; set; }
    public string? Hash { get; set; }
    public long Length { get; set; }
    public bool Deleted => State == ScanState.Deleted;
    public IEnumerable<Entry> Descendants() => Children.SelectMany(c => new[] { c }.Concat(c.Descendants()));
}

public sealed class ScanResult(Entry root)
{
    public Entry Root { get; } = root;
    public List<Entry> Files { get; } = root.Descendants().Where(e => !e.IsDirectory).ToList();
    public List<DuplicateGroup> Groups { get; set; } = [];
    public List<FolderCandidate> Folders { get; set; } = [];
}

public sealed class DuplicateGroup(string hash, List<Entry> files)
{
    public string Hash { get; } = hash;
    public List<Entry> Files { get; } = files;
    public IEnumerable<Entry> Remaining => Files.Where(f => !f.Deleted);
    public bool Resolved => Remaining.Count() == 1;
}

public sealed record FolderCandidate(Entry Directory, IReadOnlyList<Entry> Files, IReadOnlyList<string> MatchingDirectories);

public static class PathSafety
{
    public static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void Check(string path, string root)
    {
        if (!Within(path, root)) throw new IOException("The file is outside the selected folder.");
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Links, junctions, and cloud placeholders are skipped for safety.");
            if (string.Equals(Path.TrimEndingDirectorySeparator(current), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new IOException("Cannot establish a safe path to the selected folder.");
    }
}

public sealed class Scanner
{
    public Task<ScanResult> IndexAsync(string path, CancellationToken token) => Task.Run(() =>
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("The selected folder no longer exists.");
        // Check ancestors too: a selected path through a junction must not escape the visible tree.
        for (string? p = full; p != null; p = Path.GetDirectoryName(p))
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Choose a local folder without links, junctions, or cloud placeholders in its path.");
        var root = new Entry(full, ".", true);
        Walk(root, full, token);
        return new ScanResult(root);
    }, token);

    private static void Walk(Entry directory, string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                token.ThrowIfCancellationRequested();
                Entry child;
                try
                {
                    var attributes = File.GetAttributes(path);
                    child = new Entry(path, Path.GetRelativePath(root, path), attributes.HasFlag(FileAttributes.Directory));
                    directory.Children.Add(child);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        child.State = ScanState.Failed;
                        child.Error = "Skipped link, junction, or cloud placeholder. Download locally before scanning.";
                    }
                    else if (child.IsDirectory) Walk(child, root, token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    child = new Entry(path, Path.GetRelativePath(root, path), false) { State = ScanState.Failed, Error = ex.Message };
                    directory.Children.Add(child);
                }
            }
            directory.Children.Sort((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            directory.State = ScanState.Failed;
            directory.Error = ex.Message;
        }
    }

    public async Task HashAsync(ScanResult result, IProgress<Entry>? progress, CancellationToken token)
    {
        result.Groups.Clear();
        result.Folders.Clear();
        await Parallel.ForEachAsync(result.Files.Where(f => f.State != ScanState.Failed),
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token }, async (file, ct) =>
            {
                file.State = ScanState.Hashing;
                progress?.Report(file);
                try
                {
                    PathSafety.Check(file.Path, result.Root.Path);
                    await using var stream = OpenRead(file.Path);
                    file.Length = stream.Length;
                    file.Hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                    file.State = ScanState.Complete;
                }
                catch (OperationCanceledException) { file.State = ScanState.Pending; throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    file.State = ScanState.Failed;
                    file.Error = ex.Message;
                }
                finally { progress?.Report(file); }
            });
        result.Groups = result.Files.Where(f => f.State == ScanState.Complete && f.Hash != null)
            .GroupBy(f => f.Hash!).Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(g.Key, g.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(g => g.Files[0].Length * (g.Files.Count - 1)).ToList();
        result.Folders = FindFolders(result);
    }

    public static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public static List<FolderCandidate> FindFolders(ScanResult result)
    {
        var directories = result.Root.Descendants().Where(e => e.IsDirectory && e.State != ScanState.Failed).ToList();
        var valid = new List<(Entry Dir, List<Entry> Files, string Signature)>();
        foreach (var dir in directories)
        {
            var descendants = dir.Descendants().ToList();
            var files = descendants.Where(e => !e.IsDirectory && !e.Deleted).ToList();
            if (files.Count == 0 || descendants.Any(e => e.State == ScanState.Failed) || files.Any(f => f.Hash == null)) continue;
            // Every hash needs at least one keeper OUTSIDE the entire candidate subtree.
            if (!files.All(f => result.Groups.Any(g => g.Hash == f.Hash && g.Remaining.Any(k => !PathSafety.Within(k.Path, dir.Path))))) continue;
            // Include relative names and empty directories, not just a bag of hashes.
            var signature = string.Join("\n", descendants.Where(e => !e.Deleted).Select(e =>
                (e.IsDirectory ? "D:" : "F:") + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(dir.Path, e.Path).ToUpperInvariant())) + ":" + e.Hash)
                .Order(StringComparer.Ordinal));
            valid.Add((dir, files, signature));
        }
        return valid.Select(v => new FolderCandidate(v.Dir, v.Files, valid.Where(o => o.Dir != v.Dir && o.Signature == v.Signature).Select(o => o.Dir.RelativePath).ToList()))
            .OrderByDescending(c => c.Files.Count).ThenBy(c => c.Directory.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
