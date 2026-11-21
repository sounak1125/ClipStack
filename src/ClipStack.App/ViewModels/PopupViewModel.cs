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
    private readonly HistoryStore _store;

    public ClipboardItemViewModel(ClipboardItem item, int shortcutNumber, HistoryStore store, FileLogger logger)
    {
        Item = item;
        ShortcutNumber = shortcutNumber;
        _store = store;
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

    public string ImageMetaText => Item.ImageWidth is int width && Item.ImageHeight is int height
        ? $"{width}×{height} · {FormatSize(Item.TotalSizeBytes)}"
        : FormatSize(Item.TotalSizeBytes);

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

    /// <summary>
    /// Decoded on first read rather than up front. The ListBox virtualizes, so the binding
    /// only touches this for rows that are actually realized — roughly four at a time
    /// instead of all fifty on every popup open.
    /// </summary>
    public BitmapSource? Thumbnail
    {
        get
        {
            EnsureThumbnail();
            return _thumbnail;
        }
    }

    public bool ShowThumbnail => Kind == ClipboardItemKind.Image;

    public bool IsPinned => Item.IsPinned;

    public void Update(ClipboardItem item, int shortcutNumber)
    {
        Item = item;
        ShortcutNumber = shortcutNumber;
        OnPropertyChanged(nameof(ShortcutLabel));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(ImageMetaText));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(ShowThumbnail));
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(IsPinned));
    }

    private void EnsureThumbnail()
    {
        if (_thumbnailLoadAttempted || Kind != ClipboardItemKind.Image)
            return;

        _thumbnailLoadAttempted = true;
        try
        {
            // Read through the store rather than opening the file: payloads are encrypted
            // at rest, and only the store knows how to unwrap them.
            var bytes = _store.ReadPayloadBytes(Item, ClipboardFormatKind.ThumbnailPng, maximumStoredBytes: 2 * 1024 * 1024);
            if (bytes is not { Length: > 0 }) return;

            _thumbnail = ThumbnailService.LoadFrozenFromBytes(bytes, pngOnly: true);
        }
        catch (Exception ex)
        {
            _logger.Error("LoadThumbnail", ex, $"Item {Id:D}: {ex.Message}");
        }
    }

    /// <summary>
    /// Drops the decoded bitmap when the popup hides. Deliberately silent — notifying here
    /// would make any still-realized row re-read Thumbnail and immediately decode it again,
    /// which is exactly the work this is trying to avoid. <see cref="Update"/> re-notifies
    /// when the popup is next populated.
    /// </summary>
    public void ClearThumbnail()
    {
        _thumbnail = null;
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
    private bool _autoPaste = true;

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
        }
    }

    public string PasteAction => _autoPaste ? "Paste" : "Copy";

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

    public void Refresh(int limit, bool isPaused, bool autoPaste)
    {
        _limit = limit;
        IsPaused = isPaused;
        if (_autoPaste != autoPaste)
        {
            _autoPaste = autoPaste;
            OnPropertyChanged(nameof(PasteAction));
        }
        Rebuild();
    }

    private void Rebuild()
    {
        // ApplyLimit rather than Take: the limit counts unpinned clips only, so the stored
        // list can legitimately be longer and taking the first N off the top would hide
        // the newest unpinned clips behind the pins.
        var all = ClipboardHistoryView.ApplyLimit(_history.Items, _limit);
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
                    Items.Insert(i, new ClipboardItemViewModel(item, number, _history, _logger));
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

        // No eager thumbnail pass here: each row decodes on first bind, so virtualization
        // limits the work to visible rows instead of the whole history.

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>Re-selects a specific clip after an operation that reorders the list.</summary>
    public void SelectById(Guid id)
    {
        var match = Items.FirstOrDefault(i => i.Id == id);
        if (match is not null)
            SelectedItem = match;
    }

    /// <summary>
    /// Drops the filter so the next open starts clean.
    /// </summary>
    /// <remarks>
    /// Resets the backing fields without rebuilding: this runs while the popup is being
    /// hidden, and rebuilding there would re-read every thumbnail from disk for a list
    /// nobody is looking at. <see cref="Refresh"/> always runs before the popup is shown
    /// again, which is what repopulates the list.
    /// </remarks>
    public void ResetSearch()
    {
        if (_searchText.Length == 0)
            return;

        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HasSearchText));
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
