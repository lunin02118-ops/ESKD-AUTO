# -*- coding: utf-8 -*-
"""Контактный лист из снимков: sheet.py out.jpg a.jpg b.jpg ... (обрезка белых полей, 2 в ряд)."""
import sys
from PIL import Image, ImageOps
out, files = sys.argv[1], sys.argv[2:]
ims = []
for f in files:
    im = Image.open(f).convert("L")
    box = ImageOps.invert(im).getbbox()
    im = im.crop(box)
    im.thumbnail((1400, 1000))
    ims.append(im)
cols = 2
w = max(i.width for i in ims); h = max(i.height for i in ims)
rows = (len(ims) + cols - 1) // cols
sheet = Image.new("L", (w * cols, h * rows), 255)
for n, im in enumerate(ims):
    sheet.paste(im, ((n % cols) * w, (n // cols) * h))
sheet.save(out, quality=85)
print(sheet.size)
