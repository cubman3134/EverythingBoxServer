using System.Text;
using System.Text.Json;
using EverythingBox.Server.Sync;

namespace EverythingBox.Server.Tests;

public sealed class SyncStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-sync-" + Guid.NewGuid().ToString("N"));

    public SyncStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } GC.SuppressFinalize(this); }

    private SyncStore NewStore(long quota = 1L << 30, long maxObject = 1L << 30) => new(_root, quota, maxObject);

    private static MemoryStream Body(byte[] bytes) => new(bytes, writable: false);
    private static MemoryStream Body(string text) => new(Encoding.UTF8.GetBytes(text), writable: false);
    private static byte[] ReadBlob(SyncObjectContent c) => File.ReadAllBytes(c.BlobPath);

    // ---- namespace validation ----

    [Theory]
    [InlineData("p1")]
    [InlineData("Prof-2.a_b")]
    [InlineData("A")]
    public void IsValidNamespace_accepts_safe_names(string ns) => Assert.True(SyncStore.IsValidNamespace(ns));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../x")]
    public void IsValidNamespace_rejects_unsafe_names(string ns) => Assert.False(SyncStore.IsValidNamespace(ns));

    [Fact]
    public void IsValidNamespace_rejects_65_char_name()
    {
        Assert.True(SyncStore.IsValidNamespace(new string('a', 64)));
        Assert.False(SyncStore.IsValidNamespace(new string('a', 65)));
    }

    // ---- put / get round-trip ----

    [Fact]
    public async Task Put_then_get_round_trips_exact_bytes_with_version_meta_size()
    {
        var store = NewStore();
        var payload = new byte[] { 1, 2, 3, 4, 250, 0, 99 };

        var put = await store.PutAsync("ns", "slot/1", Body(payload), SyncCondition.None, "opaque-meta", default);
        Assert.Equal(SyncWriteStatus.Ok, put.Status);
        Assert.False(string.IsNullOrEmpty(put.Version));

        var got = await store.GetAsync("ns", "slot/1", default);
        Assert.NotNull(got);
        Assert.Equal(put.Version, got!.Version);
        Assert.Equal("opaque-meta", got.Meta);
        Assert.Equal(payload.Length, got.Size);
        Assert.Equal(payload, ReadBlob(got));
    }

    [Fact]
    public async Task Second_put_changes_the_version()
    {
        var store = NewStore();
        var first = await store.PutAsync("ns", "k", Body("a"), SyncCondition.None, null, default);
        var second = await store.PutAsync("ns", "k", Body("bb"), SyncCondition.None, null, default);

        Assert.Equal(SyncWriteStatus.Ok, second.Status);
        Assert.NotEqual(first.Version, second.Version);

        var got = await store.GetAsync("ns", "k", default);
        Assert.Equal(2, got!.Size);
        Assert.Equal("bb", Encoding.UTF8.GetString(ReadBlob(got)));
    }

    [Fact]
    public async Task Get_returns_null_for_missing_key_or_namespace()
    {
        var store = NewStore();
        Assert.Null(await store.GetAsync("ns", "nope", default));
        Assert.Null(await store.GetAsync("..", "k", default)); // invalid ns → null, no disk touch
    }

    // ---- list ----

    [Fact]
    public async Task List_reports_entries_and_tombstones()
    {
        var store = NewStore();
        await store.PutAsync("ns", "one", Body("hello"), SyncCondition.None, "m1", default);
        await store.PutAsync("ns", "two", Body("xy"), SyncCondition.None, null, default);

        var list = await store.ListAsync("ns", default);
        Assert.Equal(2, list.Count);
        var one = list.Single(i => i.Key == "one");
        Assert.Equal(5, one.Size);
        Assert.Equal("m1", one.Meta);
        Assert.False(one.Deleted);
        Assert.False(string.IsNullOrEmpty(one.Version));

        await store.DeleteAsync("ns", "two", SyncCondition.None, default);
        var afterDelete = await store.ListAsync("ns", default);
        Assert.Equal(2, afterDelete.Count);
        Assert.True(afterDelete.Single(i => i.Key == "two").Deleted);
    }

    [Fact]
    public async Task List_of_invalid_namespace_is_empty()
        => Assert.Empty(await NewStore().ListAsync("a/b", default));

    // ---- If-Match ----

    [Fact]
    public async Task IfMatch_current_version_succeeds_stale_fails()
    {
        var store = NewStore();
        var v1 = (await store.PutAsync("ns", "k", Body("1"), SyncCondition.None, null, default)).Version!;

        var stale = await store.PutAsync("ns", "k", Body("2"),
            new SyncCondition(SyncConditionKind.IfMatch, "not-the-version"), null, default);
        Assert.Equal(SyncWriteStatus.PreconditionFailed, stale.Status);

        var current = await store.PutAsync("ns", "k", Body("3"),
            new SyncCondition(SyncConditionKind.IfMatch, v1), null, default);
        Assert.Equal(SyncWriteStatus.Ok, current.Status);
        Assert.NotEqual(v1, current.Version);

        // stale write did not alter the object
        var got = await store.GetAsync("ns", "k", default);
        Assert.Equal("3", Encoding.UTF8.GetString(ReadBlob(got!)));
    }

    [Fact]
    public async Task IfMatch_on_absent_key_fails()
    {
        var store = NewStore();
        var r = await store.PutAsync("ns", "missing", Body("x"),
            new SyncCondition(SyncConditionKind.IfMatch, "whatever"), null, default);
        Assert.Equal(SyncWriteStatus.PreconditionFailed, r.Status);
    }

    // ---- If-None-Match:* ----

    [Fact]
    public async Task IfNoneMatchStar_absent_then_live_then_after_delete()
    {
        var store = NewStore();
        var star = new SyncCondition(SyncConditionKind.IfNoneMatchStar);

        var created = await store.PutAsync("ns", "k", Body("a"), star, null, default);
        Assert.Equal(SyncWriteStatus.Ok, created.Status); // absent → allowed

        var blocked = await store.PutAsync("ns", "k", Body("b"), star, null, default);
        Assert.Equal(SyncWriteStatus.PreconditionFailed, blocked.Status); // live → refused

        await store.DeleteAsync("ns", "k", SyncCondition.None, default);
        var recreated = await store.PutAsync("ns", "k", Body("c"), star, null, default);
        Assert.Equal(SyncWriteStatus.Ok, recreated.Status); // tombstone counts as absent
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_hides_from_get_and_recreate_makes_live()
    {
        var store = NewStore();
        await store.PutAsync("ns", "k", Body("payload"), SyncCondition.None, "meta", default);

        var del = await store.DeleteAsync("ns", "k", SyncCondition.None, default);
        Assert.Equal(SyncWriteStatus.Ok, del.Status);
        Assert.Null(await store.GetAsync("ns", "k", default));

        var listed = (await store.ListAsync("ns", default)).Single(i => i.Key == "k");
        Assert.True(listed.Deleted);
        Assert.Equal(0, listed.Size);

        var recreate = await store.PutAsync("ns", "k", Body("again"), SyncCondition.None, null, default);
        Assert.Equal(SyncWriteStatus.Ok, recreate.Status);
        Assert.NotEqual(del.Version, recreate.Version);
        var got = await store.GetAsync("ns", "k", default);
        Assert.Equal("again", Encoding.UTF8.GetString(ReadBlob(got!)));
    }

    // ---- quota ----

    [Fact]
    public async Task Quota_exceeded_refuses_and_leaves_prior_object_intact()
    {
        var store = NewStore(quota: 100, maxObject: 1L << 30);
        var first = await store.PutAsync("ns", "a", Body(new byte[60]), SyncCondition.None, null, default);
        Assert.Equal(SyncWriteStatus.Ok, first.Status);

        // 60 (live) + 60 > 100 → refused
        var over = await store.PutAsync("ns", "b", Body(new byte[60]), SyncCondition.None, null, default);
        Assert.Equal(SyncWriteStatus.QuotaExceeded, over.Status);

        // prior object untouched and readable
        var got = await store.GetAsync("ns", "a", default);
        Assert.NotNull(got);
        Assert.Equal(60, got!.Size);
        Assert.Equal(new byte[60], ReadBlob(got));
        Assert.Null(await store.GetAsync("ns", "b", default));
    }

    [Fact]
    public async Task Quota_replaces_old_contribution_of_same_key()
    {
        var store = NewStore(quota: 100, maxObject: 1L << 30);
        await store.PutAsync("ns", "a", Body(new byte[80]), SyncCondition.None, null, default);
        // overwriting the same key: 80 - 80 + 90 = 90 <= 100 → allowed
        var grow = await store.PutAsync("ns", "a", Body(new byte[90]), SyncCondition.None, null, default);
        Assert.Equal(SyncWriteStatus.Ok, grow.Status);
    }

    // ---- object cap ----

    [Fact]
    public async Task Object_over_max_bytes_is_TooLarge_with_no_partial_blob()
    {
        var store = NewStore(quota: 1L << 30, maxObject: 50);
        var r = await store.PutAsync("ns", "big", Body(new byte[51]), SyncCondition.None, null, default);
        Assert.Equal(SyncWriteStatus.TooLarge, r.Status);

        Assert.Null(await store.GetAsync("ns", "big", default));
        Assert.Empty(await store.ListAsync("ns", default));

        // no leftover files (blob or temp) in the namespace dir beyond a possible index.json
        var nsDir = Path.Combine(_root, "ns");
        if (Directory.Exists(nsDir))
            Assert.DoesNotContain(Directory.GetFiles(nsDir), f => !f.EndsWith("index.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Object_exactly_at_max_bytes_is_accepted()
    {
        var store = NewStore(quota: 1L << 30, maxObject: 50);
        var r = await store.PutAsync("ns", "edge", Body(new byte[50]), SyncCondition.None, null, default);
        Assert.Equal(SyncWriteStatus.Ok, r.Status);
    }

    // ---- containment: invalid namespaces never touch disk outside root ----

    [Fact]
    public async Task Invalid_namespace_operations_are_inert()
    {
        var store = NewStore();
        Assert.Equal(SyncWriteStatus.PreconditionFailed,
            (await store.PutAsync("..", "k", Body("x"), SyncCondition.None, null, default)).Status);
        Assert.Equal(SyncWriteStatus.PreconditionFailed,
            (await store.PutAsync("a/b", "k", Body("x"), SyncCondition.None, null, default)).Status);
        Assert.Null(await store.GetAsync("../secret", "k", default));
        Assert.Empty(await store.ListAsync("..", default));
    }

    // ---- concurrency ----

    [Fact]
    public async Task Parallel_puts_to_same_key_yield_one_entry_and_parseable_index()
    {
        var store = NewStore();
        const int n = 40;
        var tasks = Enumerable.Range(0, n).Select(i =>
            store.PutAsync("ns", "shared", Body("v" + i), SyncCondition.None, null, default));
        var outcomes = await Task.WhenAll(tasks);
        Assert.All(outcomes, o => Assert.Equal(SyncWriteStatus.Ok, o.Status));

        var list = await store.ListAsync("ns", default);
        Assert.Single(list);
        Assert.Equal("shared", list[0].Key);

        // the on-disk index parses (never left half-written)
        var indexJson = File.ReadAllText(Path.Combine(_root, "ns", "index.json"));
        using var doc = JsonDocument.Parse(indexJson);
        Assert.True(doc.RootElement.GetProperty("Objects").TryGetProperty("shared", out _));

        // the winning version is retrievable
        var got = await store.GetAsync("ns", "shared", default);
        Assert.NotNull(got);
    }

    [Fact]
    public async Task Parallel_puts_to_distinct_keys_all_land()
    {
        var store = NewStore();
        const int n = 40;
        var tasks = Enumerable.Range(0, n).Select(i =>
            store.PutAsync("ns", "key-" + i, Body("body-" + i), SyncCondition.None, null, default));
        var outcomes = await Task.WhenAll(tasks);
        Assert.All(outcomes, o => Assert.Equal(SyncWriteStatus.Ok, o.Status));

        var list = await store.ListAsync("ns", default);
        Assert.Equal(n, list.Count);
        for (int i = 0; i < n; i++)
        {
            var got = await store.GetAsync("ns", "key-" + i, default);
            Assert.NotNull(got);
            Assert.Equal("body-" + i, Encoding.UTF8.GetString(ReadBlob(got!)));
        }
    }
}
