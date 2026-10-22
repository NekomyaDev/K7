# K7 Linux Flatpak

Builds a single-file `.flatpak` bundle for GitHub Releases from the self-contained
`dotnet publish` output. It is the only Linux package K7 ships (the `.deb` and the tarball
depended on the host GTK4 / WebKitGTK / GStreamer versions and were dropped), and a prebuilt
bundle, not a Flathub submission (that would rebuild from source inside the Sdk).

## Layout

| File | Role |
|---|---|
| `com.k7.maui.yml` | Manifest (app id `com.k7.maui`, GNOME Platform 49, prebuilt module) |
| `k7-wrapper.sh` | `/app/bin/k7` entry: WebKit sandbox opt-out, `proxy` stub on `PATH`, CA bundle |
| `proxy` | libproxy-style helper libvlc spawns; answers `direct://` so loopback HTTP stays direct |
| `com.k7.maui.desktop` | Menu entry + `k7://` handler (`MimeType=x-scheme-handler/k7`) |
| `com.k7.maui.metainfo.xml` | AppStream metadata (`@VERSION@` / `@DATE@` filled by the script) |

## Build

On Linux (Ubuntu 24.04, same as release CI):

```bash
bash tools/linux/bundle-libvlc.sh                      # libvlc 4 bundle, once per machine
dotnet publish src/Clients/MAUI/K7.Clients.MAUI.csproj \
  -f net10.0 -c Release -r linux-x64 --self-contained true \
  -p:K7PublishPlatform=linux -o /tmp/k7-linux
bash tools/linux/build-flatpak.sh /tmp/k7-linux 1.16.0 /tmp/K7-1.16.0-linux-x64.flatpak
flatpak install --user /tmp/K7-1.16.0-linux-x64.flatpak
flatpak run com.k7.maui 2>&1 | tee /tmp/k7.log
```

On WSL keep the publish dir and the output under `/tmp`: flatpak-builder wants its state,
repo and build dir on one filesystem (the script stages everything under `$TMPDIR`).
`K7_FLATPAK_RUNTIME_VERSION` overrides the GNOME runtime branch (default `49`).

## What the sandbox changes

- GNOME 49 sits on freedesktop 25.08: `ffmpeg-full` and `openh264` are gone, H.264 / AAC for
  WebKitGTK HLS and music come from the runtime's `codecs-extra` extension, installed
  automatically with the runtime (`flatpak list --runtime | grep codecs`).
- WebKit's nested bubblewrap sandbox cannot start inside Flatpak: the wrapper sets
  `K7_WEBKIT_DISABLE_SANDBOX=1`. Compositing stays on (MSE needs it).
- libvlc's HTTP access calls `vlc_getProxyUrl`, which spawns `proxy <url>` and trusts its
  output; empty output falls back to `$http_proxy`. `/app/bin/proxy` prints `direct://`.
- The runtime ships `libbz2.so.1` but not Ubuntu's `libbz2.so.1.0` soname, and no `libbsd` /
  `libmd`: `bundle-libvlc.sh` copies them into `libvlc/linux-x64/lib`. It drops `libva*`, the
  runtime GStreamer needs its own libva (`vaMapBuffer2`).
- The bundle must contain `video_output/libwdummy_plugin.so` (`libvlc_video_set_callbacks`
  selects `window=dummy`) and `access/libimem_plugin.so` (media callbacks: Direct Play is fed
  by `LinuxHttpRangeMediaInput`, .NET HTTP Range requests, because libvlc's own HTTP module
  cannot connect to the loopback proxy inside the sandbox).
- The wrapper picks `GSK_RENDERER=ngl`: the runtime GTK's Vulkan renderer aborts on live
  resize on software Vulkan (Hyper-V, WSLg, xrdp). `GSK_RENDERER=cairo` is the slow fallback.
  Under WSL (`/proc/version`) it also switches WebKit to its software path
  (`WEBKIT_DISABLE_DMABUF_RENDERER=1`); elsewhere, virtual machines included, the DMA-BUF
  renderer must stay on (disabling it on Hyper-V stalled Video.js HLS).
- `k7://`: Flatpak exports the desktop file to the host, so the client skips its per-user
  desktop entry (`LinuxPackaging.IsFlatpak`). Sign-in opens the browser through the OpenURI
  portal (`GIO_USE_PORTAL=1`). If a sandboxed browser cannot launch host apps for custom
  schemes, `K7_AUTH_REDIRECT=loopback` is the fallback (see developing docs).

## Troubleshooting

`flatpak run com.k7.maui 2>&1 | tee /tmp/k7.log`, then look for:

- `vlc bundle /app/lib/k7/libvlc/linux-x64 libs=N` at the first Direct Play; a
  `vlc bundle could not load ...` line names a missing runtime soname.
- `vlc libvlc version 4.x` then `vlc args ...`; a `vlc ctor options rejected` line means an
  instance option the bundle's modules do not know.
- `K7_VLC_DEBUG=1 flatpak run com.k7.maui` echoes every libvlc log line as `vlc-dbg ...`
  (module probing, http access, tls) when the lines below are not enough.
- `vlc http-range GET offset=0 status=206 length=...` at the first Direct Play, then
  `offset=<bytes>` lines on resume / seek: the .NET input and Range requests work. With
  `K7_VLC_ACCESS=http` the old path logs `vlc-proxy GET /direct range=...` instead; inside
  Flatpak it fails with `HTTP connection failure` (see releasing.md).
- HLS / music failing with GStreamer "Internal data stream error": `codecs-extra` missing
  (`flatpak install --user org.freedesktop.Platform.codecs-extra//25.08-extra`).
- HLS spinner: `web video probe hls+2s|6s|15s {...}` (every HLS start, F9 on demand) shows the
  Video.js side. Healthy: `player.sources` empty, `player.elCur` a `blob:` URL, `player.loader`
  past `HAVE_NOTHING`, `gtkQuirks:true`. A manifest URL in `elCur` with `net:3` means VHS took
  the Safari `<source>` path (`safari:true`). `stream fetch ... -> 200` lines are the bridge
  requests VHS makes through .NET (VHS never fetches HLS itself, `hlsRequests:0` is normal); a
  `stream fetch rejected` line names why one was refused. `log` holds the last console
  warnings / errors of the page.
