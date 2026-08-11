# Local Library plugin

Serves media files already on the server's own disk as a browsable catalog.

**Movies:** it scans the configured movie folders, classifies each video file into a
`movies` catalog (title and year parsed from the filename), and streams the bytes through
the host's proxy route with full HTTP byte-range support (seeking, resume).

**Series:** each immediate subfolder of a configured series root is listed as an expandable
show in a `series` catalog (title parsed from the folder name). Expanding a show flattens its
files into episodes titled `SxxEyy`, ordered by season then episode; a file with no `SxxEyy`
in its name is skipped. Episodes stream through the same proxy route with byte-range support.

A fresh checkout serves nothing: with no folders configured the source declares no catalog
at all, and each shelf appears only for a root kind that is actually configured. Point it at
your own folders to light it up.

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
      "Movies": ["D:\\Media\\Movies", "E:\\More Movies"],
      "Series": ["D:\\Media\\TV"]
    }
  }
}
```

Each `Movies` entry is an absolute path to a folder whose video files are treated as movies;
subfolders are scanned recursively. Each `Series` entry is an absolute path to a folder laid
out as `Show/Season NN/…` — every immediate subfolder is one show, expanded into its episodes
on demand.

## Security

Nothing outside a configured folder is ever listed or served. Every id that arrives from a
client is decoded, resolved to where it *actually* points on disk — following every symlink
or directory junction in its ancestor chain, not just the leaf — and confirmed to live
inside a configured root (also resolved the same way) before a single byte is opened. A
lexical path check alone is not enough: a reparse point planted inside a configured folder
can point anywhere, and the filesystem follows it transparently. Listing enforces the same
discipline as opening, through one shared containment check, so the two can never diverge.
