using System.Text.Json;

namespace EverythingBox.Server.MusicLibrary;

/// <summary>
/// The tiny slice of user state a music library needs but the scanned filesystem can't carry: which
/// songs/albums are starred, a listening (scrobble) history, and named playlists. This server has no
/// user model, so it is a single identity — one JSON file in the plugin cache dir. Every operation is
/// best-effort: a load or persist failure is swallowed (an IO fault must never escape into a request),
/// so a missing/locked/corrupt file just means empty-or-stale state, never a thrown 500. The file is
/// loaded once at construction and rewritten on each mutation with the temp-then-move discipline
/// <see cref="EverythingBox.Server.Abstractions.FileResolverCache"/> uses, so a concurrent reader
/// never sees a half-written file.
/// </summary>
public sealed class MusicStateStore
{
    private readonly string? _path;
    private readonly Lock _gate = new();

    // In-memory authoritative copy; the file is a durable mirror. Ordinal because ids are opaque tokens.
    // Stars are datetimes (id → the instant it was starred), so the Subsonic surface can render
    // starred="<ISO8601>"; a missing key means unstarred.
    private readonly Dictionary<string, DateTimeOffset> _starred = new(StringComparer.Ordinal);
    private readonly List<ScrobbleRow> _scrobbles = [];
    private readonly Dictionary<string, PlaylistRow> _playlists = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <param name="directory">The plugin cache dir the state file lives in; null disables persistence
    /// (the store still works in-memory for the lifetime of the instance).</param>
    public MusicStateStore(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            try { Directory.CreateDirectory(directory); } catch { /* best effort */ }
            _path = Path.Combine(directory, "music-state.json");
        }
        Load();
    }

    public bool IsStarred(string id)
    {
        lock (_gate) return _starred.ContainsKey(id);
    }

    /// <summary>The instant an id was starred, or null when it is not starred.</summary>
    public DateTimeOffset? StarredAt(string id)
    {
        lock (_gate) return _starred.TryGetValue(id, out var at) ? at : null;
    }

    /// <summary>Stars (records <c>now</c>) or unstars an id. Re-starring an already-starred id refreshes its
    /// timestamp; unstarring an unstarred id is a no-op. Persisted immediately (best-effort).</summary>
    public void SetStarred(string id, bool starred)
    {
        lock (_gate)
        {
            if (starred)
            {
                _starred[id] = DateTimeOffset.UtcNow;
                Persist();
            }
            else if (_starred.Remove(id))
            {
                Persist();
            }
        }
    }

    public void Scrobble(string songId, DateTimeOffset playedAt)
    {
        lock (_gate)
        {
            _scrobbles.Add(new ScrobbleRow { SongId = songId, PlayedAt = playedAt });
            Persist();
        }
    }

    /// <summary>The listening history, oldest first.</summary>
    public IReadOnlyList<(string SongId, DateTimeOffset PlayedAt)> Scrobbles()
    {
        lock (_gate) return _scrobbles.Select(s => (s.SongId, s.PlayedAt)).ToList();
    }

    public IReadOnlyList<(string Id, string Name, IReadOnlyList<string> SongIds)> Playlists()
    {
        lock (_gate)
            return _playlists.Values
                .Select(p => (p.Id, p.Name, (IReadOnlyList<string>)p.SongIds.ToList()))
                .ToList();
    }

    public (string Id, string Name, IReadOnlyList<string> SongIds)? Playlist(string id)
    {
        lock (_gate)
            return _playlists.TryGetValue(id, out var p)
                ? (p.Id, p.Name, (IReadOnlyList<string>)p.SongIds.ToList())
                : null;
    }

    /// <summary>Creates or replaces a playlist. Persisted immediately (best-effort).</summary>
    public void SavePlaylist(string id, string name, IReadOnlyList<string> songIds)
    {
        lock (_gate)
        {
            _playlists[id] = new PlaylistRow { Id = id, Name = name, SongIds = [.. songIds] };
            Persist();
        }
    }

    public void DeletePlaylist(string id)
    {
        lock (_gate)
        {
            if (_playlists.Remove(id)) Persist();
        }
    }

    // ---- persistence (best-effort; never throws) ----

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            var model = JsonSerializer.Deserialize<StateModel>(json, JsonOptions);
            if (model is null) return;

            foreach (var kv in model.Starred ?? []) _starred[kv.Key] = kv.Value;
            foreach (var s in model.Scrobbles ?? []) _scrobbles.Add(s);
            foreach (var p in model.Playlists ?? [])
                if (!string.IsNullOrEmpty(p.Id)) _playlists[p.Id] = p;
        }
        catch { /* missing/locked/corrupt → empty state, never a thrown request */ }
    }

    // Caller holds _gate.
    private void Persist()
    {
        if (_path is null) return;
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var model = new StateModel
            {
                Starred = new Dictionary<string, DateTimeOffset>(_starred, StringComparer.Ordinal),
                Scrobbles = [.. _scrobbles],
                Playlists = [.. _playlists.Values],
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(model, JsonOptions);
            // temp-then-move so a concurrent reader never sees a half-written file.
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* ignore */ }
        }
    }

    // ---- on-disk model ----

    private sealed class StateModel
    {
        // id → the instant it was starred. (Was a bare List<string> before stars became datetimes; an old
        // file in that shape simply fails to deserialize and degrades to empty state, like any other fault.)
        public Dictionary<string, DateTimeOffset>? Starred { get; set; }
        public List<ScrobbleRow>? Scrobbles { get; set; }
        public List<PlaylistRow>? Playlists { get; set; }
    }

    private sealed class ScrobbleRow
    {
        public string SongId { get; set; } = "";
        public DateTimeOffset PlayedAt { get; set; }
    }

    private sealed class PlaylistRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> SongIds { get; set; } = [];
    }
}
