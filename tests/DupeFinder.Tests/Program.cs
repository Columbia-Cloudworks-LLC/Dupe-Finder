using DupeFinder.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Hashes content, not names; includes empty files", Hashes),
    ("Folders require an outside survivor for every hash", Folders),
    ("Exact trees are separated from scattered collections", TreeMatch),
    ("Last copy and unflagged files cannot be deleted", LastCopy),
    ("Changed target blocks whole batch", ChangedTarget),
    ("Changed or missing keeper blocks deletion", ChangedKeeper),
    ("Partial failures report only completed deletions", Partial),
    ("Keepers remain locked throughout deletion", LockedKeeper),
    ("New unscanned content is retained during folder cleanup", NewContent),
    ("Overlapping batches cannot remove final copy", Concurrent),
    ("Cancellation produces no duplicate groups", Cancellation),
    ("Corrupt and opted-out settings do not persist", Settings),
    ("Path boundary rejects sibling prefix", Boundary)
};
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + "\n" + ex); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
static async Task Hashes()
{
    using var f = new Fixture(); f.Write("a", "same"); f.Write("b", "same"); f.Write("c", "diff"); f.Write("e1", ""); f.Write("e2", "");
    var s = await f.Scan(); Check(s.Groups.Count == 2); Check(s.Groups.All(g => g.Files.Count == 2));
}
static async Task Folders()
{
    using var f = new Fixture(); f.Write("a/x", "1"); f.Write("a/y", "1");
    Check((await f.Scan()).Folders.Count == 0);
    f.Write("b/z", "1"); var scan = await f.Scan(); Check(scan.Folders.Any(c => c.Directory.Name == "a"));
    f.Write("a/unique", "2"); Check(!(await f.Scan()).Folders.Any(c => c.Directory.Name == "a"));
}
static async Task TreeMatch()
{
    using var f = new Fixture(); f.Write("a/x", "1"); f.Write("a/sub/y", "2"); f.Write("b/x", "1"); f.Write("b/sub/y", "2"); f.Write("c/z", "1"); f.Write("d/w", "2");
    var s = await f.Scan(); var a = s.Folders.Single(c => c.Directory.Name == "a"); Check(a.MatchingDirectories.SequenceEqual(new[] { "b" }));
    Check(s.Folders.Single(c => c.Directory.Name == "c").MatchingDirectories.Count == 0);
}
static async Task LastCopy()
{
    using var f = new Fixture(); f.Write("a", "1"); f.Write("b", "1"); f.Write("u", "2"); var s = await f.Scan(); var b = new Backend(); var d = new DeletionService(b);
    Check((await d.DeleteAsync(s, [s.Files.Single(x => x.Name == "u")], true)).Error != null);
    Check((await d.DeleteAsync(s, s.Groups[0].Files, true)).Error != null); Check(b.Calls == 0);
    Check((await d.DeleteAsync(s, [s.Groups[0].Files[0]], true)).Deleted.Count == 1);
    Check((await d.DeleteAsync(s, [s.Groups[0].Files[1]], true)).Error != null); Check(b.Calls == 1);
}
static async Task ChangedTarget()
{
    using var f = new Fixture(); f.Write("a", "1"); f.Write("b", "1"); f.Write("c", "1"); var s = await f.Scan(); f.Write("b", "2"); var b = new Backend();
    var r = await new DeletionService(b).DeleteAsync(s, s.Files.Where(x => x.Name != "c").ToList(), true); Check(r.Error != null); Check(b.Calls == 0);
}
static async Task ChangedKeeper()
{
    using var f = new Fixture(); f.Write("a", "1"); f.Write("b", "1"); var s = await f.Scan(); f.Write("b", "2"); var b = new Backend();
    var r = await new DeletionService(b).DeleteAsync(s, [s.Files.Single(x => x.Name == "a")], true); Check(r.Error != null && b.Calls == 0);
    File.Delete(Path.Combine(f.Root, "b")); r = await new DeletionService(b).DeleteAsync(s, [s.Files.Single(x => x.Name == "a")], true); Check(r.Error != null && b.Calls == 0);
}
static async Task Partial()
{
    using var f = new Fixture(); foreach (var n in new[] { "a", "b", "c" }) f.Write(n, "1"); var s = await f.Scan(); var b = new Backend { FailAt = 2 };
    var r = await new DeletionService(b).DeleteAsync(s, s.Files.Take(2).ToList(), true); Check(r.Deleted.Count == 1 && r.Error != null); Check(s.Groups[0].Remaining.Count() == 2);
}
static async Task LockedKeeper()
{
    using var f = new Fixture(); f.Write("a", "1"); f.Write("b", "1"); var s = await f.Scan(); var prevented = false;
    var b = new Backend { Before = () => { try { f.Write("b", "new"); } catch (IOException) { prevented = true; } } };
    var r = await new DeletionService(b).DeleteAsync(s, [s.Files.Single(x => x.Name == "a")], true); Check(r.Error == null && prevented);
}
static async Task NewContent()
{
    using var f = new Fixture(); f.Write("a/x", "1"); f.Write("b", "1"); var s = await f.Scan(); var folder = s.Folders.Single(); f.Write("a/new", "unscanned");
    var r = await new DeletionService(new Backend()).DeleteAsync(s, folder.Files, true); Check(r.Error == null);
    Check(DeletionService.RemoveEmptyDirectories(folder, s.Root.Path) != null); Check(File.Exists(Path.Combine(f.Root, "a/new")));
}
static async Task Concurrent()
{
    using var f = new Fixture(); f.Write("a", "1"); f.Write("b", "1"); var s = await f.Scan(); var d = new DeletionService(new Backend());
    var results = await Task.WhenAll(s.Files.Select(file => d.DeleteAsync(s, [file], true))); Check(results.Sum(r => r.Deleted.Count) == 1); Check(s.Groups[0].Remaining.Count() == 1);
}
static async Task Cancellation()
{
    using var f = new Fixture(); f.Write("a", "1"); f.Write("b", "1"); var scanner = new Scanner(); var s = await scanner.IndexAsync(f.Root, default);
    try { await scanner.HashAsync(s, null, new CancellationToken(true)); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
    Check(s.Groups.Count == 0);
}
static Task Settings()
{
    using var f = new Fixture(); var store = new SettingsStore(f.Root); store.Save(new("abc", true, true)); Check(store.Load().DeletePermanently);
    store.Save(new()); Check(!File.Exists(Path.Combine(f.Root, "settings.json")));
    f.Write("settings.json", "bad json"); Check(!store.Load().RememberSettings); return Task.CompletedTask;
}
static Task Boundary() { Check(!PathSafety.Within("C:\\data-other\\a", "C:\\data")); Check(PathSafety.Within("C:\\data\\a", "C:\\data")); return Task.CompletedTask; }

sealed class Backend : IDeletionBackend
{
    public int Calls;
    public int FailAt;
    public Action? Before;
    public Task DeleteVerifiedAsync(Entry f, string root, bool permanently, CancellationToken token)
    {
        Calls++; Before?.Invoke(); if (Calls == FailAt) throw new IOException("Simulated failure"); File.Delete(f.Path); return Task.CompletedTask;
    }
}
sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "DupeFinderTests-" + Guid.NewGuid().ToString("N"));
    public Fixture() => Directory.CreateDirectory(Root);
    public void Write(string path, string text) { var full = Path.Combine(Root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text); }
    public async Task<ScanResult> Scan() { var scanner = new Scanner(); var s = await scanner.IndexAsync(Root, default); await scanner.HashAsync(s, null, default); return s; }
    public void Dispose()
    {
        var full = Path.GetFullPath(Root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("DupeFinderTests-")) throw new InvalidOperationException();
        Directory.Delete(full, true);
    }
}
