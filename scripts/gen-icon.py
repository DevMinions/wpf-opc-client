#!/usr/bin/env python3
"""生成应用图标 dc.ico（多尺寸）。Fluent 风：accent 圆角方 + 数据脉冲波 + 采集节点。
运行：python3 scripts/gen-icon.py  → src/Dc.App/Assets/dc.ico
"""
import math
from PIL import Image, ImageDraw

OUT = "src/Dc.App/Assets/dc.ico"
SS = 8  # 超采样
BASE = 256
S = BASE * SS

ACCENT = (0, 103, 192, 255)      # #0067c0 Fluent accent
ACCENT_HI = (76, 194, 255, 255)  # #4cc2ff
WHITE = (255, 255, 255, 255)

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# 圆角方背景（accent 实心）
r = int(S * 0.22)
d.rounded_rectangle([0, 0, S - 1, S - 1], radius=r, fill=ACCENT)

# 顶部高光（轻微渐变感：叠一层半透明亮色斜带）
hi = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(hi).rounded_rectangle([0, 0, S - 1, int(S * 0.5)], radius=r, fill=(255, 255, 255, 28))
img = Image.alpha_composite(img, hi)
d = ImageDraw.Draw(img)

# 数据脉冲波（白色折线，带采集节点圆点）—— 贯穿中部
pad = int(S * 0.18)
midy = int(S * 0.56)
amp = int(S * 0.14)
# 一段「心跳/脉冲」式折线点
xs = [pad, pad + (S - 2 * pad) * 0.18, pad + (S - 2 * pad) * 0.30,
      pad + (S - 2 * pad) * 0.42, pad + (S - 2 * pad) * 0.54,
      pad + (S - 2 * pad) * 0.70, S - pad]
ys = [midy, midy, midy - amp, midy + amp, midy - int(amp * 1.6), midy, midy]
pts = list(zip([int(x) for x in xs], [int(y) for y in ys]))
d.line(pts, fill=WHITE, width=int(S * 0.045), joint="curve")

# 采集节点圆点（线上几个高亮点）
for (x, y) in [pts[2], pts[4], pts[6]]:
    rr = int(S * 0.035)
    d.ellipse([x - rr, y - rr, x + rr, y + rr], fill=ACCENT_HI, outline=WHITE, width=int(S * 0.012))

# 下采样到各尺寸 + 存 ICO
sizes = [256, 128, 64, 48, 32, 16]
frames = [img.resize((n, n), Image.LANCZOS) for n in sizes]
import os
os.makedirs(os.path.dirname(OUT), exist_ok=True)
frames[0].save(OUT, format="ICO", sizes=[(n, n) for n in sizes])
print("wrote", OUT, sizes)
