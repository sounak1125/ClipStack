using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using ClipStack.Core.Models;
using ClipStack.Core.Settings;
using ClipStack.Core.Storage;
using ClipStack.Core.Utilities;
using ClipStack.Services;

namespace ClipStack.ViewModels;

public sealed class ClipboardItemViewModel : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private bool _thumbnailLoadAttempted;
    private readonly FileLogger _logger;

    public ClipboardItemViewModel(ClipboardItem item, int shortcutNumber, FileLogger logger)
    {
        Item = item;
        ShortcutNumber = shortcutNumber;
        _logger = logger;
    }

    public ClipboardItem Item { get; private set; }

    public Guid Id => Item.Id;

    public int ShortcutNumber { get; private set; }

    public string ShortcutLabel => ShortcutNumber switch
    {
        10 => "0",
        > 10 => string.Empty,
        _ => ShortcutNumber.ToString(),
    };

    public ClipboardItemKind Kind => Item.DominantKind;

    public string KindLabel => Item.DominantKind switch
    {
        ClipboardItemKind.Text => "Text",
        ClipboardItemKind.RichText => "Rich text",
        ClipboardItemKind.Image => "Image",
        ClipboardItemKind.Files => "Files",
        _ => "Unknown",
    };

    public string PreviewText => Item.PreviewText;

    public string MetaText
    {
        get
        {
            return Item.DominantKind switch
            {
                ClipboardItemKind.Image => $"{Item.ImageWidth}×{Item.ImageHeight} · {FormatSize(Item.TotalSizeBytes)}",
                ClipboardItemKind.Files => $"{Item.FileCount} files",
                ClipboardItemKind.RichText or ClipboardItemKind.Text => $"{Item.CharacterCount:N0} chars",
                _ => FormatSize(Item.TotalSizeBytes),
            };
        }
    }

    public string TimeText
    {
        get
        {
            var local = Item.CapturedUtc.ToLocalTime();
            var age = DateTimeOffset.Now - Item.CapturedUtc;
            if (age < TimeSpan.FromMinutes(1)) return "Just now";
            if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
            if (age.TotalHours < 24 && local.Date == DateTime.Today) return local.ToString("t");
            return local.ToString("g");
        }
    }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public bool ShowThumbnail => Kind == ClipboardItemKind.Image;

    public void Update(ClipboardItem item, int shortcutNumber)
    {
        Item = item;
        ShortcutNumber = shortcutNumber;
        OnPropertyChanged(nameof(ShortcutNumber));
        OnPropertyChanged(nameof(ShortcutLabel));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(ShowThumbnail));
    }

    public void EnsureThumbnail(HistoryStore store)
    {
        if (_thumbnailLoadAttempted || Kind != ClipboardItemKind.Image)
            return;

        _thumbnailLoadAttempted = true;
        try
        {
            var path = store.ResolveThumbnailPath(Item);
            if (path is null) return;
            Thumbnail = ThumbnailService.LoadFrozenThumbnail(path);
        }
        catch (Exception ex)
        {
            _logger.Error("LoadThumbnail", ex, $"Item {Id:D}: {ex.Message}");
        }
    }

    public void ClearThumbnail()
    {
        Thumbnail = null;
        _thumbnailLoadAttempted = false;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class PopupViewModel : INotifyPropertyChanged
{
    private readonly HistoryStore _history;
    private readonly FileLogger _logger;
    private ClipboardItemViewModel? _selected;
    private bool _isPaused;
    private string _headerCount = "0";
    private string _searchText = string.Empty;
    private bool _isFilterVisible;

    // Last values supplied by Refresh, so a keystroke in the search box can rebuild the
    // list without the caller having to hand us settings again.
    private int _limit = AppSettings.DefaultHistoryLimit;

    public PopupViewModel(HistoryStore history, FileLogger logger)
    {
        _history = history;
        _logger = logger;
        Items = new ObservableCollection<ClipboardItemViewModel>();
    }

    public ObservableCollection<ClipboardItemViewModel> Items { get; }

    public bool HasItems => Items.Count > 0;

    public ClipboardItemViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            _selected = value;
            OnPropertyChanged();
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PausedText));
        }
    }

    public string PausedText => IsPaused ? "Paused" : string.Empty;

    /// <summary>Text in the popup's filter box. Setting it rebuilds the visible list.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_searchText, next, StringComparison.Ordinal))
                return;

            _searchText = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSearchText));
            Rebuild();
        }
    }

    public bool HasSearchText => _searchText.Length > 0;

    /// <summary>Whether the filter row is shown. Toggled by pressing "/" or Ctrl+F.</summary>
    public bool IsFilterVisible
    {
        get => _isFilterVisible;
        set
        {
            if (_isFilterVisible == value)
                return;

            _isFilterVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowFilterHint));
        }
    }

    public bool ShowFilterHint => !_isFilterVisible;

    public string HeaderCount
    {
        get => _headerCount;
        private set
        {
            if (string.Equals(_headerCount, value, StringComparison.Ordinal))
                return;

            _headerCount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Distinguishes "history is empty" from "the filter matched nothing".</summary>
    public string EmptyMessage => HasSearchText ? "No matches" : "No clips";

    public void Refresh(int limit, bool isPaused)
    {
        _limit = limit;
        IsPaused = isPaused;
        Rebuild();
    }

    private void Rebuild()
    {
        var all = _history.Items.Take(_limit).ToList();
        var terms = ClipboardSearch.ParseTerms(_searchText);
        var source = terms.Length == 0
            ? all
            : all.Where(i => ClipboardSearch.Matches(i, terms)).ToList();

        HeaderCount = terms.Length == 0
            ? source.Count.ToString()
            : $"{source.Count} / {all.Count}";

        // Incremental update by id order
        for (var i = 0; i < source.Count; i++)
        {
            var item = source[i];
            var number = i + 1;
            if (i < Items.Count && Items[i].Id == item.Id)
            {
                Items[i].Update(item, number);
            }
            else
            {
                var existingIndex = -1;
                for (var j = i; j < Items.Count; j++)
                {
                    if (Items[j].Id == item.Id)
                    {
                        existingIndex = j;
                        break;
                    }
                }

                if (existingIndex >= 0)
                {
                    var vm = Items[existingIndex];
                    Items.RemoveAt(existingIndex);
                    vm.Update(item, number);
                    Items.Insert(i, vm);
                }
                else
                {
                    Items.Insert(i, new ClipboardItemViewModel(item, number, _logger));
                }
            }
        }

        while (Items.Count > source.Count)
        {
            Items[^1].ClearThumbnail();
            Items.RemoveAt(Items.Count - 1);
        }

        // Always highlight the newest (top) row after refresh.
        SelectedItem = Items.FirstOrDefault();

        foreach (var vm in Items)
            vm.EnsureThumbnail(_history);

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>Always highlight the newest (top) history row.</summary>
    public void SelectMostRecent()
    {
        SelectedItem = Items.FirstOrDefault();
    }

    /// <summary>
    /// Drops the filter and hides the filter row so the next open starts clean.
    /// </summary>
    /// <remarks>
    /// Resets the backing fields without rebuilding: this runs while the popup is being
    /// hidden, and rebuilding there would re-read every thumbnail from disk for a list
    /// nobody is looking at. <see cref="Refresh"/> always runs before the popup is shown
    /// again, which is what repopulates the list.
    /// </remarks>
    public void ResetSearch()
    {
        if (_searchText.Length == 0 && !_isFilterVisible)
            return;

        _searchText = string.Empty;
        _isFilterVisible = false;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(IsFilterVisible));
        OnPropertyChanged(nameof(ShowFilterHint));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    public void ClearThumbnails()
    {
        foreach (var item in Items)
            item.ClearThumbnail();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
