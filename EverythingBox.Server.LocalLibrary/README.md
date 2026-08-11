# Local Library plugin

Serves media files already on the server's own disk as a browsable catalog. This increment
covers **movies**: it scans the configured folders, classifies each video file into a
`movies` catalog (title and year parsed from the filename), and streams the bytes through
the host's proxy route with full HTTP byte-range support (seeking, resume).

A fresh checkout serves nothing: with no folders configured the source declares no catalog
at all. Point it at your own folders to light it up.

## Build and install

```bash
dotnet build EverythingBox.Server.LocalLibrary -c Release
```

Copy the build output into the server's `plugins/locallib/` directory. Copy **only**
`EverythingBox.Server.LocalLibrary.dll` — never `EverythingBox.Server.Abstractions.dll`.
The host supplies the contract assembly, and a second copy in the plugin folder makes every
cast fail at runtime.

## Configure

In `everythingbox-server.json`:

```jsonc
{
  "Plugins": {
    "locallib": {
      "Movies": ["D:\\Media\\Movies", "E:\\More Movies"]
    }
  }
}
```

Each entry is an absolute path to a folder whose video files are treated as movies.
Subfolders are scanned recursively.

## Security

Nothing outside a configured folder is ever listed or served. Every id that arrives from a
client is decoded, resolved to where it *actually* points on disk — following every symlink
or directory junction in its ancestor chain, not just the leaf — and confirmed to live
inside a configured root (also resolved the same way) before a single byte is opened. A
lexical path check alone is not enough: a reparse point planted inside a configured folder
can point anywhere, and the filesystem follows it transparently. Listing enforces the same
discipline as opening, through one shared containment check, so the two can never diverge.
