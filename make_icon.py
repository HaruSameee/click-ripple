from math import hypot
from pathlib import Path
from struct import pack


def make_icon_image(size):
    pixels = bytearray()
    mask_stride = ((size + 31) // 32) * 4
    mask = bytearray(mask_stride * size)
    for y in range(size):
        for x in range(size):
            distance = hypot(x + 0.5 - size / 2, y + 0.5 - size / 2) / size
            outer = max(0, min(1, (0.069 - abs(distance - 0.327)) * size))
            inner = max(0, min(1, (0.064 - abs(distance - 0.155)) * size))
            edge = max(0, min(1, (0.107 - abs(distance - 0.33)) * size))
            alpha = max(outer, inner, edge * 0.78)
            bright = max(outer, inner * 0.85)
            pixels.extend((int(126 + 108 * bright), int(73 + 144 * bright),
                           int(19 + 94 * bright), round(alpha * 255)))
            if not alpha:
                mask[y * mask_stride + x // 8] |= 128 >> (x % 8)

    header = pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0)
    return header + pixels + mask


sizes = (16, 24, 32, 48, 64)
directory = bytearray(pack("<HHH", 0, 1, len(sizes)))
contents = bytearray()
for size in sizes:
    image = make_icon_image(size)
    offset = 6 + 16 * len(sizes) + len(contents)
    directory.extend(pack("<BBBBHHII", size, size, 0, 0, 1, 32, len(image), offset))
    contents.extend(image)
Path(__file__).with_name("ClickRipple.ico").write_bytes(directory + contents)
