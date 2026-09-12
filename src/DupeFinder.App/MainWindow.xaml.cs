using DupeFinder.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.Storage.Pickers;

namespace DupeFinder.App;

public sealed partial class MainWindow : Window
{
    private readonly Scanner scanner = new();
    private readonly SettingsStore settings = new(Environment.GetCommandLineArgs().Contains("--self-test") ? Path.Combine(Path.GetTempPath(), "DupeFinderUiSettings-" + Guid.NewGuid().ToString("N")) : null);
    private readonly DeletionService deletion = new(new WindowsDeletionBackend());
    private readonly Dictionary<Entry, StackPanel> treeRows = [];
    private readonly Dictionary<TreeViewNode, Entry> treeEntries = [];
    private readonly Dictionary<Entry, (Grid Row, TextBlock Text, Border Strike)> fileRows = [];
    private readonly List<(Button Button, DuplicateGroup Group)> deleteButtons = [];
    private readonly List<(TextBlock Label, DuplicateGroup Group)> groupLabels = [];
    private readonly StackPanel folderCards = new() { Spacing = 10 };
    private ScanResult? scan;
    private CancellationTokenSource? cancellation;
    private bool busy;
    private bool closing;
    private bool deleting;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 800));
        var saved = settings.Load();
        FolderPath.Text = saved.LastFolder ?? "";
        Permanent.IsOn = saved.DeletePermanently;
        Remember.IsOn = saved.RememberSettings;
        AppWindow.Closing += (_, args) => { if (deleting) { args.Cancel = true; Status.Text = "Please wait for the current file operation to finish."; } };
        if (Environment.GetCommandLineArgs().Contains("--self-test")) Root.Loaded += async (_, _) => await RunSelfTestAsync();
        Closed += (_, _) => { closing = true; cancellation?.Cancel(); };
    }

    // End-to-end release check: exercises the real view handlers and animation on private fixtures only.
    private async Task RunSelfTestAsync()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "DupeFinderUiTest-" + Guid.NewGuid().ToString("N"));
        var report = Path.Combine(AppContext.BaseDirectory, "self-test-result.txt");
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture, "Originals"));
            Directory.CreateDirectory(Path.Combine(fixture, "Backup"));
            await File.WriteAllTextAsync(Path.Combine(fixture, "Originals", "one.txt"), "UI integration fixture A");
            await File.WriteAllTextAsync(Path.Combine(fixture, "Backup", "one.txt"), "UI integration fixture A");
            await File.WriteAllTextAsync(Path.Combine(fixture, "Originals", "two.txt"), "UI integration fixture B");
            await File.WriteAllTextAsync(Path.Combine(fixture, "Backup", "two.txt"), "UI integration fixture B");
            FolderPath.Text = fixture; Permanent.IsOn = false; Remember.IsOn = false;
            await OpenFolderAsync();
            if (scan?.Files.Count != 4 || Results.Children.Count != 0) throw new Exception("Folder view or empty results failed.");
            await ScanAsync();
            if (scan.Groups.Count != 2 || scan.Folders.Count != 2 || fileRows.Count != 4 || Notice.IsOpen) throw new Exception("Duplicate cards or folder collections failed.");
            var first = scan.Groups[0].Files[0];
            await DeleteFilesAsync([first], null);
            if (!first.Deleted || fileRows.Count != 3 || !scan.Groups[0].Resolved || Notice.IsOpen) throw new Exception("Deletion animation or resolved card failed. " + Notice.Message);
            if (deleteButtons.Any(b => b.Group == scan.Groups[0] && b.Button.Visibility != Visibility.Collapsed)) throw new Exception("Last-copy button remained visible.");
            await File.WriteAllTextAsync(report, "PASS: portable startup; folder view; scan; duplicate cards; matching trees; native recycling; row animation; final-copy protection; resolved card.");
        }
        catch (Exception ex) { await File.WriteAllTextAsync(report, "FAIL: " + ex); }
        finally
        {
            // This directory is created above, never taken from a user argument.
            if (Path.GetFileName(fixture).StartsWith("DupeFinderUiTest-", StringComparison.Ordinal) && PathSafety.Within(fixture, Path.GetTempPath()))
                Directory.Delete(fixture, true);
            Close();
        }
    }
    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(AppWindow.Id);
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) FolderPath.Text = folder.Path;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenFolderAsync();
    private async Task OpenFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderPath.Text)) { ShowError("Choose a folder first."); return; }
        SetBusy(true, "Reading folders…");
        OpenButton.IsEnabled = false;
        try
        {
            settings.Save(new(FolderPath.Text, Permanent.IsOn, Remember.IsOn));
            cancellation = new();
            scan = await scanner.IndexAsync(FolderPath.Text, cancellation.Token);
            Setup.Visibility = Visibility.Collapsed;
            Workspace.Visibility = Visibility.Visible;
            ChangeFolder.Visibility = Visibility.Visible;
            Heading.Text = "Make room for what matters.";
            Subtitle.Text = scan.Root.Path;
            ModeLabel.Text = Permanent.IsOn ? "PERMANENT DELETION" : "RECYCLE BIN";
            Results.Children.Clear();
            ResultCount.Text = "";
            BuildTree();
            Status.Text = $"{scan.Files.Count:N0} files · ready to scan. Links and cloud placeholders are skipped.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { ShowError(ex.Message); }
        finally { OpenButton.IsEnabled = true; SetBusy(false); }
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        Workspace.Visibility = Visibility.Collapsed;
        Setup.Visibility = Visibility.Visible;
        ChangeFolder.Visibility = Visibility.Collapsed;
        Heading.Text = "A little less clutter.";
        Subtitle.Text = "Find identical files. Keep what matters.";
        ModeLabel.Text = "COLUMBIA CLOUDWORKS";
        Notice.IsOpen = false;
    }

    private void BuildTree()
    {
        FileTree.RootNodes.Clear(); treeRows.Clear(); treeEntries.Clear();
        if (scan == null) return;
        var root = Node(scan.Root);
        Populate(root);
        root.IsExpanded = true;
        FileTree.RootNodes.Add(root);
    }

    private TreeViewNode Node(Entry entry)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        treeRows[entry] = row;
        UpdateRow(entry);
        var node = new TreeViewNode { Content = row, HasUnrealizedChildren = entry.Children.Count > 0 };
        treeEntries[node] = entry;
        return node;
    }
    private void Populate(TreeViewNode node)
    {
        if (!node.HasUnrealizedChildren) return;
        foreach (var child in treeEntries[node].Children) node.Children.Add(Node(child));
        node.HasUnrealizedChildren = false;
    }
    private void Tree_Expanding(TreeView sender, TreeViewExpandingEventArgs args) => Populate(args.Node);

    private void UpdateRow(Entry entry)
    {
        if (!treeRows.TryGetValue(entry, out var row)) return;
        row.Children.Clear();
        if (entry.State == ScanState.Hashing) row.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14 });
        else
        {
            var glyph = entry.State switch { ScanState.Failed => "\uEA39", ScanState.Complete => "\uEA3A", ScanState.Deleted => "\uE73E", _ => entry.IsDirectory ? "\uE8B7" : "\uE8A5" };
            var icon = new FontIcon { Glyph = glyph, FontSize = 14 };
            if (entry.State == ScanState.Failed) icon.Foreground = Brush(Colors.IndianRed);
            if (entry.State is ScanState.Complete or ScanState.Deleted) icon.Foreground = Brush(Colors.SeaGreen);
            ToolTipService.SetToolTip(icon, entry.Error ?? entry.State.ToString());
            AutomationProperties.SetName(icon, entry.Error ?? entry.State.ToString());
            row.Children.Add(icon);
        }
        row.Children.Add(new TextBlock { Text = entry.Name, Opacity = entry.Deleted ? 0.45 : 1, VerticalAlignment = VerticalAlignment.Center });
        ToolTipService.SetToolTip(row, entry.Error ?? entry.RelativePath);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();
    private async Task ScanAsync()
    {
        if (busy || scan == null) return;
        SetBusy(true, "Indexing current files…");
        Notice.IsOpen = false;
        Results.Children.Clear(); fileRows.Clear(); deleteButtons.Clear(); groupLabels.Clear();
        ResultCount.Text = "";
        CancelButton.Visibility = Visibility.Visible;
        cancellation = new();
        try
        {
            scan = await scanner.IndexAsync(scan.Root.Path, cancellation.Token);
            BuildTree();
            var completed = 0;
            var progress = new Progress<Entry>(entry =>
            {
                if (closing) return;
                UpdateRow(entry);
                if (entry.State is ScanState.Complete or ScanState.Failed) completed++;
                Status.Text = $"Hashing files · {completed:N0} / {scan.Files.Count:N0}";
            });
            await scanner.HashAsync(scan, progress, cancellation.Token);
            RenderResults();
            var failures = scan.Root.Descendants().Append(scan.Root).Count(f => f.State == ScanState.Failed);
            Status.Text = $"Scan complete · {scan.Files.Count:N0} files · {failures:N0} skipped or failed";
            if (failures > 0) ShowError("Some items could not be scanned. Expand the file tree and hover over red indicators for details.", InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) { Status.Text = "Scan canceled. Scan again to enable deletion."; }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { CancelButton.Visibility = Visibility.Collapsed; SetBusy(false); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();

    private void RenderResults()
    {
        Results.Children.Clear(); fileRows.Clear(); deleteButtons.Clear(); groupLabels.Clear();
        if (scan == null) return;
        ResultCount.Text = $"{scan.Groups.Count:N0} groups";
        if (scan.Groups.Count == 0)
        {
            Results.Children.Add(new TextBlock { Text = "No duplicate files found.", FontSize = 20, Margin = new Thickness(0, 32, 0, 0) });
            return;
        }
        RenderFolders();
        Results.Children.Add(folderCards);
        foreach (var group in scan.Groups)
        {
            var stack = new StackPanel { Spacing = 12 };
            var label = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            groupLabels.Add((label, group));
            stack.Children.Add(label);
            var hashLabel = new TextBlock { Text = "SHA-256 · " + group.Hash[..16] + "…", FontSize = 11, Opacity = 0.55 };
            ToolTipService.SetToolTip(hashLabel, group.Hash);
            stack.Children.Add(hashLabel);
            foreach (var file in group.Remaining)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var path = new Grid { VerticalAlignment = VerticalAlignment.Center };
                var text = new TextBlock { Text = file.RelativePath, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
                ToolTipService.SetToolTip(text, file.Path);
                var strike = new Border { Height = 2, Background = Brush(Colors.IndianRed), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new ScaleTransform { ScaleX = 0 } };
                path.Children.Add(text); path.Children.Add(strike); row.Children.Add(path);
                var button = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 }, Foreground = Brush(Colors.IndianRed), VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(button, (Permanent.IsOn ? "Permanently delete " : "Recycle ") + file.RelativePath);
                AutomationProperties.SetName(button, "Delete " + file.RelativePath);
                button.Click += async (_, _) => await DeleteFilesAsync([file], null);
                Grid.SetColumn(button, 1); row.Children.Add(button);
                deleteButtons.Add((button, group));
                stack.Children.Add(row);
                fileRows[file] = (row, text, strike);
            }
            Results.Children.Add(Card(stack));
        }
        RefreshActions();
    }

    private void RenderFolders()
    {
        folderCards.Children.Clear();
        if (scan == null) return;
        scan.Folders = Scanner.FindFolders(scan);
        if (scan.Folders.Count == 0) return;
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "Each collection has verified copies outside its folder. Review the exact files before deleting.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        foreach (var folder in scan.Folders)
        {
            var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            var title = folder.MatchingDirectories.Count > 0 ? "Matching tree" : "Copies in other folders";
            button.Content = new TextBlock { Text = $"{folder.Directory.RelativePath}\n{folder.Files.Count} files · {title}", TextWrapping = TextWrapping.Wrap };
            button.Click += async (_, _) => await DeleteFilesAsync(folder.Files, folder);
            content.Children.Add(button);
        }
        folderCards.Children.Add(new Expander { Header = $"Folder collections · {scan.Folders.Count}", Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
    }

    private async Task DeleteFilesAsync(IReadOnlyList<Entry> files, FolderCandidate? folder)
    {
        if (busy || scan == null) return;
        deleting = true;
        SetBusy(true);
        try
        {
            if (folder != null || Permanent.IsOn)
            {
                var preview = new StackPanel { Spacing = 10 };
                preview.Children.Add(new TextBlock { Text = Permanent.IsOn ? "This cannot be undone. A verified copy of every file must remain." : "Move these files to the Recycle Bin. Only verified files are removed; folders are removed only when empty.", TextWrapping = TextWrapping.Wrap });
                if (folder?.MatchingDirectories.Count > 0) preview.Children.Add(new TextBlock { Text = "Matching trees: " + string.Join(", ", folder.MatchingDirectories), TextWrapping = TextWrapping.Wrap });
                var paths = new StackPanel { Spacing = 8 };
                foreach (var file in files)
                {
                    var keeper = scan.Groups.First(g => g.Hash == file.Hash).Remaining.FirstOrDefault(k => !files.Contains(k));
                    paths.Children.Add(new TextBlock { Text = file.RelativePath + "\nKeep: " + (keeper?.RelativePath ?? "No copy available — action will be blocked"), TextWrapping = TextWrapping.Wrap });
                }
                preview.Children.Add(new ScrollViewer { Content = paths, MaxHeight = 320 });
                var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"{(Permanent.IsOn ? "Permanently delete" : "Recycle")} {files.Count} file{(files.Count == 1 ? "" : "s")}?", Content = preview, PrimaryButtonText = Permanent.IsOn ? "Delete permanently" : "Move to Recycle Bin", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            Status.Text = "Verifying files and protecting surviving copies…";
            var permanently = Permanent.IsOn;
            var result = await Task.Run(() => deletion.DeleteAsync(scan, files, permanently));
            // Disable last-copy buttons immediately, before animation starts.
            RefreshActions();
            foreach (var file in result.Deleted) UpdateRow(file);
            await Task.WhenAll(result.Deleted.Select(AnimateRemovalAsync));
            string? cleanup = null;
            if (folder != null && result.Error == null) cleanup = await Task.Run(() => DeletionService.RemoveEmptyDirectories(folder, scan.Root.Path));
            RenderFolders();
            RefreshActions();
            Status.Text = $"{result.Deleted.Count} file(s) {(permanently ? "permanently deleted" : "moved to the Recycle Bin")}.";
            if (result.Error != null || cleanup != null) ShowError(result.Error ?? cleanup!);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { deleting = false; SetBusy(false); }
    }

    private async Task AnimateRemovalAsync(Entry file)
    {
        if (!fileRows.TryGetValue(file, out var controls)) return;
        await Task.WhenAll(AnimateColor(controls.Text), Animate(controls.Strike.RenderTransform, "ScaleX", 1, 250));
        await Task.Delay(1000);
        await Animate(controls.Row, "Opacity", 0, 300);
        var height = controls.Row.ActualHeight;
        controls.Row.Height = height;
        await Animate(controls.Row, "Height", 0, 180, dependent: true);
        if (controls.Row.Parent is Panel parent) parent.Children.Remove(controls.Row);
        fileRows.Remove(file);
    }
    private static Task AnimateColor(TextBlock text)
    {
        var brush = new SolidColorBrush((text.Foreground as SolidColorBrush)?.Color ?? Colors.Gray);
        text.Foreground = brush;
        var completion = new TaskCompletionSource();
        var animation = new ColorAnimation { To = Colors.IndianRed, Duration = new Duration(TimeSpan.FromMilliseconds(250)), EnableDependentAnimation = true };
        Storyboard.SetTarget(animation, brush); Storyboard.SetTargetProperty(animation, "Color");
        var storyboard = new Storyboard(); storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => completion.TrySetResult(); storyboard.Begin();
        return completion.Task;
    }
    private static Task Animate(DependencyObject target, string property, double to, int milliseconds, bool dependent = false)
    {
        var completion = new TaskCompletionSource();
        var animation = new DoubleAnimation { To = to, Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)), EnableDependentAnimation = dependent };
        Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, property);
        var storyboard = new Storyboard(); storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => completion.TrySetResult(); storyboard.Begin();
        return completion.Task;
    }
    private void RefreshActions()
    {
        foreach (var (button, group) in deleteButtons)
        {
            button.Visibility = group.Resolved ? Visibility.Collapsed : Visibility.Visible;
            button.IsEnabled = !busy && !group.Resolved;
        }
        foreach (var (label, group) in groupLabels)
        {
            label.Text = group.Resolved ? "✓ Done · one copy kept" : $"{group.Remaining.Count()} identical files · {FormatSize(group.Files[0].Length)} each";
            label.Foreground = group.Resolved ? Brush(Colors.SeaGreen) : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        }
        folderCards.IsHitTestVisible = !busy;
        foreach (var expander in folderCards.Children.OfType<Expander>()) expander.IsEnabled = !busy;
    }
    private void SetBusy(bool value, string? status = null)
    {
        busy = value;
        Busy.IsActive = value; Busy.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = !value; ChangeFolder.IsEnabled = !value; Setup.IsHitTestVisible = !value;
        Permanent.IsEnabled = !value; Remember.IsEnabled = !value;
        RefreshActions();
        if (status != null) Status.Text = status;
    }
    private void ShowError(string message, InfoBarSeverity severity = InfoBarSeverity.Error) { Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true; }
    private static SolidColorBrush Brush(Windows.UI.Color color) => new(color);
    private static Border Card(UIElement content) => new() { Child = content, Padding = new Thickness(18), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] };
    private static string FormatSize(long value) => value >= 1024 * 1024 ? $"{value / (1024d * 1024):N1} MB" : value >= 1024 ? $"{value / 1024d:N1} KB" : $"{value} B";
}
