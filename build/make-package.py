"""Packs dist/Wallsets.zip for passing Wallsets on: the self-contained program, the playback engine,
README and licence notes, and a "Тестовый набор" with a white and a black picture.
No user sets, caches, logs, settings or debug files go in, so a fresh copy starts with the defaults."""
import pathlib, struct, sys, zipfile, zlib

root, exe, target = (pathlib.Path(a) for a in sys.argv[1:4])
app = root / "Wallsets"


def png(rgb, width=1920, height=1080):
    raw = (b"\x00" + bytes(rgb) * width) * height
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def text(path):
    # Windows line endings and a BOM, so every Notepad shows the Russian text correctly.
    return "﻿" + path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").replace("\n", "\r\n")


engine = app / "engine" / "mpv.exe"
if engine.stat().st_size < 1_000_000:
    sys.exit("engine/mpv.exe is a Git LFS pointer: run 'git lfs pull' first")
target.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    z.write(exe, "Wallsets/Wallsets.exe")
    for name in ["mpv.exe", "d3dcompiler_43.dll", "LICENSE.GPL", "mpv/fonts.conf"]:
        z.write(app / "engine" / name, "Wallsets/engine/" + name)
    for name in ["README.txt", "THIRD-PARTY.txt"]:
        z.writestr("Wallsets/" + name, text(app / name).encode("utf-8"))
    z.writestr("Wallsets/Наборы/Тестовый набор/Белый фон.png", png((255, 255, 255)))
    z.writestr("Wallsets/Наборы/Тестовый набор/Чёрный фон.png", png((0, 0, 0)))
print(target, target.stat().st_size // 1048576, "MB")
