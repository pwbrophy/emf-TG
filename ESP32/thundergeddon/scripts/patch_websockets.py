# -*- coding: utf-8 -*-
#
# patch_websockets.py
#
# PlatformIO pre-build hook. Patches ArduinoWebsockets so the WebSocket upgrade
# request's Host header includes the port (e.g. "192.168.1.10:8080" instead of
# just "192.168.1.10"). WebSocketSharp (used by the Unity server) rejects the
# bare-host form with HTTP 400 on any non-standard port.
#
# Without this patch, robots build and run but cannot connect to the Unity
# server. The failure mode is silent at build time.
#
# Idempotent: re-running on an already-patched file is a no-op.
# Anchored to ArduinoWebsockets 0.5.4 (commit 26eecea). If the upstream library
# is bumped, the anchor line below may need updating.
#
# Wired in platformio.ini via:
#   extra_scripts = pre:scripts/patch_websockets.py

import os
import sys

Import("env")  # noqa: F821  (provided by SCons / PlatformIO)

TARGET_REL = os.path.join("ArduinoWebsockets", "src", "websockets_client.cpp")

ALREADY_PATCHED_MARKER = "WSString hostHeader = internals::fromInterfaceString(host)"

UNPATCHED_LINE = (
    "        auto handshake = generateHandshake("
    "internals::fromInterfaceString(host), "
    "internals::fromInterfaceString(path), _customHeaders);"
)

PATCHED_BLOCK = (
    "        // Include port in Host header - WebSocketSharp requires \"host:port\" for\n"
    "        // non-standard ports (not 80/443), otherwise it rejects with 400.\n"
    "        WSString hostHeader = internals::fromInterfaceString(host) + \":\" + std::to_string(port);\n"
    "        auto handshake = generateHandshake(hostHeader, internals::fromInterfaceString(path), _customHeaders);"
)


def _target_path():
    libdeps_dir = env["PROJECT_LIBDEPS_DIR"]  # noqa: F821
    pio_env = env["PIOENV"]                   # noqa: F821
    return os.path.join(libdeps_dir, pio_env, TARGET_REL)


def apply_patch(*args, **kwargs):
    path = _target_path()

    if not os.path.exists(path):
        # Library may not be downloaded yet on the very first build pass.
        # We register this as a pre-build action as well, so it'll be retried.
        print("[patch_websockets] target not present yet, will retry: {}".format(path))
        return

    with open(path, "r", encoding="utf-8") as f:
        content = f.read()

    if ALREADY_PATCHED_MARKER in content:
        return  # already patched, silent no-op on repeat builds

    if UNPATCHED_LINE not in content:
        print("[patch_websockets] ERROR: anchor line not found in {}".format(path))
        print("[patch_websockets] upstream ArduinoWebsockets may have changed; "
              "update UNPATCHED_LINE in this script.")
        sys.exit(1)

    new_content = content.replace(UNPATCHED_LINE, PATCHED_BLOCK, 1)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(new_content)

    print("[patch_websockets] patched {}".format(path))


# Try at script-load time (covers the common case where libdeps already exist).
apply_patch()

# Safety net: also run before linking, so a fresh download still gets patched.
env.AddPreAction("$BUILD_DIR/${PROGNAME}.elf", apply_patch)  # noqa: F821
