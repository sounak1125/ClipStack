using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using ClipStack.Core.Models;
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

    public string ShortcutLabel => ShortcutNumber >= 10 ? "0" : ShortcutNumber.ToString();

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
    private string _headerCount = "0 items";

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

    public string HeaderCount
    {
        get => _headerCount;
        private set
        {
            _headerCount = value;
            OnPropertyChanged();
        }
    }

    public void Refresh(int limit, bool isPaused)
    {
        IsPaused = isPaused;
        var source = _history.Items.Take(limit).ToList();
        HeaderCount = source.Count.ToString();

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

        if (SelectedItem is null || Items.All(i => i.Id != SelectedItem.Id))
            SelectedItem = Items.FirstOrDefault();
        else
            SelectedItem = Items.First(i => i.Id == SelectedItem.Id);

        foreach (var vm in Items)
            vm.EnsureThumbnail(_history);

        OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>Always highlight the newest (top) history row.</summary>
    public void SelectMostRecent()
    {
        SelectedItem = Items.FirstOrDefault();
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
