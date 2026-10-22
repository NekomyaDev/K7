#!/bin/sh
# Flatpak entry point (/app/bin/k7). The published binaries live under /app/lib/k7.
set -eu
APP_DIR="/app/lib/k7"

# WebKitGTK's nested bubblewrap sandbox cannot start inside Flatpak (no nested user
# namespaces). Media still works without it, same opt-out as WSLg.
export K7_WEBKIT_DISABLE_SANDBOX="${K7_WEBKIT_DISABLE_SANDBOX:-1}"

# libvlc access/http asks the `proxy` helper (libproxy) for every URL and uses whatever it
# prints; the runtime helper may return a proxy for the loopback VlcAuthProxy ("HTTP
# connection failure"). /app/bin/proxy prints "direct://" (empty output is NOT "no proxy"
# for VLC: it then falls back to $http_proxy). Keep it first on PATH.
export PATH="/app/bin:${PATH:-/usr/bin}"
unset http_proxy HTTP_PROXY https_proxy HTTPS_PROXY all_proxy ALL_PROXY 2>/dev/null || true

# The bundled libvlc gnutls module reads the runtime CA bundle (HTTPS origins).
if [ -f /etc/ssl/certs/ca-certificates.crt ]; then
  export SSL_CERT_FILE="${SSL_CERT_FILE:-/etc/ssl/certs/ca-certificates.crt}"
  export SSL_CERT_DIR="${SSL_CERT_DIR:-/etc/ssl/certs}"
fi

# GSK renderer: the runtime GTK defaults to Vulkan, which aborts on live window resize on
# software Vulkan (Hyper-V / WSLg / xrdp guests on lavapipe). The GL renderer, GTK's default
# until 4.18, is stable everywhere; GSK_RENDERER=cairo is the slow fallback.
export GSK_RENDERER="${GSK_RENDERER:-ngl}"

# WSL only (/mnt/wslg is not mounted inside Flatpak, so detect it from the kernel banner):
# WebKit's software path instead of the DMA-BUF renderer. Do not extend this to hypervisor
# guests: on Hyper-V the DMA-BUF renderer works and disabling it stalls Video.js HLS.
if grep -qi microsoft /proc/version 2>/dev/null; then
  export WEBKIT_DISABLE_DMABUF_RENDERER="${WEBKIT_DISABLE_DMABUF_RENDERER:-1}"
fi

# http(s) links (OIDC sign-in) go through the OpenURI portal.
export GIO_USE_PORTAL="${GIO_USE_PORTAL:-1}"

# Do not set WEBKIT_DISABLE_COMPOSITING_MODE here: MSE / HLS video needs compositing.
# Do not put the libvlc bundle on LD_LIBRARY_PATH: the app dlopens it by full path, and a
# bundle-first search path would shadow the runtime libraries WebKit / GStreamer use.

cd "$APP_DIR"
exec "$APP_DIR/K7.Clients.MAUI" "$@"
