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
  path is inside a configured folder before opening anything.
- `ResolveAsync` returns a relative `proxy/...` path, and `OpenAsync` supplies the bytes.
  Return an absolute `https://` URL instead when the client can fetch it directly.
