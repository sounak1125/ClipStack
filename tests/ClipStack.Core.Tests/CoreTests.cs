using System.Text;
using ClipStack.Core.Hashing;
using ClipStack.Core.Models;
using ClipStack.Core.Settings;
using ClipStack.Core.Storage;
using ClipStack.Core.Updates;
using ClipStack.Core.Utilities;

namespace ClipStack.Core.Tests;

[TestClass]
public class AutomaticUpdateScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ShouldCheck_AlwaysChecksOnLaunch()
    {
        var checkedOneMinuteAgo = Now.AddMinutes(-1);

        Assert.IsTrue(AutomaticUpdateSchedule.ShouldCheck(checkedOneMinuteAgo, Now, isLaunchCheck: true));
    }

    [TestMethod]
    public void ShouldCheck_UsesCooldownForBackgroundChecks()
    {
        Assert.IsFalse(AutomaticUpdateSchedule.ShouldCheck(Now.AddHours(-23), Now, isLaunchCheck: false));
        Assert.IsTrue(AutomaticUpdateSchedule.ShouldCheck(Now.AddHours(-24), Now, isLaunchCheck: false));
        Assert.IsTrue(AutomaticUpdateSchedule.ShouldCheck(null, Now, isLaunchCheck: false));
    }
}

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
    public void Settings_NormalizeTheme_Allowlist()
    {
        Assert.AreEqual("Dark", AppSettings.NormalizeTheme(null));
        Assert.AreEqual("Dark", AppSettings.NormalizeTheme(""));
        Assert.AreEqual("Dark", AppSettings.NormalizeTheme("System"));
        Assert.AreEqual("Light", AppSettings.NormalizeTheme("light"));
        Assert.AreEqual("Dim", AppSettings.NormalizeTheme("DIM"));
        Assert.AreEqual("Contrast", AppSettings.NormalizeTheme("Contrast"));

        var s = new AppSettings { Theme = "System" };
        s.ValidateAndClamp();
        Assert.AreEqual("Dark", s.Theme);
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

            // Payloads are encrypted at rest, so read back through the store rather than
            // comparing raw file bytes.
            CollectionAssert.AreEqual(bytes, store.ReadPayloadBytes(added, ClipboardFormatKind.ImageOriginal));

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

[TestClass]
public class ClipboardExclusionFormatsTests
{
    [TestMethod]
    public void PresenceMarkers_CoverKnownPasswordManagerFormats()
    {
        CollectionAssert.Contains(
            ClipboardExclusionFormats.PresenceMarkers,
            "ExcludeClipboardContentFromMonitorProcessing");
        CollectionAssert.Contains(ClipboardExclusionFormats.PresenceMarkers, "Clipboard Viewer Ignore");
        CollectionAssert.Contains(ClipboardExclusionFormats.PresenceMarkers, "ClipboardViewerIgnore");
    }

    [TestMethod]
    public void PolicyMarkers_CoverClipboardHistoryOptOut_ButNotCloudSync()
    {
        CollectionAssert.Contains(ClipboardExclusionFormats.PolicyMarkers, "CanIncludeInClipboardHistory");

        // Cloud-sync opt-out is a weaker signal: Windows still keeps those clips locally,
        // so honouring it here would silently drop clips the user expects to keep.
        CollectionAssert.DoesNotContain(ClipboardExclusionFormats.PolicyMarkers, "CanUploadToCloudClipboard");
    }

    [TestMethod]
    public void PolicyValue_Zero_Excludes_NonZero_Allows()
    {
        Assert.IsFalse(ClipboardExclusionFormats.PolicyValueAllowsCapture(0));
        Assert.IsTrue(ClipboardExclusionFormats.PolicyValueAllowsCapture(1));
        Assert.IsFalse(ClipboardExclusionFormats.PolicyValueAllowsCapture(false));
        Assert.IsTrue(ClipboardExclusionFormats.PolicyValueAllowsCapture(true));
        Assert.IsFalse(ClipboardExclusionFormats.PolicyValueAllowsCapture("0"));
        Assert.IsTrue(ClipboardExclusionFormats.PolicyValueAllowsCapture("1"));
    }

    [TestMethod]
    public void PolicyValue_FailsClosed_WhenUnreadable()
    {
        // A marker present but unparseable must exclude: the app set it deliberately.
        Assert.IsFalse(ClipboardExclusionFormats.PolicyValueAllowsCapture(null));
        Assert.IsFalse(ClipboardExclusionFormats.PolicyValueAllowsCapture(new object()));
        Assert.IsFalse(ClipboardExclusionFormats.PolicyValueAllowsCapture("not-a-number"));
        Assert.IsFalse(ClipboardExclusionFormats.PolicyValueAllowsCapture(Array.Empty<byte>()));
        Assert.IsFalse(ClipboardExclusionFormats.PolicyValueAllowsCapture(new byte[] { 1, 0, 0, 0, 0 }));
    }

    [TestMethod]
    public void PolicyValue_ReadsDwordBytes()
    {
        Assert.IsFalse(ClipboardExclusionFormats.PolicyValueAllowsCapture(new byte[] { 0, 0, 0, 0 }));
        Assert.IsTrue(ClipboardExclusionFormats.PolicyValueAllowsCapture(new byte[] { 1, 0, 0, 0 }));
        Assert.IsFalse(ClipboardExclusionFormats.BytesAllowCapture(ReadOnlySpan<byte>.Empty));
    }

    [TestMethod]
    public void PolicyValue_ReadsDwordStream()
    {
        Assert.IsFalse(ClipboardExclusionFormats.PolicyValueAllowsCapture(new MemoryStream(new byte[] { 0, 0, 0, 0 })));
        Assert.IsTrue(ClipboardExclusionFormats.PolicyValueAllowsCapture(new MemoryStream(new byte[] { 1, 0, 0, 0 })));
    }

    [TestMethod]
    public void PolicyValue_RewindsStream_SoAPartiallyReadValueStillParses()
    {
        var stream = new MemoryStream(new byte[] { 1, 0, 0, 0 });
        stream.ReadByte();
        Assert.IsTrue(ClipboardExclusionFormats.PolicyValueAllowsCapture(stream));
    }
}

[TestClass]
public class PayloadEncryptionTests
{
    private string _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "ClipStackTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private static ClipboardItem Add(HistoryStore store, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return store.AddOrPromote(new NewClipboardItemData
        {
            DominantKind = ClipboardItemKind.Text,
            ContentHash = ContentHasher.ComputeHash([(ClipboardFormatKind.UnicodeText, bytes)]),
            PreviewText = text,
            CharacterCount = text.Length,
            Payloads = [new PayloadWriteRequest { Format = ClipboardFormatKind.UnicodeText, Bytes = bytes }],
        }, 10).Item;
    }

    [TestMethod]
    public void Protect_RoundTrips()
    {
        var plain = Encoding.UTF8.GetBytes("correct horse battery staple");
        var sealed_ = PayloadProtector.Protect(plain);

        Assert.IsTrue(PayloadProtector.IsProtected(sealed_));
        CollectionAssert.AreNotEqual(plain, sealed_);
        CollectionAssert.AreEqual(plain, PayloadProtector.Unprotect(sealed_));
    }

    [TestMethod]
    public void Unprotect_PassesPlaintextThrough()
    {
        // Payloads written before encryption existed carry no header and must stay readable.
        var legacy = Encoding.UTF8.GetBytes("written by an older build");
        Assert.IsFalse(PayloadProtector.IsProtected(legacy));
        CollectionAssert.AreEqual(legacy, PayloadProtector.Unprotect(legacy));
    }

    [TestMethod]
    public void Protect_LeavesEmptyInputAlone()
    {
        var empty = Array.Empty<byte>();
        Assert.AreSame(empty, PayloadProtector.Protect(empty));
        Assert.IsFalse(PayloadProtector.IsProtected(empty));
    }

    [TestMethod]
    public void StoredPayload_IsNotReadableAsPlaintextOnDisk()
    {
        var store = new HistoryStore(new StoragePaths(_root)) { EncryptPayloads = true };
        store.Initialize();

        const string secret = "SECRET-not-on-disk-in-the-clear";
        var item = Add(store, secret);

        var onDisk = File.ReadAllBytes(store.ResolvePayloadPath(item, item.Payloads[0]));
        Assert.IsTrue(PayloadProtector.IsProtected(onDisk));

        var raw = Encoding.UTF8.GetString(onDisk);
        Assert.IsFalse(raw.Contains(secret, StringComparison.Ordinal), "secret found in the payload file");

        // ...but the store still returns it.
        Assert.AreEqual(secret, store.ReadPayloadText(item, ClipboardFormatKind.UnicodeText));
        Assert.IsTrue(item.Payloads[0].Encrypted);
    }

    [TestMethod]
    public void EncryptedHistory_SurvivesReload()
    {
        var store = new HistoryStore(new StoragePaths(_root)) { EncryptPayloads = true };
        store.Initialize();
        var item = Add(store, "persisted secret");

        var reloaded = new HistoryStore(new StoragePaths(_root));
        reloaded.Initialize();

        Assert.AreEqual("persisted secret", reloaded.ReadPayloadText(reloaded.GetById(item.Id)!, ClipboardFormatKind.UnicodeText));
    }

    [TestMethod]
    public void ExistingPlaintextItems_StayReadableAfterEnablingEncryption()
    {
        // The upgrade path: history written unencrypted, then encryption switched on.
        var plainStore = new HistoryStore(new StoragePaths(_root)) { EncryptPayloads = false };
        plainStore.Initialize();
        var legacy = Add(plainStore, "written before encryption");

        var onDisk = File.ReadAllBytes(plainStore.ResolvePayloadPath(legacy, legacy.Payloads[0]));
        Assert.IsFalse(PayloadProtector.IsProtected(onDisk));
        Assert.IsFalse(legacy.Payloads[0].Encrypted);

        var upgraded = new HistoryStore(new StoragePaths(_root)) { EncryptPayloads = true };
        upgraded.Initialize();

        // Old clip readable...
        Assert.AreEqual(
            "written before encryption",
            upgraded.ReadPayloadText(upgraded.GetById(legacy.Id)!, ClipboardFormatKind.UnicodeText));

        // ...and new clips encrypted alongside it.
        var fresh = Add(upgraded, "written after encryption");
        Assert.IsTrue(fresh.Payloads[0].Encrypted);
        Assert.AreEqual("written after encryption", upgraded.ReadPayloadText(fresh, ClipboardFormatKind.UnicodeText));
    }

    [TestMethod]
    public void DisablingEncryption_LeavesEarlierEncryptedClipsReadable()
    {
        var store = new HistoryStore(new StoragePaths(_root)) { EncryptPayloads = true };
        store.Initialize();
        var encrypted = Add(store, "encrypted clip");

        store.EncryptPayloads = false;
        var plain = Add(store, "plain clip");

        Assert.AreEqual("encrypted clip", store.ReadPayloadText(store.GetById(encrypted.Id)!, ClipboardFormatKind.UnicodeText));
        Assert.AreEqual("plain clip", store.ReadPayloadText(plain, ClipboardFormatKind.UnicodeText));
        Assert.IsFalse(plain.Payloads[0].Encrypted);
    }

    [TestMethod]
    public void CorruptCiphertext_ReportsFailureInsteadOfReturningGarbage()
    {
        var store = new HistoryStore(new StoragePaths(_root)) { EncryptPayloads = true };
        store.Initialize();
        var item = Add(store, "will be corrupted");

        var path = store.ResolvePayloadPath(item, item.Payloads[0]);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        bytes[^2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Exception? reported = null;
        store.DecryptionFailed += ex => reported = ex;

        Assert.IsNull(store.ReadPayloadBytes(store.GetById(item.Id)!, ClipboardFormatKind.UnicodeText));
        Assert.IsNotNull(reported, "DecryptionFailed should have been raised");
    }

    [TestMethod]
    public void EncryptHistorySetting_DefaultsOnAndRoundTrips()
    {
        Assert.IsTrue(new AppSettings().EncryptHistory);
        Assert.IsFalse(new AppSettings { EncryptHistory = false }.Clone().EncryptHistory);
    }
}

[TestClass]
public class PinnedItemTests
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
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private ClipboardItem Add(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return _store.AddOrPromote(new NewClipboardItemData
        {
            DominantKind = ClipboardItemKind.Text,
            ContentHash = ContentHasher.ComputeHash([(ClipboardFormatKind.UnicodeText, bytes)]),
            PreviewText = text,
            Payloads = [new PayloadWriteRequest { Format = ClipboardFormatKind.UnicodeText, Bytes = bytes }],
        }, 3).Item;
    }

    [TestMethod]
    public void TogglePin_FlipsAndPersists()
    {
        var item = Add("keep me");
        Assert.IsTrue(_store.TogglePin(item.Id));
        Assert.IsTrue(_store.GetById(item.Id)!.IsPinned);

        var reloaded = new HistoryStore(new StoragePaths(_root));
        reloaded.Initialize();
        Assert.IsTrue(reloaded.GetById(item.Id)!.IsPinned);

        Assert.IsFalse(_store.TogglePin(item.Id));
        Assert.IsFalse(_store.GetById(item.Id)!.IsPinned);
    }

    [TestMethod]
    public void TogglePin_ReturnsNullForUnknownId() =>
        Assert.IsNull(_store.TogglePin(Guid.NewGuid()));

    [TestMethod]
    public void PinnedItem_SurvivesEviction()
    {
        var pinned = Add("pin this");
        _store.TogglePin(pinned.Id);

        // Limit is 3; push well past it.
        for (var i = 0; i < 10; i++)
            Add($"filler {i}");

        Assert.IsNotNull(_store.GetById(pinned.Id));
        Assert.IsTrue(_store.GetById(pinned.Id)!.IsPinned);
    }

    [TestMethod]
    public void PinnedItems_SortAboveUnpinned()
    {
        Add("first");
        var target = Add("second");
        Add("third");

        _store.TogglePin(target.Id);
        Assert.AreEqual(target.Id, _store.Items[0].Id);

        // A newly captured clip goes to the top of the unpinned block, not above the pin.
        Add("fourth");
        Assert.AreEqual(target.Id, _store.Items[0].Id);
        Assert.AreEqual("fourth", _store.Items[1].PreviewText);
    }

    [TestMethod]
    public void Unpinning_ReturnsTheClipToItsRecencyPosition()
    {
        Add("oldest");
        Add("middle");
        Add("newest");

        var oldest = _store.Items.Last();
        _store.TogglePin(oldest.Id);
        Assert.AreEqual(oldest.Id, _store.Items[0].Id, "pinning should promote it");

        _store.TogglePin(oldest.Id);

        // A stable sort alone would strand it at the top, leaving the oldest clip above
        // newer ones.
        Assert.AreEqual(oldest.Id, _store.Items.Last().Id, "unpinning should send it back");
        Assert.AreEqual("newest", _store.Items[0].PreviewText);
    }

    [TestMethod]
    public void HistoryLimit_CountsUnpinnedOnly()
    {
        var pinned = Add("pinned");
        _store.TogglePin(pinned.Id);

        for (var i = 0; i < 5; i++)
            Add($"filler {i}");

        // Limit 3 governs the rolling history; the pin sits on top of it.
        Assert.AreEqual(3, _store.Items.Count(i => !i.IsPinned));
        Assert.AreEqual(4, _store.Items.Count);
    }

    [TestMethod]
    public void CaptureStillWorks_WhenEverythingIsPinned()
    {
        // Pinning every slot must not wedge capture.
        for (var i = 0; i < 3; i++)
        {
            var item = Add($"pinned {i}");
            _store.TogglePin(item.Id);
        }

        var fresh = Add("still captured");
        Assert.IsNotNull(_store.GetById(fresh.Id));
        Assert.AreEqual(3, _store.Items.Count(i => i.IsPinned));
    }

    [TestMethod]
    public void TrimToLimit_LeavesPinnedAlone()
    {
        var pinned = Add("pinned");
        _store.TogglePin(pinned.Id);
        Add("a");
        Add("b");

        _store.TrimToLimit(1);

        Assert.IsNotNull(_store.GetById(pinned.Id));
        Assert.AreEqual(1, _store.Items.Count(i => !i.IsPinned));
    }

    [TestMethod]
    public void DeleteItem_RemovesEvenWhenPinned()
    {
        var pinned = Add("pinned");
        _store.TogglePin(pinned.Id);

        Assert.IsTrue(_store.DeleteItem(pinned.Id));
        Assert.IsNull(_store.GetById(pinned.Id));
    }
}

[TestClass]
public class TextPreviewTests
{
    [TestMethod]
    public void Create_TruncatesAndCollapsesWhitespace()
    {
        Assert.AreEqual("a b c", TextPreview.Create("a   b \t c"));
        Assert.AreEqual(string.Empty, TextPreview.Create(null));
        Assert.AreEqual(string.Empty, TextPreview.Create(""));
    }

    [TestMethod]
    public void Create_KeepsAtMostThreeLines()
    {
        var preview = TextPreview.Create("one\ntwo\nthree\nfour\nfive");
        Assert.IsFalse(preview.Contains("four", StringComparison.Ordinal));
        Assert.IsTrue(preview.StartsWith("one", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Create_DoesNotScanTheWholeStringForALongSingleLine()
    {
        // 8 MB on one line: the old implementation walked every character and allocated a
        // StringBuilder the size of the input to produce a 240-character preview.
        var huge = new string('x', 8 * 1024 * 1024);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var preview = TextPreview.Create(huge);
        sw.Stop();

        Assert.IsTrue(preview.Length <= 241, $"preview was {preview.Length} chars");
        Assert.IsTrue(
            sw.ElapsedMilliseconds < 200,
            $"took {sw.ElapsedMilliseconds} ms, suggesting the whole string was scanned");
    }

    [TestMethod]
    public void Create_StillFillsThePreviewWhenInputIsHeavilySpaced()
    {
        // Whitespace collapses, so the scan window must be wide enough to still yield a
        // full preview from sparse input.
        var spaced = string.Join(" ", Enumerable.Repeat("word", 400));
        var preview = TextPreview.Create(spaced);
        Assert.IsTrue(preview.Length >= 200, $"preview was only {preview.Length} chars");
    }

    [TestMethod]
    public void NormalizeWhitespace_RespectsAnExplicitScanLimit()
    {
        Assert.AreEqual("abc", TextPreview.NormalizeWhitespace("abcdef", 3));
        Assert.AreEqual(string.Empty, TextPreview.NormalizeWhitespace("abcdef", 0));
        Assert.AreEqual("abcdef", TextPreview.NormalizeWhitespace("abcdef"));
    }
}

[TestClass]
public class PlainTextSettingTests
{
    [TestMethod]
    public void PasteAsPlainText_DefaultsOffAndRoundTrips()
    {
        Assert.IsFalse(new AppSettings().PasteAsPlainText);

        var settings = new AppSettings { PasteAsPlainText = true };
        Assert.IsTrue(settings.Clone().PasteAsPlainText);
    }
}

[TestClass]
public class ClipboardSearchTests
{
    private static ClipboardItem Item(
        string preview,
        ClipboardItemKind kind = ClipboardItemKind.Text,
        params string[] filePaths) => new()
        {
            Id = Guid.NewGuid(),
            PreviewText = preview,
            DominantKind = kind,
            FilePaths = [.. filePaths],
        };

    [TestMethod]
    public void ParseTerms_SplitsOnWhitespace()
    {
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, ClipboardSearch.ParseTerms("  alpha   beta "));
        Assert.IsEmpty(ClipboardSearch.ParseTerms("   "));
        Assert.IsEmpty(ClipboardSearch.ParseTerms(null));
    }

    [TestMethod]
    public void EmptyQuery_MatchesEverything()
    {
        Assert.IsTrue(ClipboardSearch.Matches(Item("anything"), ""));
        Assert.IsTrue(ClipboardSearch.Matches(Item("anything"), "   "));
        Assert.IsTrue(ClipboardSearch.Matches(Item("anything"), (string?)null));
    }

    [TestMethod]
    public void Matches_PreviewText_CaseInsensitively()
    {
        var item = Item("Invoice_2026_Q3.pdf");
        Assert.IsTrue(ClipboardSearch.Matches(item, "invoice"));
        Assert.IsTrue(ClipboardSearch.Matches(item, "Q3"));
        Assert.IsFalse(ClipboardSearch.Matches(item, "receipt"));
    }

    [TestMethod]
    public void MultipleTerms_AllMustMatch()
    {
        var item = Item("quarterly revenue summary");
        Assert.IsTrue(ClipboardSearch.Matches(item, "revenue summary"));
        Assert.IsTrue(ClipboardSearch.Matches(item, "summary quarterly"));
        Assert.IsFalse(ClipboardSearch.Matches(item, "revenue missing"));
    }

    [TestMethod]
    public void Matches_FilePaths_NotJustThePreview()
    {
        // A Files clip previews only the first two names, so paths must be searchable.
        var item = Item("a.txt, b.txt", ClipboardItemKind.Files, @"C:\reports\a.txt", @"C:\reports\z.txt");
        Assert.IsTrue(ClipboardSearch.Matches(item, "reports"));
        Assert.IsTrue(ClipboardSearch.Matches(item, "z.txt"));
    }

    [TestMethod]
    public void Matches_KindLabel_SoTypingImageNarrowsByKind()
    {
        Assert.IsTrue(ClipboardSearch.Matches(Item("screenshot", ClipboardItemKind.Image), "image"));
        Assert.IsTrue(ClipboardSearch.Matches(Item("a.txt", ClipboardItemKind.Files), "files"));
        Assert.IsFalse(ClipboardSearch.Matches(Item("hello", ClipboardItemKind.Text), "image"));
    }

    [TestMethod]
    public void Matches_ToleratesNullsFromADeserializedIndex()
    {
        var item = new ClipboardItem { Id = Guid.NewGuid(), PreviewText = null!, FilePaths = null! };
        Assert.IsFalse(ClipboardSearch.Matches(item, "anything"));
        Assert.IsTrue(ClipboardSearch.Matches(item, ""));
    }
}
