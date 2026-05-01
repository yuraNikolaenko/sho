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

                MarkInitiallySelected(node, initiallySelected);

                // Expand each drive's first level so the user immediately sees
                // top-level folders and can navigate without searching for the
                // expand chevron. Lazy load is still cheap — only one level deep.
                node.IsExpanded = true;
            }
            catch { }
        }
    }

    private void MarkInitiallySelected(FolderTreeNode node, HashSet<string> selected)
    {
        // We deliberately do NOT auto-expand the tree. The user opens the picker
        // and immediately sees ALL drives (C:\, D:\, …) at the top level.
        // Selected paths are surfaced via the chip in the dialog footer; user
        // navigates manually to the folder they want to add or remove.
        if (selected.Count == 0) return;
        if (selected.Contains(node.FullPath)) node.IsChecked = true;
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
