# Music library — Increment 3 (Subsonic API: auth, envelope, read endpoints) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Subsonic/OpenSubsonic `/rest` API serving the scanned music library — per-request `u/t/s` auth against the access token, an XML-default/`f=json` response envelope, and the read endpoints (`ping` … `search3`) — so real Subsonic clients can browse. Media + write endpoints are Increment 4.

**Architecture:** `MapSubsonic` mounts at bare `/rest/*` (NOT under the token path-prefix — Subsonic brings its own auth), gated on `config.Subsonic.Enabled` + an `IMusicLibrary` present. `SubsonicAuth` validates the token scheme against `ServerConfig.AccessToken`. `SubsonicResponse` builds a small `SubsonicNode` tree once per endpoint and renders it to XML or JSON so the two formats never drift. Endpoints map `IMusicLibrary` DTOs → nodes.

**Tech Stack:** .NET 9 / C#, ASP.NET minimal APIs, `System.Xml.Linq` (BCL — no dep), `System.Security.Cryptography.MD5` (BCL), xUnit + `WebApplicationFactory`. All in `EverythingBox.Server` + tests.

## Global Constraints

- **`/rest` is OUTSIDE the token path-prefix** and is the server's first per-request-authed surface — deliberate. Auth is Subsonic's own `u/t/s` (or legacy `p`), where the password == `ServerConfig.AccessToken`. An empty `AccessToken` = open (LAN), consistent with the tokenless model.
- **One object model, two formats.** Every endpoint builds `SubsonicNode`s; a single renderer emits XML (default) or JSON (`f=json`; `f=jsonp` wraps in `callback`). XML and JSON must never be hand-written per endpoint.
- **Single identity** — `u` is accepted but not distinguished (no user model). Document it.
- **Errors are Subsonic envelopes**, not raw 401/404 bodies (clients parse the envelope). Failed auth → code 40; not-found → code 70; missing param → code 10; wrong API version is not enforced.
- Opt-in `config.Subsonic.Enabled` (default false); routes unmapped when disabled or no music library. No `IMusicLibrary`/API change (Increment 2 shipped 1.17). PUBLIC repo cleanliness. Stage by explicit path; no `git add -A`; no AI attribution.

---

### Task 1: `SubsonicConfig` + auth + the dual-format envelope + `MapSubsonic` skeleton (ping/getLicense)

**Files:**
- Modify: `EverythingBox.Server/ServerConfig.cs` (add `Subsonic` + `SubsonicConfig { bool Enabled }`)
- Create: `EverythingBox.Server/Subsonic/SubsonicNode.cs` (the node model + XML/JSON renderer)
- Create: `EverythingBox.Server/Subsonic/SubsonicResponse.cs` (envelope + content negotiation + `IResult`)
- Create: `EverythingBox.Server/Subsonic/SubsonicAuth.cs` (validate against AccessToken)
- Create: `EverythingBox.Server/Subsonic/SubsonicEndpoints.cs` (`MapSubsonic` + ping/getLicense)
- Modify: `EverythingBox.Server/Program.cs` (map when enabled + IMusicLibrary present)
- Create: `EverythingBox.Server.Tests/SubsonicServerFactory.cs`, `EverythingBox.Server.Tests/SubsonicAuthEnvelopeTests.cs`

**Interfaces:**
- Produces: `SubsonicNode`, `SubsonicResponse.Ok(HttpRequest, SubsonicNode? payload)` / `.Error(HttpRequest, int code, string message)` → `IResult`; `SubsonicAuth.Authenticate(HttpRequest, string? accessToken) → bool`; `MapSubsonic(this WebApplication)`.

- [ ] **Step 1: `SubsonicConfig`** — add `public SubsonicConfig Subsonic { get; set; } = new();` + `public sealed class SubsonicConfig { public bool Enabled { get; set; } }` (mirror `SyncConfig`'s opt-in shape). Absent section → disabled.

- [ ] **Step 2: `SubsonicNode.cs`** — the format-neutral model + renderers:
```csharp
namespace EverythingBox.Server.Subsonic;

/// <summary>A Subsonic response element: a name, ordered string attributes, and child nodes. Rendered
/// to XML (attributes → XML attributes, children → child elements) OR JSON (attributes + single-child
/// groups → object properties; repeated child names → arrays) so the two formats stay identical.</summary>
public sealed class SubsonicNode(string name)
{
    public string Name { get; } = name;
    public List<(string Key, string Value)> Attributes { get; } = [];
    public List<SubsonicNode> Children { get; } = [];
    public string? Text { get; set; }   // rare: element text (e.g. an error message is an attribute, not text)

    public SubsonicNode Attr(string key, string? value) { if (value is not null) Attributes.Add((key, value)); return this; }
    public SubsonicNode Add(SubsonicNode child) { Children.Add(child); return this; }
}
```
Plus a static renderer: `ToXml(SubsonicNode root) → XElement` (attributes as `XAttribute`, children recursively) and `ToJson(SubsonicNode root) → object` producing the Subsonic-JSON shape — **the JSON rule that matters:** attributes become properties; children with the SAME name collapse into a JSON array under that name (e.g. many `<album>` → `"album":[ … ]`), a lone child becomes an object. (This asymmetry is the whole reason for one model → two renderers.) Numbers/bools stay strings in XML; in JSON, emit them as the raw string too — clients tolerate string values, and mixing typed/untyped is where per-endpoint drift creeps in; keep it uniformly string-valued for v1 (documented).

- [ ] **Step 3: `SubsonicResponse.cs`** — the envelope + negotiation:
```csharp
public static class SubsonicResponse
{
    public const string ApiVersion = "1.16.1";
    public const string ServerType = "EverythingBox";

    public static IResult Ok(HttpRequest req, SubsonicNode? payload) => Render(req, "ok", payload, null);
    public static IResult Error(HttpRequest req, int code, string message)
        => Render(req, "failed", new SubsonicNode("error").Attr("code", code.ToString()).Attr("message", message), null);
    // Render: wrap payload in <subsonic-response status version type serverVersion>; pick XML vs JSON on
    // req.Query["f"] ("json"/"jsonp" → JSON, else XML); for jsonp wrap in `req.Query["c"?]`… actually the
    // callback is `req.Query["callback"]`. Return Results.Content(xml, "application/xml") or
    // Results.Content(json, "application/json") / jsonp text/javascript. serverVersion = ServerApi.VersionString.
}
```
(Implement `Render` to build the `subsonic-response` root node with the status/version/type/serverVersion attributes + the payload child, then `SubsonicNode.ToXml`/`ToJson`. JSON top-level key is `"subsonic-response"`.)

- [ ] **Step 4: `SubsonicAuth.cs`**:
```csharp
public static class SubsonicAuth
{
    /// <summary>True when the request is authorised. Empty accessToken ⇒ open (LAN). Supports the token
    /// scheme t=md5(password+salt),s=salt and the legacy p=password (plain or "enc:"+hex), where the
    /// password is the server access token. Never logs any credential.</summary>
    public static bool Authenticate(HttpRequest req, string? accessToken)
    {
        var token = accessToken?.Trim();
        if (string.IsNullOrEmpty(token)) return true;                    // tokenless LAN = open
        var t = req.Query["t"].ToString(); var s = req.Query["s"].ToString();
        if (!string.IsNullOrEmpty(t) && !string.IsNullOrEmpty(s))
        {
            var expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(token + s))).ToLowerInvariant();
            return string.Equals(t, expected, StringComparison.OrdinalIgnoreCase);
        }
        var p = req.Query["p"].ToString();
        if (!string.IsNullOrEmpty(p))
        {
            if (p.StartsWith("enc:", StringComparison.Ordinal))
            {
                try { p = Encoding.UTF8.GetString(Convert.FromHexString(p[4..])); } catch { return false; }
            }
            return string.Equals(p, token, StringComparison.Ordinal);
        }
        return false;
    }
}
```

- [ ] **Step 5: `SubsonicEndpoints.cs` — `MapSubsonic` + ping/getLicense**:
```csharp
public static class SubsonicEndpoints
{
    public static void MapSubsonic(this WebApplication app)
    {
        // NOTE: no token prefix — Subsonic auth is per-request.
        app.MapMethods("/rest/{endpoint}", ["GET", "POST"], async (string endpoint, HttpContext http, IMusicLibrary music, ServerConfig config, CancellationToken ct) =>
        {
            var name = endpoint.EndsWith(".view", StringComparison.OrdinalIgnoreCase) ? endpoint[..^5] : endpoint;
            if (!SubsonicAuth.Authenticate(http.Request, config.AccessToken))
                return SubsonicResponse.Error(http.Request, 40, "Wrong username or password.");
            return name switch
            {
                "ping" => SubsonicResponse.Ok(http.Request, null),
                "getLicense" => SubsonicResponse.Ok(http.Request, new SubsonicNode("license").Attr("valid", "true")),
                // read endpoints added in Task 2, media in Increment 4:
                _ => SubsonicResponse.Error(http.Request, 0, $"Endpoint not implemented: {name}"),
            };
        });
    }
}
```
(Clients hit either `/rest/ping` or `/rest/ping.view` — strip a trailing `.view`. `IMusicLibrary` is resolved from DI; if the route is mapped, it is present — see Step 6.)

- [ ] **Step 6: `Program.cs`** — map only when enabled AND a music library exists (resolving `IMusicLibrary` here forces the lazy plugin load — use `GetService`, never `GetRequiredService`):
```csharp
if (config.Subsonic.Enabled && app.Services.GetService<IMusicLibrary>() is not null)
    app.MapSubsonic();
```
(Place it near `if (config.Sync.Enabled) app.MapSync(prefix);`. Note `MapSubsonic` takes NO prefix.)

- [ ] **Step 7: `SubsonicServerFactory.cs`** — mirror `SyncServerFactory`: write a config with `AccessToken` set + `"Subsonic": { "Enabled": true }`, and stage a music plugin over a temp tagged tree (reuse the music fixture synthesis; the factory needs the built `musiclib` plugin staged with ATL.dll — mirror how the plugin-loading factories stage `testplugins`, and point a `Roots` config at a temp folder of synthesized tracks). Its own non-parallel `[CollectionDefinition]`.

- [ ] **Step 8: `SubsonicAuthEnvelopeTests.cs`** — `CreateClient()` against `/rest/...`:
  - **auth:** `/rest/ping.view?u=x&t=<md5(token+salt)>&s=<salt>` → 200 ok envelope; a wrong `t` → `failed` code 40; legacy `p=<token>` and `p=enc:<hex>` accepted; with a tokenless config (separate factory or assert the open path) any creds pass.
  - **envelope negotiation:** default (no `f`) → XML `<subsonic-response status="ok" version="1.16.1" type="EverythingBox">`; `f=json` → `{"subsonic-response":{"status":"ok",…}}`; `getLicense` shows `valid="true"` (XML) / `"valid":"true"` (JSON).
  - a request to a disabled-Subsonic server (a factory without the section) → the `/rest` route does not exist (404, not an envelope).

- [ ] **Step 9: Build + test + commit**
Run: `dotnet test EverythingBox.Server.Tests -v minimal` + `dotnet test EverythingBox.Server.Core.Tests -v minimal` — green.
```bash
git add EverythingBox.Server/ServerConfig.cs EverythingBox.Server/Subsonic/SubsonicNode.cs EverythingBox.Server/Subsonic/SubsonicResponse.cs EverythingBox.Server/Subsonic/SubsonicAuth.cs EverythingBox.Server/Subsonic/SubsonicEndpoints.cs EverythingBox.Server/Program.cs EverythingBox.Server.Tests/SubsonicServerFactory.cs EverythingBox.Server.Tests/SubsonicAuthEnvelopeTests.cs
git commit -m "feat: Subsonic /rest — auth (u/t/s vs access token), XML/JSON envelope, ping/getLicense"
```

---

### Task 2: the read endpoints (getMusicFolders … search3)

**Files:**
- Modify: `EverythingBox.Server/Subsonic/SubsonicEndpoints.cs` (the `switch` + node builders)
- Create: `EverythingBox.Server.Tests/SubsonicReadTests.cs`

**Interfaces:**
- Consumes: `IMusicLibrary` (Increment 2), `SubsonicNode`/`SubsonicResponse` (Task 1).

- [ ] **Step 1: node builders** (private static, DTO → `SubsonicNode`), following the Subsonic element shapes:
  - `Artist(ArtistInfo a)` → `<artist id name albumCount coverArt?>`.
  - `Album(AlbumInfo a)` → `<album id name artist artistId coverArt? songCount duration year? genre?>` (getAlbumList2/getArtist shape; for `getAlbum` the same element carries child `<song>`s).
  - `Song(SongInfo s)` → `<song id parent title album artist artistId albumId coverArt? duration? track? discNumber? year? genre? suffix contentType isDir="false" type="music">`.
  - `Genre(GenreInfo g)` → `<genre songCount albumCount>` with the genre name as element TEXT (Subsonic quirk: genre value is the text node) — set `.Text = g.Name`.
- [ ] **Step 2: the endpoints** in the `switch` (each reads query params off `http.Request.Query`, calls `IMusicLibrary`, builds a payload node, wraps in `SubsonicResponse.Ok`; a bad/unknown id → `Error(70, "not found")`):
  - `getMusicFolders` → `<musicFolders>` of `<musicFolder id name>`.
  - `getIndexes` / `getArtists` → `<artists>`/`<indexes>` with `<index name="A">` groups (first letter of each artist, uppercased; non-letters under `#`) each holding `<artist>`s. (getArtists → `<artists>`; getIndexes → `<indexes>` — same grouping, both fine to serve identically-shaped.)
  - `getArtist(id)` → `<artist …>` + child `<album>`s (from `IMusicLibrary.Artist(id)`); unknown → 70.
  - `getAlbum(id)` → `<album …>` + child `<song>`s (from `Album(id)`); unknown → 70.
  - `getSong(id)` → `<song …>`; unknown → 70.
  - `getAlbumList2` → `<albumList2>` of `<album>`; params `type` (default `alphabeticalByName`), `size` (default 10, cap 500), `offset` (default 0), `genre`, `fromYear`/`toYear` → `IMusicLibrary.AlbumList(...)`.
  - `getGenres` → `<genres>` of `<genre>` (from `Genres()`).
  - `search3` → `<searchResult3>` with `<artist>`/`<album>`/`<song>` from `Search(query, artistCount, albumCount, songCount)` (defaults 20/20/20; `query` may be `""` for browse-all — cap).
  - `getRandomSongs` → `<randomSongs>` of `<song>`; `size` (default 10), `genre` → `RandomSongs(size, genre)`.
- [ ] **Step 3: `SubsonicReadTests.cs`** — over the `SubsonicServerFactory` library (synthesized tagged tracks incl. a compilation + genres):
  - `getArtists` groups the artists under first-letter indexes incl. "Various Artists"; `getArtist(id)` lists its albums; `getAlbum(id)` lists its songs with `suffix`/`contentType`/`duration`; `getSong(id)` returns one song; `getAlbumList2?type=alphabeticalByName` orders; `type=byGenre&genre=…` filters; `getGenres` lists the genres with counts; `search3?query=<name>` finds the artist/album/song; an unknown `getAlbum?id=nope` → `failed` code 70. Assert against BOTH XML and `f=json` for at least `getArtist` + `getAlbum` (proving the dual renderer).
- [ ] **Step 4: Full suites + commit**
Run both test projects — green.
```bash
git add EverythingBox.Server/Subsonic/SubsonicEndpoints.cs EverythingBox.Server.Tests/SubsonicReadTests.cs
git commit -m "feat: Subsonic read endpoints — getArtists/getArtist/getAlbum/getSong/getAlbumList2/getGenres/search3/getRandomSongs"
```

---

## Self-review

**Spec coverage (Increment 3):** `/rest` outside the token prefix + `u/t/s`-vs-access-token auth + tokenless-open (spec) → Task 1 Steps 4-6, 8. XML-default/`f=json` envelope from one model (spec) → Task 1 Steps 2-3. Opt-in `Subsonic.Enabled` + gated on IMusicLibrary (spec) → Task 1 Steps 1,6. Read endpoints ping…search3 + getGenres (spec) → Task 1 Step 5 + Task 2. Errors as envelopes (spec) → `SubsonicResponse.Error`. No API bump (spec) → nothing touches ServerApi/Abstractions. ✅

**Placeholder scan:** auth + envelope + node model are complete code; the per-endpoint node shapes carry the exact Subsonic element/attribute names (the one novelty — genre-as-text is called out); the DI gating + `.view` stripping are concrete.

**Type consistency:** `SubsonicNode`/`SubsonicResponse.Ok|Error`/`SubsonicAuth.Authenticate` defined Task 1, used by the endpoints Task 2. `IMusicLibrary` (Artists/Artist/Album/Song/AlbumList/Genres/Search/RandomSongs/Folders) from Increment 2 consumed by the node builders. `config.Subsonic.Enabled` read in `Program.cs`. `ServerApi.VersionString` unchanged (1.17). ✅
