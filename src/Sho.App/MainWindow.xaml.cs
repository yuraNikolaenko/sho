using System.Windows;
using System.Windows.Controls;
using Sho.App.ViewModels;
using Sho.Core.Models;

namespace Sho.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void FilesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileSummary fs) Vm.OpenFile(fs.FilePath);
    }

    private void LinesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LinesGrid.SelectedItem is SearchHit hit) Vm.OpenFile(hit.FilePath);
    }

    private void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileSummary fs) Vm.OpenFile(fs.FilePath);
    }

    private void MenuReveal_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileSummary fs) Vm.RevealInExplorer(fs.FilePath);
    }

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileSummary fs)
        {
            try { Clipboard.SetText(fs.FilePath); } catch { }
        }
    }

    private void MenuLineOpen_Click(object sender, RoutedEventArgs e)
    {
        if (LinesGrid.SelectedItem is SearchHit hit) Vm.OpenFile(hit.FilePath);
    }

    private void MenuLineReveal_Click(object sender, RoutedEventArgs e)
    {
        if (LinesGrid.SelectedItem is SearchHit hit) Vm.RevealInExplorer(hit.FilePath);
    }
}
