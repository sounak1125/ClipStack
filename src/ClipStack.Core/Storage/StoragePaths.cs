using System.Text;
using System.Text.Json;
using ClipStack.Core.Models;
using ClipStack.Core.Utilities;

namespace ClipStack.Core.Storage;

public sealed class StoragePaths
{
    public StoragePaths(string rootDirectory)
    {
        Root = Path.GetFullPath(rootDirectory);
        Items = Path.Combine(Root, "items");
        Logs = Path.Combine(Root, "logs");
        IndexFile = Path.Combine(Root, "index.json");
        SettingsFile = Path.Combine(Root, "settings.json");
        ReleaseConfigFile = Path.Combine(Root, "release-config.json");
    }

    public string Root { get; }
    public string Items { get; }
    public string Logs { get; }
    public string IndexFile { get; }
    public string SettingsFile { get; }
    public string ReleaseConfigFile { get; }

    public string GetItemDirectory(Guid id) => Path.Combine(Items, id.ToString("D"));

    public string GetTempItemDirectory(Guid id) => Path.Combine(Items, $".tmp-{id:D}");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Items);
        Directory.CreateDirectory(Logs);
    }
}

public static class PayloadFileNames
{
    public const string Text = "text.txt";
    public const string Html = "content.html";
    public const string Rtf = "content.rtf";
    public const string Image = "image.png";
    public const string ImageOriginalPrefix = "original";
    public const string Thumbnail = "thumbnail.png";
    public const string Files = "files.json";

    /// <summary>
    /// Sidecar holding what the payload files cannot express. Reserved: no payload may
    /// claim this name, or a capture would overwrite its own recovery record.
    /// </summary>
    public const string Metadata = "meta.json";

    public static string ForFormat(ClipboardFormatKind format) => format switch
    {
        ClipboardFormatKind.UnicodeText or ClipboardFormatKind.Text => Text,
        ClipboardFormatKind.Html => Html,
        ClipboardFormatKind.Rtf => Rtf,
        ClipboardFormatKind.ImagePng => Image,
        ClipboardFormatKind.ImageOriginal => "original.bin", // overridden by RelativeFileName when writing
        ClipboardFormatKind.ThumbnailPng => Thumbnail,
        ClipboardFormatKind.FileDropList => Files,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static bool IsOriginalImageFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        return fileName.StartsWith(ImageOriginalPrefix + ".", StringComparison.OrdinalIgnoreCase)
               && ImageFileDetector.IsImageFilePath(fileName);
    }
}

public sealed class NewClipboardItemData
{
    public required ClipboardItemKind DominantKind { get; init; }
    public required string ContentHash { get; init; }
    public required string PreviewText { get; init; }
    public int CharacterCount { get; init; }
    public int? ImageWidth { get; init; }
    public int? ImageHeight { get; init; }
    public IReadOnlyList<string> FilePaths { get; init; } = [];
    public IReadOnlyList<PayloadWriteRequest> Payloads { get; init; } = [];
}

public sealed class PayloadWriteRequest
{
    public required ClipboardFormatKind Format { get; init; }
    public required byte[] Bytes { get; init; }
    public string? RelativeFileName { get; init; }
}
