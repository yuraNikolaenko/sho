using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Sho.App.ViewModels;

/// <summary>
/// <see cref="ObservableCollection{T}"/> with a single-notification bulk replace.
/// Adding items one at a time fires CollectionChanged per insert, which a bound
/// DataGrid recomputes layout for — fine for a handful of rows, painful for the
/// 5000+ rows a search can produce. <see cref="ReplaceAll"/> swaps the contents
/// and emits a single <see cref="NotifyCollectionChangedAction.Reset"/> so the
/// UI repopulates once, keeping the dispatcher responsive (no "Not Responding"
/// title-bar flicker while results land).
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
