using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DDS2ModManager.Views;

public partial class ModFilesWindow : Window
{
    private class TreeNode
    {
        public string Name { get; set; } = "";
        public SortedDictionary<string, TreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsFile { get; set; }
    }

    public ModFilesWindow(ModInfo mod)
    {
        InitializeComponent();

        TitleText.Text = mod.Name;
        SubtitleText.Text = $"{mod.Type} - {mod.ContainedAssetPaths.Count} file(s)";

        if (mod.ContainedAssetPaths.Count == 0)
        {
            FileTree.Items.Add(new TreeViewItem
            {
                Header = "No file list available for this mod (installed before this feature was added, " +
                         "or CUE4Parse couldn't read it). Reinstall the mod to populate this.",
                Foreground = (Brush)FindResource("TextMutedBrush")
            });
            return;
        }

        var root = new TreeNode { Name = "" };
        foreach (var path in mod.ContainedAssetPaths)
        {
            var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var isFile = i == parts.Length - 1;
                if (!current.Children.TryGetValue(part, out var child))
                {
                    child = new TreeNode { Name = part, IsFile = isFile };
                    current.Children[part] = child;
                }
                current = child;
            }
        }

        foreach (var child in root.Children.Values)
            FileTree.Items.Add(BuildTreeViewItem(child));
    }

    private TreeViewItem BuildTreeViewItem(TreeNode node)
    {
        var item = new TreeViewItem
        {
            Header = (node.IsFile ? "📄 " : "📁 ") + node.Name,
            IsExpanded = !node.IsFile && node.Children.Count <= 15
        };

        foreach (var child in node.Children.Values)
            item.Items.Add(BuildTreeViewItem(child));

        return item;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
