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
- `ResolveAsync` returns a relative `proxy/...` path, and `OpenAsync` supplies the bytes.
  Return an absolute `https://` URL instead when the client can fetch it directly.
