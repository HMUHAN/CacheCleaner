"""QQ + 微信 图标：去米色背景，按尺寸裁切。"""
import io, os, struct
from PIL import Image, ImageDraw

HERE = os.path.dirname(__file__)
SRC = os.path.join(HERE, "..", "assets", "icon3", "wx.png")
OUT = os.path.join(HERE, "..", "assets", "QQCacheTray.ico")
PREVIEW = os.path.join(HERE, "..", "assets", "icon3")

CREAM = (254, 255, 243)
SENTINEL = (255, 0, 255)


def near(c, t=14):
    return abs(c[0] - CREAM[0]) <= t and abs(c[1] - CREAM[1]) <= t and abs(c[2] - CREAM[2]) <= t


def key_out_background(im):
    rgb = im.convert("RGB")
    w, h = rgb.size
    px = rgb.load()
    seeds = []
    step = 6
    for x in range(0, w, step):
        for y in (0, h - 1):
            if near(px[x, y]):
                seeds.append((x, y))
    for y in range(0, h, step):
        for x in (0, w - 1):
            if near(px[x, y]):
                seeds.append((x, y))

    for s in seeds:
        ImageDraw.floodfill(rgb, s, SENTINEL, thresh=12)

    out = im.convert("RGBA")
    src = rgb.load()
    dst = out.load()
    for y in range(h):
        for x in range(w):
            if src[x, y] == SENTINEL:
                dst[x, y] = (0, 0, 0, 0)
    return out


def square(img, size=1024, pad=0.05):
    bbox = img.getchannel("A").getbbox()
    if bbox:
        img = img.crop(bbox)
    iw, ih = img.size
    scale = (size * (1 - 2 * pad)) / max(iw, ih)
    nw, nh = max(1, int(round(iw * scale))), max(1, int(round(ih * scale)))
    img = img.resize((nw, nh), Image.LANCZOS)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.paste(img, ((size - nw) // 2, (size - nh) // 2), img)
    return canvas


def head_crop(img):
    bbox = img.getchannel("A").getbbox()
    img = img.crop(bbox)
    w, h = img.size
    return img.crop((int(w * 0.02), 0, int(w * 0.60), int(h * 0.52)))


def bmp_bits(img):
    w, h = img.size
    rgba = img.convert("RGBA").tobytes()
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        base = y * w * 4
        for x in range(w):
            i = base + x * 4
            row += bytes((rgba[i + 2], rgba[i + 1], rgba[i], rgba[i + 3]))
        rows.append(bytes(row))
    xor = b"".join(rows)
    stride = ((w + 31) // 32) * 4
    mask = b"\x00" * (stride * h)
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor), 0, 0, 0, 0)
    return header + xor + mask


def write_ico(path, frames, png_sizes=(256,)):
    blobs = []
    for size, img in frames:
        if size in png_sizes:
            buf = io.BytesIO()
            img.save(buf, "PNG", optimize=True)
            blobs.append((size, buf.getvalue()))
        else:
            blobs.append((size, bmp_bits(img)))
    header = struct.pack("<HHH", 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    entries = b""
    data = b""
    for size, blob in blobs:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
        data += blob
    with open(path, "wb") as f:
        f.write(header + entries + data)


def main():
    im = key_out_background(Image.open(SRC).convert("RGBA"))
    print("去背景 bbox:", im.getchannel("A").getbbox())

    full = square(im)
    head = square(head_crop(im), pad=0.02)
    os.makedirs(PREVIEW, exist_ok=True)
    full.resize((512, 512), Image.LANCZOS).save(os.path.join(PREVIEW, "full-512.png"))
    head.resize((512, 512), Image.LANCZOS).save(os.path.join(PREVIEW, "head-512.png"))

    frames = []
    for s in (16, 24, 32, 40):
        frames.append((s, head.resize((s, s), Image.LANCZOS)))
    for s in (48, 64, 128, 256):
        frames.append((s, full.resize((s, s), Image.LANCZOS)))

    write_ico(OUT, frames)
    print("写出:", OUT, os.path.getsize(OUT))

    zoom, pad = 8, 12
    chips = [(s, img.resize((s * zoom, s * zoom), Image.NEAREST)) for s, img in frames]
    cw = sum(c[1].width for c in chips) + pad * (len(chips) + 1)
    ch = max(c[1].height for c in chips) + pad * 2
    sheet = Image.new("RGB", (cw, ch), (232, 232, 232))
    x = pad
    for s, big in chips:
        sheet.paste(big, (x, pad), big)
        x += big.width + pad
    sheet.save(os.path.join(PREVIEW, "sheet.png"))


if __name__ == "__main__":
    main()
