using System.Text;
using ClipStack.Core.Hashing;
using ClipStack.Core.Models;
using ClipStack.Core.Settings;
using ClipStack.Core.Storage;
using ClipStack.Core.Utilities;

namespace ClipStack.Core.Tests;

[TestClass]
public class ContentHasherTests
{
    [TestMethod]
    public void ComputeHash_IsStable_ForSamePayloads()
    {
        var bytes = Encoding.UTF8.GetBytes("hello world");
        var a = ContentHasher.ComputeHash([(ClipboardFormatKind.UnicodeText, bytes)]);
        var b = ContentHasher.ComputeHash([(ClipboardFormatKind.UnicodeText, bytes)]);
        Assert.AreEqual(a, b);
        Assert.AreEqual(64, a.Length);
    }

    [TestMethod]
    public void ComputeHash_Changes_WhenPayloadChanges()
    {
        var a = ContentHasher.ComputeHash([(ClipboardFormatKind.UnicodeText, Encoding.UTF8.GetBytes("a"))]);
        var b = ContentHasher.ComputeHash([(ClipboardFormatKind.UnicodeText, Encoding.UTF8.GetBytes("b"))]);
        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void NormalizeFilePaths_IsCaseInsensitiveAndSorted()
    {
        var normalized = ContentHasher.NormalizeFilePaths(
        [
            @"C:\Temp\B.txt",
            @"c:\temp\a.txt",
            @"C:\Temp\B.txt",
        ]);

        Assert.AreEqual(2, normalized.Count);
        Assert.IsTrue(normalized[0].EndsWith("a.txt", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(normalized[1].EndsWith("B.txt", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ComputeHash_IncludesNormalizedFilePaths()
    {
        var text = Encoding.UTF8.GetBytes("x");
        var a = ContentHasher.ComputeHash([(ClipboardFormatKind.FileDropList, text)], [@"C:\A\file.txt"]);
        var b = ContentHasher.ComputeHash([(ClipboardFormatKind.FileDropList, text)], [@"C:\B\file.txt"]);
        Assert.AreNotEqual(a, b);
    }
}

[TestClass]
public class HistoryStoreTests
{
    private string _root = null!;
    private HistoryStore _store = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "ClipStackTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new HistoryStore(new StoragePaths(_root));
        _store.Initialize();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { /* ignore */ }
    }

    private static NewClipboardItemData MakeTextItem(string text, string? hash = null)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var contentHash = hash ?? ContentHasher.ComputeHash([(ClipboardFormatKind.UnicodeText, bytes)]);
        return new NewClipboardItemData
        {
            DominantKind = ClipboardItemKind.Text,
            ContentHash = contentHash,
            PreviewText = TextPreview.Create(text),
            CharacterCount = text.Length,
            Payloads =
            [
                new PayloadWriteRequest
                {
                    Format = ClipboardFormatKind.UnicodeText,
                    Bytes = bytes,
                },
            ],
        };
    }

    [TestMethod]
    public void AddOrPromote_DeduplicatesAndMovesToTop()
    {
        var first = _store.AddOrPromote(MakeTextItem("one"), 10);
        _ = _store.AddOrPromote(MakeTextItem("two"), 10);
        var again = _store.AddOrPromote(MakeTextItem("one"), 10);

        Assert.IsTrue(again.WasDuplicate);
        Assert.AreEqual(first.Item.Id, again.Item.Id);
        Assert.AreEqual(2, _store.Items.Count);
        Assert.AreEqual(first.Item.Id, _store.Items[0].Id);
    }

    [TestMethod]
    public void AddOrPromote_EvictsOldestAtLimit()
    {
        for (var i = 0; i < 5; i++)
            _store.AddOrPromote(MakeTextItem($"item-{i}"), 3);

        Assert.AreEqual(3, _store.Items.Count);
        Assert.AreEqual("item-4", _store.Items[0].PreviewText);
        Assert.IsFalse(_store.Items.Any(i => i.PreviewText == "item-0"));
        Assert.IsFalse(_store.Items.Any(i => i.PreviewText == "item-1"));
    }

    [TestMethod]
    public void TrimToLimit_EvictsImmediately()
    {
        for (var i = 0; i < 5; i++)
            _store.AddOrPromote(MakeTextItem($"item-{i}"), 10);

        Assert.AreEqual(5, _store.Items.Count);
        var removed = _store.TrimToLimit(2);
        Assert.AreEqual(3, removed);
        Assert.AreEqual(2, _store.Items.Count);
        Assert.AreEqual("item-4", _store.Items[0].PreviewText);
        Assert.AreEqual("item-3", _store.Items[1].PreviewText);
    }

    [TestMethod]
    public void AddOrPromote_DuplicateAlsoRespectsLoweredLimit()
    {
        for (var i = 0; i < 4; i++)
            _store.AddOrPromote(MakeTextItem($"item-{i}"), 10);

        _ = _store.AddOrPromote(MakeTextItem("item-3"), 2);
        Assert.AreEqual(2, _store.Items.Count);
        Assert.AreEqual("item-3", _store.Items[0].PreviewText);
    }

    [TestMethod]
    public void AtomicSaveAndReload_PreservesItems()
    {
        _store.AddOrPromote(MakeTextItem("persist-me"), 10);
        var store2 = new HistoryStore(new StoragePaths(_root));
        store2.Initialize();
        Assert.AreEqual(1, store2.Items.Count);
        Assert.AreEqual("persist-me", store2.Items[0].PreviewText);
        Assert.IsTrue(File.Exists(store2.ResolvePayloadPath(store2.Items[0], store2.Items[0].Payloads[0])));
    }

    [TestMethod]
    public void CorruptIndex_RecoversFromFolders()
    {
        var added = _store.AddOrPromote(MakeTextItem("recover"), 10).Item;
        File.WriteAllText(Path.Combine(_root, "index.json"), "{ not-json");

        var store2 = new HistoryStore(new StoragePaths(_root));
        store2.Initialize();

        Assert.IsTrue(store2.Items.Count >= 1);
        Assert.IsTrue(Directory.Exists(Path.Combine(_root, "items", added.Id.ToString("D"))));
        Assert.IsTrue(Directory.EnumerateFiles(_root, "index.corrupt.*.json").Any());
    }

    [TestMethod]
    public void MissingPayload_IsRemovedFromIndex()
    {
        var item = _store.AddOrPromote(MakeTextItem("gone"), 10).Item;
        var dir = Path.Combine(_root, "items", item.Id.ToString("D"));
        Directory.Delete(dir, recursive: true);

        var store2 = new HistoryStore(new StoragePaths(_root));
        store2.Initialize();
        Assert.AreEqual(0, store2.Items.Count);
    }

    [TestMethod]
    public void TemporaryFolders_AreCleanedOnInitialize()
    {
        var tmp = Path.Combine(_root, "items", $".tmp-{Guid.NewGuid():D}");
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "x.txt"), "x");

        var store2 = new HistoryStore(new StoragePaths(_root));
        store2.Initialize();
        Assert.IsFalse(Directory.Exists(tmp));
    }

    [TestMethod]
    public void DeleteItem_RemovesFolder()
    {
        var item = _store.AddOrPromote(MakeTextItem("delete-me"), 10).Item;
        Assert.IsTrue(_store.DeleteItem(item.Id));
        Assert.AreEqual(0, _store.Items.Count);
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "items", item.Id.ToString("D"))));
    }

    [TestMethod]
    public void ClearAll_RemovesEverything()
    {
        _store.AddOrPromote(MakeTextItem("a"), 10);
        _store.AddOrPromote(MakeTextItem("b"), 10);
        _store.ClearAll();
        Assert.AreEqual(0, _store.Items.Count);
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(_root, "items")).Length);
    }

    [TestMethod]
    public void CalculateDiskUsage_CountsPayloads()
    {
        _store.AddOrPromote(MakeTextItem("usage-check"), 10);
        Assert.IsTrue(_store.CalculateDiskUsageBytes() > 0);
    }

    [TestMethod]
    public void PathTraversal_InIndex_IsRejected()
    {
        _store.AddOrPromote(MakeTextItem("safe"), 10);
        var indexPath = Path.Combine(_root, "index.json");
        var json = File.ReadAllText(indexPath);
        json = json.Replace("text.txt", "..\\\\..\\\\evil.txt");
        File.WriteAllText(indexPath, json);

        var store2 = new HistoryStore(new StoragePaths(_root));
        store2.Initialize();
        // Corrupt/unsafe index should be backed up and reconstructed without traversal paths
        Assert.IsFalse(store2.Items.Any(i => i.Payloads.Any(p => p.RelativePath.Contains(".."))));
    }
}

[TestClass]
public class SettingsStoreTests
{
    [TestMethod]
    public void Settings_ValidateAndClamp_HistoryLimit()
    {
        var s = new AppSettings { HistoryLimit = 999, MaxItemSizeBytes = -5 };
        s.ValidateAndClamp();
        Assert.AreEqual(AppSettings.MaxHistoryLimit, s.HistoryLimit);
        Assert.AreEqual(0, s.MaxItemSizeBytes);
    }

    [TestMethod]
    public void Settings_PersistAndReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClipStackTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new StoragePaths(root);
            var store = new SettingsStore(paths);
            store.Initialize();
            store.Update(s =>
            {
                s.HistoryLimit = 20;
                s.AutoPaste = false;
                s.HotKey = new HotKeyConfiguration { Control = true, Alt = true, Shift = false, Win = false, VirtualKey = 0x51 };
            });

            var store2 = new SettingsStore(paths);
            store2.Initialize();
            var loaded = store2.Current;
            Assert.AreEqual(20, loaded.HistoryLimit);
            Assert.IsFalse(loaded.AutoPaste);
            Assert.AreEqual(0x51, loaded.HotKey.VirtualKey);
            Assert.IsTrue(loaded.HotKey.Alt);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void HotKey_Serialization_RoundTrip()
    {
        var hk = new HotKeyConfiguration { Control = true, Shift = true, VirtualKey = 0x53 };
        Assert.AreEqual("Ctrl + Shift + S", hk.ToDisplayString());
        Assert.IsTrue(hk.IsValid);

        var invalid = new HotKeyConfiguration { Control = false, Alt = false, Shift = false, Win = false, VirtualKey = 0x53 };
        Assert.IsFalse(invalid.IsValid);
    }

    [TestMethod]
    public void MaxItemSize_ZeroMeansUnlimited()
    {
        var s = new AppSettings { MaxItemSizeBytes = 0 };
        s.ValidateAndClamp();
        Assert.AreEqual(0, s.MaxItemSizeBytes);
    }
}

[TestClass]
public class ReleaseConfigStoreTests
{
    [TestMethod]
    public void Load_MigratesPersistedEmptyFeedFromConfiguredBundle()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClipStackTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new StoragePaths(root);
            paths.EnsureCreated();
            File.WriteAllText(paths.ReleaseConfigFile, """
                {
                  "feedUrl": "",
                  "channel": "stable",
                  "automaticChecks": true
                }
                """);

            var bundled = Path.Combine(root, "bundled-release-config.json");
            File.WriteAllText(bundled, """
                {
                  "feedUrl": "https://github.com/sounak1125/ClipStack",
                  "channel": "win",
                  "automaticChecks": true
                }
                """);

            var store = new ReleaseConfigStore(paths);
            var loaded = store.Load(bundled);
            var persisted = store.Load();

            Assert.AreEqual("https://github.com/sounak1125/ClipStack", loaded.FeedUrl);
            Assert.AreEqual("win", loaded.Channel);
            Assert.AreEqual(loaded.FeedUrl, persisted.FeedUrl);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch { /* ignore */ }
        }
    }
}

[TestClass]
public class PathSafetyTests
{
    [TestMethod]
    public void ResolveSafeRelativePath_BlocksTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClipStackTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                PathSafety.ResolveSafeRelativePath(root, @"..\evil.txt"));
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                PathSafety.ResolveSafeRelativePath(root, @"C:\Windows\notepad.exe"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void IsPathInsideRoot_Works()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClipStackTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var inside = Path.Combine(root, "a", "b.txt");
            Assert.IsTrue(PathSafety.IsPathInsideRoot(root, inside));
            Assert.IsFalse(PathSafety.IsPathInsideRoot(root, Path.GetTempPath()));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

[TestClass]
public class ImageSizeGuardTests
{
    [TestMethod]
    public void Rejects_OverflowAndHugeDimensions()
    {
        Assert.IsFalse(ImageSizeGuard.TryEstimateUncompressedBytes(int.MaxValue, int.MaxValue, 4, out _));
        Assert.IsFalse(ImageSizeGuard.IsWithinBudget(20000, 20000));
        Assert.IsTrue(ImageSizeGuard.IsWithinBudget(100, 100));
    }
}

[TestClass]
public class ImageFileDetectorTests
{
    [TestMethod]
    public void IsImageFilePath_RecognizesCommonExtensions()
    {
        Assert.IsTrue(ImageFileDetector.IsImageFilePath(@"C:\photos\shot.JPG"));
        Assert.IsTrue(ImageFileDetector.IsImageFilePath(@"C:\a\b.webp"));
        Assert.IsFalse(ImageFileDetector.IsImageFilePath(@"C:\docs\file.pdf"));
        Assert.IsFalse(ImageFileDetector.IsImageFilePath(@"C:\docs\file"));
    }

    [TestMethod]
    public void IsSingleExistingImageFile_RequiresExactlyOneExistingImage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ClipStackTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var jpg = Path.Combine(dir, "a.jpg");
            var txt = Path.Combine(dir, "b.txt");
            File.WriteAllBytes(jpg, [1, 2, 3]);
            File.WriteAllText(txt, "x");

            Assert.IsTrue(ImageFileDetector.IsSingleExistingImageFile([jpg]));
            Assert.IsFalse(ImageFileDetector.IsSingleExistingImageFile([jpg, Path.Combine(dir, "c.png")]));
            Assert.IsFalse(ImageFileDetector.IsSingleExistingImageFile([txt]));
            Assert.IsFalse(ImageFileDetector.IsSingleExistingImageFile([]));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [TestMethod]
    public void SafeOriginalFileName_UsesOriginalPrefix()
    {
        Assert.AreEqual("original.jpg", ImageFileDetector.SafeOriginalFileName(@"C:\x\photo.JPEG"));
        Assert.AreEqual("original.png", ImageFileDetector.SafeOriginalFileName(@"C:\x\a.png"));
        Assert.IsTrue(PayloadFileNames.IsOriginalImageFileName("original.webp"));
        Assert.IsFalse(PayloadFileNames.IsOriginalImageFileName("image.png"));
    }
}

[TestClass]
public class ImageOriginalHashTests
{
    [TestMethod]
    public void ComputeHash_Changes_WhenOriginalImageBytesChange()
    {
        var a = ContentHasher.ComputeHash([(ClipboardFormatKind.ImageOriginal, new byte[] { 1, 2, 3 })]);
        var b = ContentHasher.ComputeHash([(ClipboardFormatKind.ImageOriginal, new byte[] { 1, 2, 4 })]);
        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void HistoryStore_PersistsOriginalImageRelativeFileName()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClipStackTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new HistoryStore(new StoragePaths(root));
            store.Initialize();

            var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0x00 }; // not a real jpeg; storage only
            var data = new NewClipboardItemData
            {
                DominantKind = ClipboardItemKind.Image,
                ContentHash = ContentHasher.ComputeHash([(ClipboardFormatKind.ImageOriginal, bytes)]),
                PreviewText = "photo.jpg",
                ImageWidth = 10,
                ImageHeight = 10,
                FilePaths = [@"C:\photos\photo.jpg"],
                Payloads =
                [
                    new PayloadWriteRequest
                    {
                        Format = ClipboardFormatKind.ImageOriginal,
                        Bytes = bytes,
                        RelativeFileName = "original.jpg",
                    },
                ],
            };

            var added = store.AddOrPromote(data, 10).Item;
            var path = store.ResolvePayloadPath(added, added.Payloads[0]);
            Assert.IsTrue(path.EndsWith("original.jpg", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(File.Exists(path));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));

            var reloaded = new HistoryStore(new StoragePaths(root));
            reloaded.Initialize();
            Assert.AreEqual(1, reloaded.Items.Count);
            Assert.AreEqual(ClipboardItemKind.Image, reloaded.Items[0].DominantKind);
            Assert.IsTrue(reloaded.Items[0].Payloads.Any(p => p.Format == ClipboardFormatKind.ImageOriginal));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
