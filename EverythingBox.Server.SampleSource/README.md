# Sample plugin: a local folder

A complete, working `IMediaSource` — the shortest path to understanding the contract.

## Build and install

```bash
dotnet build EverythingBox.Server.SampleSource -c Release
```

Copy the build output into the server's `plugins/local/` directory. Copy **only**
`EverythingBox.Server.SampleSource.dll` and any dependencies of your own — never
`EverythingBox.Server.Abstractions.dll`. The host supplies that, and a second copy in
the plugin folder makes every cast fail at runtime.

## Configure

In `everythingbox-server.json`:

```jsonc
{
  "Plugins": {
    "local": {
      "Folders": ["D:\\Media\\Movies", "D:\\Media\\Music"]
    }
  }
}
```

## What to copy from it

- `Key` namespaces every id the source emits; the host prefixes and strips it.
- Ids are opaque to the host, so encode whatever you need — this one base64url-encodes a path.
- **Ids arrive from the client, so never trust one.** `ResolvePath` confirms the decoded
  path is inside a configured folder before opening anything. A plain lexical comparison
  (`Path.GetFullPath` plus a prefix check) is **not sufficient**: it collapses `..` and relative
  segments, but it does not resolve symlinks or directory junctions. A reparse point placed inside
  a configured folder — by anything with write access to it — that points outside is followed
  transparently by `File.Exists`/`File.OpenRead` while still looking lexically contained, so a
  naive check would serve a file physically outside every configured folder. `ResolvePath` walks
  the candidate's full ancestor chain (any directory in it, not just the leaf, can be the link)
  through `File.ResolveLinkTarget`/`Directory.ResolveLinkTarget` and checks containment against
  the *resolved* location, resolving the configured roots the same way. Omit this and you
  reintroduce the hole.
- **The same discipline applies to *listing*, not just opening.** `Directory.EnumerateFiles`
  follows junctions exactly as transparently as `File.Exists`/`File.OpenRead` did — a junction
  planted inside a configured folder makes `SearchAsync` enumerate a file physically outside every
  configured folder, with its real title and size, even if `ResolvePath` would later correctly
  refuse to open it. A source that only guards its open path still leaks that file's metadata into
  the catalog and advertises an item it can never actually serve. `SearchAsync` runs every
  enumerated path through the same resolved-path containment check `ResolvePath` uses (factored
  out as `IsContained`, not duplicated) and silently skips anything that fails it, the same way it
  skips a non-media extension. Guard the open path without also guarding the list path and you've
  fixed only half of it.
- Also note what a decoded id can do to a filesystem call that isn't gated by `File.Exists`
  first: `Path.GetFullPath` throws `ArgumentException` on some byte sequences that decode from
  base64 just fine (an empty string, one with an embedded NUL). Every other malformed-id case
  here returns `null` rather than throwing; a client-controlled string reaching any filesystem API
  needs the same guard around whatever that API documents itself as throwing — not a bare `catch
  (Exception)`, which would also hide a real bug.
- `ResolveAsync` returns a relative `proxy/...` path, and `OpenAsync` supplies the bytes.
  Return an absolute `https://` URL instead when the client can fetch it directly.
