using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using Wpf.Ui.Controls;

namespace Sho.App.Views;

public partial class FolderPickerWindow : FluentWindow
{
    public ObservableCollection<FolderTreeNode> Roots { get; } = new();
    public List<string> SelectedPaths { get; private set; } = new();

    public FolderPickerWindow(IEnumerable<string> initiallySelected)
    {
        InitializeComponent();
        Icon = Sho.App.Services.AppIconFactory.Create();
        BuildRoots(new HashSet<string>(initiallySelected ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase));
    }

    private void BuildRoots(HashSet<string> initiallySelected)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;

                // CRITICAL: keep the trailing backslash. On Windows,
                // Directory.EnumerateDirectories("C:") (without "\\") means the
                // *current directory* on the C: drive, not the root, so the
                // tree would show CWD's children (e.g., the app's own folder)
                // instead of the actual drive contents.
                var path = drive.RootDirectory.FullName; // "C:\"
                var display = path.TrimEnd('\\');         // "C:" — for label only
                var label = string.IsNullOrEmpty(drive.VolumeLabel)
                    ? display
                    : $"{display}  ({drive.VolumeLabel})";

                var node = new FolderTreeNode(label, path);
                Roots.Add(node);

                if (initiallySelected.Count > 0)
                    ExpandToSelected(node, initiallySelected);
            }
            catch { }
        }
    }

    /// <summary>
    /// Walks the tree, expanding only the chain of nodes that contain a
    /// previously-selected path. Branches without selected descendants stay
    /// collapsed. If <paramref name="selected"/> is empty the whole tree
    /// stays collapsed (caller should not invoke this).
    /// </summary>
    private static void ExpandToSelected(FolderTreeNode node, HashSet<string> selected)
    {
        var prefix = node.FullPath.TrimEnd('\\') + "\\";
        bool hasSelectedDescendant = selected.Any(s =>
            s.Equals(node.FullPath, StringComparison.OrdinalIgnoreCase)
            || s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (!hasSelectedDescendant) return;

        // Mark FIRST so the IsChecked event fires before children load,
        // skipping the propagation-to-descendants path inside the node.
        if (selected.Contains(node.FullPath)) node.IsChecked = true;

        node.IsExpanded = true; // triggers Load via OnIsExpandedChanged

        foreach (var child in node.Children)
            ExpandToSelected(child, selected);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var allChecked = Roots
            .SelectMany(r => r.AllChecked())
            .Select(n => n.FullPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Dedupe descendants: keep only highest-level checked nodes
        allChecked.Sort((a, b) => a.Length - b.Length);
        var deduped = new List<string>();
        foreach (var p in allChecked)
        {
            bool covered = deduped.Any(d =>
                p.Length > d.Length
                && p.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (!covered) deduped.Add(p);
        }

        SelectedPaths = deduped;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        ClearChecks(Roots);
    }

    private static void ClearChecks(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            n.IsChecked = false;
            ClearChecks(n.Children);
        }
    }
}
