using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DDS2ModManager.Views;

/// One row in the inspector tree. The reader's own types are shaped for parsing, not for display,
/// and filtering needs to prune branches - so the tree is projected into these rather than bound
/// to the parse output directly.
public class InspectorNode
{
    public string Header { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Value { get; init; } = "";
    public List<InspectorNode> Children { get; } = new();
}

public partial class SaveInspectorWindow : Window
{
    /// Beyond this many rows the tree stops being usable and starts being slow, so deep branches
    /// are truncated with a marker instead of being built in full.
    private const int MaxChildrenPerNode = 500;

    private readonly List<string> _files;
    private readonly RamaSaveReader _progressReader = new();
    private readonly GvasSaveReader _gvasReader = new();
    private SaveFileData? _data;

    public SaveInspectorWindow(SaveEntry save)
    {
        InitializeComponent();

        Title = $"Inspect Save - {save.Name}";
        _files = FindSaveFiles(save);

        if (_files.Count == 0)
        {
            SummaryText.Text = "No readable save data found in this save.";
            VerifyText.Text = "";
            FileCombo.IsEnabled = false;
            return;
        }

        FileCombo.ItemsSource = _files.Select(Path.GetFileName).ToList();
        FileCombo.SelectedIndex = PreferredFileIndex(_files);
    }

    /// Everything readable in the save: the RamaSave progress files that hold the game state, plus
    /// the plain GVAS companions (CartelDefaults.sav and the like). Both are understood, so both
    /// are offered - the progress file just gets picked first.
    private static List<string> FindSaveFiles(SaveEntry save)
    {
        try
        {
            var paths = save.IsFolder
                ? Directory.GetFiles(save.Path, "*.sav*", SearchOption.AllDirectories)
                : new[] { save.Path };

            return paths
                .Where(p => RamaSaveReader.IsProgressSave(p) || GvasSaveReader.IsGvasSave(p))
                .OrderBy(p => p)
                .ToList();
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't list save files for '{save.Name}': {ex.Message}");
            return new List<string>();
        }
    }

    /// Which file to show first: the one most likely to hold the actual playthrough.
    ///
    /// DDS2 names it "&lt;cartel&gt;_Progress.save", so that still wins outright. Falling back to the
    /// largest file rather than the first covers every other shape - DDS1's slots are called
    /// saveSlot-N.save and sit beside far smaller settings and index files, and opening on an
    /// alphabetically-first metadata file would look like the inspector could not read the save.
    private static int PreferredFileIndex(List<string> files)
    {
        var idx = files.FindIndex(f => f.EndsWith("_Progress.save", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) return idx;

        var largest = 0;
        long largestSize = -1;
        for (var i = 0; i < files.Count; i++)
        {
            long size;
            try { size = new FileInfo(files[i]).Length; } catch { continue; }
            if (size <= largestSize) continue;
            largestSize = size;
            largest = i;
        }
        return largest;
    }

    private void FileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = FileCombo.SelectedIndex;
        if (index < 0 || index >= _files.Count) return;

        var path = _files[index];
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            _data = RamaSaveReader.IsProgressSave(path)
                ? _progressReader.Read(path)
                : _gvasReader.Read(path);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }

        if (_data == null)
        {
            SummaryText.Text = "This file couldn't be read.";
            VerifyText.Text = "";
            Tree.ItemsSource = null;
            return;
        }

        var parts = new List<string> { _data.ParseSummary, $"Unreal {_data.EngineVersion}" };

        parts.Add(_data.CompressedBytes == _data.DecompressedBytes
            ? $"{_data.CompressedBytes / 1024.0:F0} KB"
            : $"{_data.CompressedBytes / 1024.0:F0} KB on disk, {_data.DecompressedBytes / 1024.0:F0} KB uncompressed");

        if (_data.Tags.Count > 0) parts.Add($"{_data.Tags.Count} content tags");
        SummaryText.Text = string.Join("  |  ", parts);

        // Describe the check that actually applies to this file rather than claiming one that
        // doesn't, and say plainly when something didn't line up instead of quietly showing
        // partial data as though it were complete.
        var isProgress = _data.Format == SaveFormat.RamaSaveProgress;
        if (_data.AllActorsParsed)
        {
            VerifyText.Text = isProgress
                ? "Every record was checked against the end offset the save itself declares, and all of them matched."
                : "The whole property list was read, ending exactly where the file does.";
            VerifyText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        }
        else
        {
            VerifyText.Text = isProgress
                ? $"{_data.Actors.Count - _data.FullyParsedActors} of {_data.Actors.Count} records didn't match the end offset " +
                  "the save declares for them, so parts of this file may be shown incompletely."
                : "The property list stopped before the end of the file, so some of it isn't shown.";
            VerifyText.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
        }

        BuildTree();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => BuildTree();

    private void Refilter_Click(object sender, RoutedEventArgs e) => BuildTree();

    private void BuildTree()
    {
        if (_data == null) return;

        var filter = FilterBox.Text.Trim();
        FilterHint.Visibility = filter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var showMetadata = ShowMetadata.IsChecked == true;
        var matched = new List<(SaveActorRecord Actor, InspectorNode Node)>();

        foreach (var actor in _data.Actors)
        {
            var source = showMetadata ? actor.Properties : actor.GameplayProperties.ToList();
            var actorMatches = filter.Length == 0 ||
                               actor.ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase);

            var children = new List<InspectorNode>();
            foreach (var p in source)
            {
                // An actor matched by name shows all of its fields; otherwise only the fields that
                // matched, so a filter never implies an actor has nothing else in it.
                var node = BuildNode(p, actorMatches ? "" : filter, 0);
                if (node != null) children.Add(node);
            }

            if (!actorMatches && children.Count == 0) continue;

            var node2 = new InspectorNode
            {
                Header = actor.ClassName,
                Detail = actor.FullyParsed
                    ? $"{children.Count} fields"
                    : $"{children.Count} fields - record did not verify"
            };
            node2.Children.AddRange(children);
            matched.Add((actor, node2));
        }

        Tree.ItemsSource = GroupByClass(matched);
        if (filter.Length > 0 && Tree.Items.Count <= 40) ExpandAll(true);
    }

    /// A save holds one record per persistent actor, so a world with forty quest boxes in it
    /// produces forty identical-looking top-level rows and the interesting actors get lost
    /// between them. Classes with more than one instance collapse into a single node, with the
    /// instances inside it.
    private static List<InspectorNode> GroupByClass(List<(SaveActorRecord Actor, InspectorNode Node)> matched)
    {
        var roots = new List<InspectorNode>();

        foreach (var group in matched.GroupBy(m => m.Actor.ClassName))
        {
            var items = group.ToList();
            if (items.Count == 1)
            {
                roots.Add(items[0].Node);
                continue;
            }

            var unverified = items.Count(i => !i.Actor.FullyParsed);
            var parent = new InspectorNode
            {
                Header = group.Key,
                Detail = unverified == 0
                    ? $"x{items.Count}"
                    : $"x{items.Count} - {unverified} did not verify"
            };

            for (var i = 0; i < items.Count; i++)
            {
                var child = items[i].Node;
                // The class name is already on the parent; number the instances instead so they
                // can be told apart.
                var instance = new InspectorNode { Header = $"[{i}]", Detail = child.Detail };
                instance.Children.AddRange(child.Children);
                parent.Children.Add(instance);
            }

            roots.Add(parent);
        }

        return roots;
    }

    /// Returns null when nothing in this subtree matches the filter.
    private static InspectorNode? BuildNode(SaveProperty p, string filter, int depth)
    {
        var self = filter.Length == 0 || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var children = new List<InspectorNode>();
        if (depth < 12)
        {
            foreach (var c in p.Children.Take(MaxChildrenPerNode))
            {
                var child = BuildNode(c, self ? "" : filter, depth + 1);
                if (child != null) children.Add(child);
            }

            if (p.Children.Count > MaxChildrenPerNode)
            {
                children.Add(new InspectorNode
                {
                    Header = $"... {p.Children.Count - MaxChildrenPerNode:N0} more not shown",
                    Detail = "use Export as Text to see all of them"
                });
            }
        }

        if (!self && children.Count == 0) return null;

        var node = new InspectorNode
        {
            Header = p.Name,
            Detail = p.Type,
            Value = p.HasChildren ? "" : p.ValueDisplay
        };
        node.Children.AddRange(children);
        return node;
    }

    private void ExpandActors_Click(object sender, RoutedEventArgs e) => ExpandAll(false);

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Tree.Items)
            if (Tree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
                tvi.IsExpanded = false;
    }

    private void ExpandAll(bool deep)
    {
        Tree.UpdateLayout();
        foreach (var item in Tree.Items)
        {
            if (Tree.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem tvi) continue;
            tvi.IsExpanded = true;
            if (deep) ExpandChildren(tvi, 0);
        }
    }

    private static void ExpandChildren(TreeViewItem parent, int depth)
    {
        if (depth > 3) return;
        parent.UpdateLayout();
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem tvi) continue;
            tvi.IsExpanded = true;
            ExpandChildren(tvi, depth + 1);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export save contents",
            Filter = "Text file (*.txt)|*.txt",
            FileName = Path.GetFileNameWithoutExtension(_data.Path) + "_contents.txt"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildExport(_data, ShowMetadata.IsChecked == true));
            LoggingService.Instance.Info($"Exported save contents to {dialog.FileName}");
            MessageBox.Show($"Written to:\n{dialog.FileName}", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Couldn't export save contents: {ex.Message}");
            MessageBox.Show($"Couldn't write that file:\n{ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// The export is deliberately complete - no truncation - because its whole point is to get at
    /// the parts the tree collapses away.
    private static string BuildExport(SaveFileData data, bool includeMetadata)
    {
        var sb = new StringBuilder();
        sb.AppendLine(data.Path);
        sb.AppendLine(data.ParseSummary);
        sb.AppendLine($"Unreal {data.EngineVersion} ({data.EngineBranch}), " +
                      $"{data.CompressedBytes:N0} bytes on disk, {data.DecompressedBytes:N0} uncompressed");
        sb.AppendLine($"{data.Tags.Count} content tags");
        sb.AppendLine();

        foreach (var actor in data.Actors)
        {
            sb.AppendLine($"{actor.ClassName}  [{actor.LevelName}]{(actor.FullyParsed ? "" : "  (record did not verify)")}");
            foreach (var p in includeMetadata ? actor.Properties : actor.GameplayProperties)
                WriteProperty(sb, p, "    ", 0);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void WriteProperty(StringBuilder sb, SaveProperty p, string indent, int depth)
    {
        if (depth > 16) return;
        sb.AppendLine($"{indent}{p.Name}  ({p.Type})  {(p.HasChildren ? "" : p.ValueDisplay)}");
        foreach (var c in p.Children) WriteProperty(sb, c, indent + "    ", depth + 1);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
