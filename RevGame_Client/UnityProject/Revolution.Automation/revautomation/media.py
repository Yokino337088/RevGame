"""证据媒体：把截图帧合成 GIF，并生成带图片/动图的 HTML 报告。

为什么自己写 GIF 编码（不用 Pillow）：
    这个包承诺"Python 3.10+ 标准库即可运行"，测试机上不一定装了 Pillow。
    GIF89a 只要求：全局调色板 + LZW + 图形控制块，实现量可控（约 150 行），
    换来的是"任何机器都能出动图"，不用为一张动图改环境。

颜色处理：
    统一量化到 3-3-2（R/G/B 各取高位，共 256 色）。游戏截图有渐变，
    会出现轻微色带，但换来做动图的零依赖与稳定体积，是划算的。
"""
import struct
import zlib
from pathlib import Path

# ---------------------------------------------------------------- PNG 解码

def _paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


def read_png(path):
    """读 8 位、非隔行的 PNG（Unity 的 EncodeToPNG 输出正是这种）。

    返回 (width, height, rows)；rows 是每行 bytes，RGB 三通道。
    遇到不支持的类型直接抛错——宁可明确失败，也不要产出一张糊图。
    """
    raw = Path(path).read_bytes()
    if raw[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("不是 PNG: %s" % path)

    pos = 8
    width = height = bit_depth = color_type = interlace = None
    idat = bytearray()
    while pos < len(raw):
        length = struct.unpack(">I", raw[pos:pos + 4])[0]
        kind = raw[pos + 4:pos + 8]
        data = raw[pos + 8:pos + 8 + length]
        pos += 12 + length
        if kind == b"IHDR":
            width, height, bit_depth, color_type, _, _, interlace = struct.unpack(">IIBBBBB", data)
        elif kind == b"IDAT":
            idat += data
        elif kind == b"IEND":
            break

    if bit_depth != 8 or interlace != 0 or color_type not in (2, 6):
        raise ValueError("只支持 8 位非隔行 RGB/RGBA PNG：bit=%s type=%s interlace=%s"
                         % (bit_depth, color_type, interlace))

    channels = 3 if color_type == 2 else 4
    stride = width * channels
    pixels = zlib.decompress(bytes(idat))
    rows = []
    prev = bytearray(stride)
    idx = 0
    for _ in range(height):
        ftype = pixels[idx]
        idx += 1
        line = bytearray(pixels[idx:idx + stride])
        idx += stride
        if ftype == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif ftype == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ftype == 3:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 0xFF
        elif ftype == 4:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                up_left = prev[i - channels] if i >= channels else 0
                line[i] = (line[i] + _paeth(left, prev[i], up_left)) & 0xFF
        if channels == 3:
            rows.append(bytes(line))
        else:  # RGBA：丢掉 alpha，重建 RGB 行（截图不需要透明通道）
            rgb = bytearray(width * 3)
            for x in range(width):
                rgb[x * 3:x * 3 + 3] = line[x * 4:x * 4 + 3]
            rows.append(bytes(rgb))
        prev = line
    return width, height, rows


def fit_rows(width, height, rows, max_width):
    """按最近邻缩到 max_width 以内；动图不需要原分辨率，缩小后体积可控。"""
    if max_width <= 0 or width <= max_width:
        return width, height, rows
    scale = width / float(max_width)
    new_w = max_width
    new_h = max(1, int(round(height / scale)))
    out = []
    for y in range(new_h):
        src = rows[min(height - 1, int(y * scale))]
        line = bytearray(new_w * 3)
        for x in range(new_w):
            sx = min(width - 1, int(x * scale)) * 3
            line[x * 3:x * 3 + 3] = src[sx:sx + 3]
        out.append(bytes(line))
    return new_w, new_h, out


def quantize_rows(width, height, rows):
    """3-3-2 量化，返回索引行；调色板由 _palette() 固定给出。"""
    out = []
    for row in rows:
        line = bytearray(width)
        for x in range(width):
            r, g, b = row[x * 3], row[x * 3 + 1], row[x * 3 + 2]
            line[x] = (r & 0xE0) | ((g & 0xE0) >> 3) | (b >> 6)
        out.append(bytes(line))
    return out


def _palette():
    table = bytearray()
    for i in range(256):
        r = ((i >> 5) & 0x07) * 255 // 7
        g = ((i >> 2) & 0x07) * 255 // 7
        b = (i & 0x03) * 255 // 3
        table += bytes((r, g, b))
    return bytes(table)


# ---------------------------------------------------------------- GIF 编码

def _lzw(indices, min_code_size=8):
    """GIF 变长 LZW。返回去掉子块封装的数据流。"""
    clear = 1 << min_code_size
    eoi = clear + 1
    code_size = min_code_size + 1
    next_code = eoi + 1
    table = {bytes((i,)): i for i in range(clear)}
    out = bytearray()
    bitbuf = 0
    bitcnt = 0

    def emit(code):
        nonlocal bitbuf, bitcnt
        bitbuf |= code << bitcnt
        bitcnt += code_size
        while bitcnt >= 8:
            out.append(bitbuf & 0xFF)
            bitbuf >>= 8
            bitcnt -= 8

    emit(clear)
    prefix = b""
    for byte in indices:
        cur = prefix + bytes((byte,))
        if cur in table:
            prefix = cur
            continue
        emit(table[prefix])
        table[cur] = next_code
        next_code += 1
        if next_code > (1 << code_size) and code_size < 12:
            code_size += 1
        if next_code >= 4096:
            emit(clear)
            table = {bytes((i,)): i for i in range(clear)}
            next_code = eoi + 1
            code_size = min_code_size + 1
        prefix = bytes((byte,))
    if prefix:
        emit(table[prefix])
    emit(eoi)
    if bitcnt:
        out.append(bitbuf & 0xFF)
    return bytes(out)


def _blocks(data):
    """把数据切成 <=255 的子块。"""
    out = bytearray()
    for i in range(0, len(data), 255):
        chunk = data[i:i + 255]
        out.append(len(chunk))
        out += chunk
    out.append(0)
    return bytes(out)


def write_gif(frame_paths, gif_path, delay_ms=120, max_width=640, loop=0):
    """把若干 PNG 合成为动图，返回写出信息（帧数、尺寸、字节数）。

    delay_ms 是每帧停留毫秒；GIF 用 1/100 秒为单位，所以按 10ms 取整，最小 20ms。
    """
    paths = [Path(p) for p in frame_paths]
    if not paths:
        raise ValueError("至少需要一帧才能生成 GIF")

    delay_cs = max(2, int(round(delay_ms / 10.0)))
    parts = [b"GIF89a"]
    width = height = None
    body = bytearray()

    for path in paths:
        w, h, rows = read_png(path)
        w, h, rows = fit_rows(w, h, rows, max_width)
        if width is None:
            width, height = w, h
            parts.append(struct.pack("<HHBBB", w, h, 0xF7, 0, 0))
            parts.append(_palette())
            if loop >= 0:
                parts.append(b"\x21\xFF\x0BNETSCAPE2.0\x03\x01" + struct.pack("<H", loop) + b"\x00")
        elif (w, h) != (width, height):
            w, h, rows = fit_rows(w, h, rows, width)
            if (w, h) != (width, height):
                raise ValueError("所有帧尺寸必须一致：期望 %dx%d，实际 %dx%d" % (width, height, w, h))

        indices = b"".join(quantize_rows(w, h, rows))
        body += b"\x21\xF9\x04" + bytes((0x04,)) + struct.pack("<H", delay_cs) + b"\x00\x00"
        body += b"\x2C" + struct.pack("<HHHH", 0, 0, w, h) + b"\x00"
        body += bytes((8,)) + _blocks(_lzw(indices))
    parts.append(bytes(body))
    parts.append(b"\x3B")

    data = b"".join(parts)
    target = Path(gif_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
    return dict(path=str(target), frames=len(paths), width=width, height=height,
                bytes=len(data), delayMs=delay_cs * 10)


def collect_media(directory, limit=None):
    """收集目录下可直接嵌入报告的图片与动图（按文件名排序，保证报告顺序稳定）。"""
    directory = Path(directory)
    images, animations = [], []
    for path in sorted(directory.glob("*")):
        suffix = path.suffix.lower()
        if suffix in (".png", ".jpg", ".jpeg"):
            images.append(path)
        elif suffix == ".gif":
            animations.append(path)
    if limit:
        images = images[:limit]
        animations = animations[:limit]
    return images, animations
