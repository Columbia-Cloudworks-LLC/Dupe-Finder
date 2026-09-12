using DupeFinder.Core;
using DupeFinder.App;

var root = Path.Combine(Path.GetTempPath(), "DupeFinderNativeTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var sink = new RecycleProgressSink();
if (sink.PreDeleteItem(0, IntPtr.Zero) >= 0 || sink.PreDeleteItem(0x80, IntPtr.Zero) != 0) throw new Exception("Recycle fallback guard failed");
Console.WriteLine("PASS Unsupported recycling aborts before deletion");
try
{
    foreach (var permanent in new[] { true, false })
    {
        var a = Path.Combine(root, permanent ? "permanent-copy.txt" : "recycled-copy.txt");
        var b = Path.Combine(root, "keeper.txt");
        await File.WriteAllTextAsync(a, "Dupe Finder native test fixture");
        await File.WriteAllTextAsync(b, "Dupe Finder native test fixture");
        var scanner = new Scanner(); var scan = await scanner.IndexAsync(root, default); await scanner.HashAsync(scan, null, default);
        var result = await new DeletionService(new WindowsDeletionBackend()).DeleteAsync(scan, [scan.Files.Single(f => f.Path == a)], permanent);
        if (result.Error != null || File.Exists(a) || !File.Exists(b)) throw new Exception(result.Error ?? "Wrong deletion outcome");
        Console.WriteLine("PASS " + (permanent ? "Permanent deletion by verified handle" : "Recycle Bin operation"));
    }
    return 0;
}
finally
{
    var full = Path.GetFullPath(root);
    if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("DupeFinderNativeTests-")) throw new Exception("Unsafe cleanup path");
    Directory.Delete(full, true);
}
