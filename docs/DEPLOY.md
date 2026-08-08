# Deploying

This is a plugin-agnostic guide to running EverythingBoxServer in production: how to
publish it, how to lay out plugins next to it, how it finds its config, and how to keep
it running. It names no plugin — see [README.md](../README.md) for what a plugin is and
how one is built.

## 1. Publish

```bash
dotnet publish EverythingBox.Server -c Release -o out
```

`out/` now holds `EverythingBox.Server.exe` (or the extension-less binary on Linux/macOS)
plus its own dependency DLLs. That folder is what you copy to the target machine — there
is nothing else to build.

## 2. Lay out plugins

The published server has no source of its own; everything it can browse or stream comes
from what you drop into its `plugins/` folder (see the "Plugins" section of the README
for the `IPlugin` contract). Next to the executable:

```
out/
  EverythingBox.Server.exe
  plugins/
    <yourplugin>/
      <YourPlugin>.dll
      <YourPlugin's dependency>.dll
      ...
```

Each subfolder of `plugins/` is one plugin, keyed by its `IPlugin.Key`; `PluginHost`
loads every `*.dll` it finds directly inside that folder (not recursively) into its own
`AssemblyLoadContext`, so one plugin's dependencies cannot collide with another's or with
the host's. That means the folder needs the plugin's own build output — its DLL plus
every managed DLL it depends on — not just the plugin assembly by itself.

**Flatten any native DLL beside the managed ones.** A plugin's load context resolves
managed dependencies via the plugin's own `.deps.json`, but it does not add
`runtimes/<rid>/native/` (the layout `dotnet publish` normally produces for a
native-dependent package) to the native library search path — only the managed
assembly's own directory is probed for unmanaged DLLs. If a plugin's publish output put a
native dependency under `runtimes/win-x64/native/foo.dll`, copy `foo.dll` up next to
`<YourPlugin>.dll` itself; leaving it under `runtimes/...` means the plugin loads but
fails the first time it actually calls into that native library.

Restart the server after adding, updating, or removing a plugin folder — plugins are
discovered once, at startup.

## 3. Configure

The server reads `everythingbox-server.json` next to the executable by default. Point it
elsewhere with the `EBS_CONFIG` environment variable (a full path to the file, not a
directory). If neither the default file nor `EBS_CONFIG` resolves to an existing file,
the server starts with all-default config rather than failing — see the README's
"Configuration" section for the full shape of that file (`Listen`, `AccessToken`,
`Indexers`, `Debrid`, `DownloadClient`, `Download`, `Ranking`, and the opaque
`Plugins.<key>` section below).

A few settings matter specifically for a production deploy:

- **`EBS_PLUGINS_DIR`** — overrides where the server looks for the `plugins/` folder
  described above (it otherwise defaults to `plugins` next to the executable, or the
  config file's `PluginsDirectory` if set). Point this at a plugins root that lives
  outside the publish output if you want plugin installs to survive re-publishing the
  server itself.
- **`Listen`** — the URL(s) the server binds, e.g. `"http://0.0.0.0:7000"`. Set this to
  the port you want exposed; nothing else in this doc assumes a particular value.
- **`Plugins.<key>`** — one opaque JSON section per installed plugin, keyed by that
  plugin's `IPlugin.Key`. The server does not interpret this section itself; each plugin
  binds its own key's section to its own config type via `IPluginContext.GetConfig<T>()`.
  Consult the plugin's own documentation for what belongs under its key.
- **`AccessToken`** — set this before exposing the server past a trusted LAN. It becomes
  a URL path prefix (`/<token>/manifest.json` and every route under it), so a request
  without the token cannot reach anything. Startup logs a warning if it is left unset.

`EBS_FILES_DIR` (defaults to `files` next to the executable) works the same way as
`EBS_PLUGINS_DIR`, for the self-download fallback's cache — set it if you want generated
files to live outside the publish output too.

## 4. Run it, and keep it running

`out/EverythingBox.Server.exe` (or `dotnet out/EverythingBox.Server.dll` if you published
framework-dependent) is a normal long-running process — it does not daemonize itself, so
whatever starts it is responsible for restarting it if it exits and for starting it again
on reboot. Pick whichever fits how the rest of the machine is managed; none of this is
specific to this server:

- **Windows service** — wrap the published exe with a service manager (e.g. `sc.exe
  create`, or a small wrapper like NSSM) so it starts on boot and restarts on crash
  under the Windows service control manager, without a logged-in session.
- **Windows Startup shortcut** — for a single-user desktop machine, a shortcut to the
  exe in the current user's Startup folder is enough to bring it back after a reboot or
  sign-in; it does not survive a crash on its own.
- **systemd (Linux)** — a unit file with `ExecStart=` pointing at the published binary,
  `Restart=on-failure`, and `WantedBy=multi-user.target` covers both boot-start and
  crash-restart; set `EBS_CONFIG`/`EBS_PLUGINS_DIR` in the unit's `Environment=` lines if
  you're not using the defaults.
- **A process supervisor** (pm2, supervisord, a container orchestrator's restart policy,
  etc.) — if the rest of your stack already uses one, add this process to it the same way
  as anything else long-running; there is nothing about this server that needs special
  handling beyond the environment variables and port above.

None of the above is checked into this repository — pick the one that matches your
platform and wire it up on the deploy target itself.
