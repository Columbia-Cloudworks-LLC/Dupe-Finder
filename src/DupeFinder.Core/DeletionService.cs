using System.Security.Cryptography;

namespace DupeFinder.Core;

public interface IDeletionBackend
{
    // Must revalidate and remove exactly this file, without recursive directory deletion.
    Task DeleteVerifiedAsync(Entry file, string root, bool permanently, CancellationToken token);
}

public sealed record DeletionResult(IReadOnlyList<Entry> Deleted, string? Error);

public sealed class DeletionService(IDeletionBackend backend)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<DeletionResult> DeleteAsync(ScanResult scan, IReadOnlyList<Entry> requested, bool permanently, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        var deleted = new List<Entry>();
        var keepers = new List<FileStream>();
        try
        {
            if (requested.Count == 0) throw new IOException("No files were selected.");
            var targets = requested.Distinct().ToList();
            if (targets.Any(t => !scan.Files.Contains(t) || t.Deleted || t.State != ScanState.Complete || t.Hash == null))
                throw new IOException("The selection contains a file that was not verified as a duplicate. Scan again.");
            var targetPaths = targets.Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Pin a verified survivor for EVERY hash for the duration of the entire operation.
            // FileShare.Read prevents changes and removal of the last copy, including by another app.
            foreach (var hash in targets.Select(t => t.Hash!).Distinct())
            {
                var group = scan.Groups.SingleOrDefault(g => g.Hash == hash) ?? throw new IOException("File was not flagged as a duplicate.");
                FileStream? keeper = null;
                foreach (var candidate in group.Remaining.Where(f => !targetPaths.Contains(f.Path)))
                {
                    try
                    {
                        PathSafety.Check(candidate.Path, scan.Root.Path);
                        var stream = Scanner.OpenRead(candidate.Path);
                        try
                        {
                            if (Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) == hash) { keeper = stream; break; }
                        }
                        catch { stream.Dispose(); throw; }
                        stream.Dispose();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                if (keeper == null) throw new IOException("No unchanged surviving copy could be locked. Nothing else will be deleted; scan again.");
                keepers.Add(keeper);
            }
            // Complete preflight before deleting the first file in a collection.
            foreach (var file in targets)
            {
                PathSafety.Check(file.Path, scan.Root.Path);
                await using var stream = Scanner.OpenRead(file.Path);
                if (Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != file.Hash)
                    throw new IOException($"'{file.RelativePath}' changed since scanning. Scan again.");
            }
            foreach (var file in targets)
            {
                token.ThrowIfCancellationRequested();
                await backend.DeleteVerifiedAsync(file, scan.Root.Path, permanently, token);
                file.State = ScanState.Deleted;
                deleted.Add(file);
            }
            return new(deleted, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new(deleted, ex is OperationCanceledException ? "Deletion canceled. Completed deletions are shown." : ex.Message);
        }
        finally
        {
            foreach (var keeper in keepers) keeper.Dispose();
            gate.Release();
        }
    }

    public static string? RemoveEmptyDirectories(FolderCandidate candidate, string root)
    {
        // Only directories from the scanned manifest. New content makes Delete(false) fail safely.
        var dirs = candidate.Directory.Descendants().Where(e => e.IsDirectory).Append(candidate.Directory)
            .OrderByDescending(e => e.Path.Length);
        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir.Path)) continue;
                PathSafety.Check(dir.Path, root);
                Directory.Delete(dir.Path, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "Verified files were removed, but a folder was retained: " + ex.Message;
            }
        }
        return null;
    }
}

public sealed record AppSettings(string? LastFolder = null, bool DeletePermanently = false, bool RememberSettings = false);

public sealed class SettingsStore(string? directory = null)
{
    private readonly string folder = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Columbia Cloudworks", "Dupe Finder");
    private string FilePath => Path.Combine(folder, "settings.json");
    public AppSettings Load()
    {
        try
        {
            var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            return settings is { RememberSettings: true } ? settings : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return new(); }
    }
    public void Save(AppSettings settings)
    {
        if (!settings.RememberSettings) { if (File.Exists(FilePath)) File.Delete(FilePath); return; }
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, "settings." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(settings));
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
