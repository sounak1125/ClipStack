using System.Text;
using System.Text.Json;
using ClipStack.Core.Hashing;
using ClipStack.Core.Models;
using ClipStack.Core.Storage;

namespace ClipStack.Core.Tests;

[TestClass]
public class IndexRecoveryTests
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
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private HistoryStore NewStore()
    {
        var store = new HistoryStore(new StoragePaths(_root));
        store.Initialize();
        return store;
    }

    private static NewClipboardItemData TextItem(string text) => new()
    {
        DominantKind = ClipboardItemKind.Text,
        ContentHash = ContentHasher.ComputeTextOnlyHash(text),
        PreviewText = text,
        CharacterCount = text.Length,
        Payloads = [new PayloadWriteRequest { Format = ClipboardFormatKind.UnicodeText, Bytes = Encoding.UTF8.GetBytes(text) }],
    };

    private string ItemDir(Guid id) => Path.Combine(_root, "items", id.ToString("D"));

    private string MetadataPath(Guid id) => Path.Combine(ItemDir(id), PayloadFileNames.Metadata);

    private void CorruptIndexFile() => File.WriteAllText(Path.Combine(_root, "index.json"), "{ not-json");

    /// <summary>
    /// One unsafe entry must cost that entry, not every pin, hash and timestamp in the file.
    /// </summary>
    [TestMethod]
    public void UnsafePathInOneEntry_DoesNotDiscardTheRestOfTheIndex()
    {
        var store = NewStore();
        var keep = store.AddOrPromote(TextItem("keep me"), 10).Item;
        var pinned = store.AddOrPromote(TextItem("pinned clip"), 10).Item;
        store.TogglePin(pinned.Id);
        var doomed = store.AddOrPromote(TextItem("unsafe entry"), 10).Item;

        // Corrupt exactly one entry's payload path, leaving the file itself valid JSON.
        var indexPath = Path.Combine(_root, "index.json");
        var index = JsonSerializer.Deserialize<ClipboardIndex>(File.ReadAllText(indexPath), AppIdentity.JsonOptions)!;
        index.Items.First(i => i.Id == doomed.Id).Payloads[0].RelativePath = "..\\..\\evil.txt";
        File.WriteAllText(indexPath, JsonSerializer.Serialize(index, AppIdentity.JsonOptions));

        var reopened = NewStore();

        Assert.IsFalse(reopened.Items.Any(i => i.Id == doomed.Id), "The unsafe entry must be dropped.");
        Assert.IsTrue(reopened.Items.Any(i => i.Id == keep.Id), "Every other entry must survive.");
        Assert.AreEqual(keep.ContentHash, reopened.Items.First(i => i.Id == keep.Id).ContentHash);

        var survivingPin = reopened.Items.FirstOrDefault(i => i.Id == pinned.Id);
        Assert.IsNotNull(survivingPin, "The pinned entry must survive.");
        Assert.IsTrue(survivingPin.IsPinned, "A pin must survive a single bad neighbour.");

        Assert.IsFalse(
            Directory.EnumerateFiles(_root, "index.corrupt.*.json").Any(),
            "A single bad entry is not whole-file corruption.");
    }

    /// <summary>
    /// A genuinely unparseable file still falls back to rebuilding from folders.
    /// </summary>
    [TestMethod]
    public void UnparseableIndex_StillRebuildsFromFolders()
    {
        var store = NewStore();
        var item = store.AddOrPromote(TextItem("recover me"), 10).Item;
        CorruptIndexFile();

        var reopened = NewStore();

        Assert.AreEqual(1, reopened.Items.Count);
        Assert.AreEqual(item.Id, reopened.Items[0].Id);
        Assert.IsTrue(Directory.EnumerateFiles(_root, "index.corrupt.*.json").Any());
    }

    /// <summary>
    /// A clip recovered from folders must still deduplicate against a fresh capture of the
    /// same content, or re-copying that text adds a second row forever.
    /// </summary>
    [TestMethod]
    public void RecoveredClip_StillDeduplicatesAgainstAFreshCapture()
    {
        const string text = "the same text";
        var store = NewStore();
        var original = store.AddOrPromote(TextItem(text), 10).Item;
        CorruptIndexFile();

        var reopened = NewStore();
        Assert.AreEqual(1, reopened.Items.Count);
        Assert.AreEqual(original.ContentHash, reopened.Items[0].ContentHash, "The real hash must survive recovery.");
        Assert.IsFalse(reopened.Items[0].ContentHash.StartsWith(HistoryStore.RecoveredHashPrefix, StringComparison.Ordinal));

        var (item, wasDuplicate) = reopened.AddOrPromote(TextItem(text), 10);

        Assert.IsTrue(wasDuplicate, "Re-copying the same text must promote, not add a row.");
        Assert.AreEqual(original.Id, item.Id);
        Assert.AreEqual(1, reopened.Items.Count);
    }

    [TestMethod]
    public void RecoveredClip_KeepsPinAndCaptureTime()
    {
        var store = NewStore();
        var item = store.AddOrPromote(TextItem("pin survivor"), 10).Item;
        store.TogglePin(item.Id);
        CorruptIndexFile();

        var recovered = NewStore().Items.Single();

        Assert.IsTrue(recovered.IsPinned, "Pin state must survive recovery.");
        Assert.AreEqual(item.CapturedUtc.ToUnixTimeSeconds(), recovered.CapturedUtc.ToUnixTimeSeconds());
        Assert.AreEqual("pin survivor", recovered.PreviewText);
    }

    [TestMethod]
    public void UnpinnedClip_DoesNotComeBackPinnedAfterRecovery()
    {
        var store = NewStore();
        var item = store.AddOrPromote(TextItem("toggled twice"), 10).Item;
        store.TogglePin(item.Id);
        store.TogglePin(item.Id);
        CorruptIndexFile();

        Assert.IsFalse(NewStore().Items.Single().IsPinned);
    }

    /// <summary>
    /// The sidecar carries preview text, so it must get the same protection as a payload.
    /// </summary>
    [TestMethod]
    public void Metadata_IsEncryptedOnDisk_WhenEncryptionIsOn()
    {
        var store = new HistoryStore(new StoragePaths(_root)) { EncryptPayloads = true };
        store.Initialize();
        var item = store.AddOrPromote(TextItem("secret preview"), 10).Item;

        var raw = File.ReadAllBytes(MetadataPath(item.Id));

        Assert.IsFalse(
            Encoding.UTF8.GetString(raw).Contains("secret preview", StringComparison.Ordinal),
            "Preview text must not sit in the clear beside encrypted payloads.");

        CorruptIndexFile();
        Assert.AreEqual("secret preview", NewStore().Items.Single().PreviewText, "Recovery must still read it.");
    }

    /// <summary>
    /// An existing history predates the sidecar, so it gets one on first open — otherwise
    /// the fix would only protect clips captured after the upgrade.
    /// </summary>
    [TestMethod]
    public void MissingSidecar_IsBackfilledOnOpen()
    {
        var store = NewStore();
        var item = store.AddOrPromote(TextItem("legacy clip"), 10).Item;

        File.Delete(MetadataPath(item.Id));
        Assert.IsFalse(File.Exists(MetadataPath(item.Id)));

        NewStore();
        Assert.IsTrue(File.Exists(MetadataPath(item.Id)), "Opening the store must backfill the sidecar.");

        CorruptIndexFile();
        Assert.AreEqual(item.ContentHash, NewStore().Items.Single().ContentHash);
    }

    /// <summary>
    /// A folder with no sidecar at all still recovers, just without a matchable hash.
    /// </summary>
    [TestMethod]
    public void FolderWithoutSidecar_FallsBackToTheRecoveredSentinel()
    {
        var store = NewStore();
        var item = store.AddOrPromote(TextItem("no sidecar"), 10).Item;

        // Delete the index first so reopening cannot backfill before recovery runs.
        File.Delete(Path.Combine(_root, "index.json"));
        File.Delete(MetadataPath(item.Id));

        var recovered = NewStore().Items.Single();

        Assert.IsTrue(recovered.ContentHash.StartsWith(HistoryStore.RecoveredHashPrefix, StringComparison.Ordinal));
        Assert.AreEqual("no sidecar", recovered.PreviewText, "Payload-derived facts still recover.");
    }

    [TestMethod]
    public void Metadata_IsNotTreatedAsAPayload()
    {
        var store = NewStore();
        var item = store.AddOrPromote(TextItem("payload check"), 10).Item;

        Assert.IsFalse(
            item.Payloads.Any(p => p.RelativePath.Equals(PayloadFileNames.Metadata, StringComparison.OrdinalIgnoreCase)));

        CorruptIndexFile();
        Assert.IsFalse(
            NewStore().Items.Single().Payloads.Any(p =>
                p.RelativePath.Equals(PayloadFileNames.Metadata, StringComparison.OrdinalIgnoreCase)));
    }
}
