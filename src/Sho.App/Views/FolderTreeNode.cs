using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sho.App.Views;

public sealed partial class FolderTreeNode : ObservableObject
{
    public string Name { get; }
    public string FullPath { get; }

    public ObservableCollection<FolderTreeNode> Children { get; } = new();

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isExpanded;

    private bool _loaded;

    public FolderTreeNode(string name, string fullPath, bool addPlaceholder = true)
    {
        Name = name;
        FullPath = fullPath;
        if (addPlaceholder)
            Children.Add(new FolderTreeNode("…", string.Empty, addPlaceholder: false));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded) Load();
    }

    // No automatic propagation: each ticked folder is an independent search root.
    // OkButton dedupes ancestor/descendant overlap, so the user can safely tick a
    // parent without wading through children.

    private void Load()
    {
        Children.Clear();
        if (string.IsNullOrEmpty(FullPath))
        {
            _loaded = true;
            return;
        }

        // "C:" without trailing backslash means CWD on C-drive, not the root.
        // Normalize to "C:\\" before enumerating.
        var enumPath = FullPath;
        if (enumPath.Length == 2 && enumPath[1] == ':')
            enumPath += Path.DirectorySeparatorChar;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(enumPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith('$') || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool hasSub;
                try { hasSub = Directory.EnumerateDirectories(dir).Any(); }
                catch { hasSub = false; }

                var child = new FolderTreeNode(name, dir, addPlaceholder: hasSub)
                {
                    IsChecked = IsChecked
                };
                Children.Add(child);
            }
        }
        catch { /* unauthorized / IO — leave empty */ }

        _loaded = true;
    }

    public IEnumerable<FolderTreeNode> AllChecked()
    {
        if (IsChecked) yield return this;
        foreach (var c in Children)
            foreach (var n in c.AllChecked()) yield return n;
    }
}
