# /// script
# requires-python = ">=3.10"
# dependencies = ["pillow", "numpy", "scipy", "potracer"]
# ///
"""把 assets/pu~.png 的手绘噗噗描成矢量，按「气 / 身体 / 眼 / 腮红 / 嘴」拆层输出 JSON。

为什么拆层：不同状态只换脸和气，身体笔触保持原作；同一份路径数据
同时喂给网页（内联 SVG）和 WPF（Path.Data），两端长得一模一样。

用法（仓库根目录）：uv run tools/mascot/trace.py
产物：tools/mascot/traced.json（坐标已归一到 128×128 画布）
"""
import json
from pathlib import Path

import numpy as np
import potrace
from PIL import Image
from scipy import ndimage

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "assets" / "pu~.png"
OUT = Path(__file__).resolve().parent / "traced.json"

# 原图 1254px 上噗噗的包围盒约 x355-951 / y271-938；取 700px 见方画布居中，再缩到 128
CANVAS = 700
CX, CY = 653, 604
SCALE = 128 / CANVAS


def load_ink():
    rgb = np.asarray(Image.open(SRC).convert("RGB")).astype(int)
    # 墨水是圆珠笔蓝：蓝通道明显高于红通道且不亮
    return (rgb[..., 2] - rgb[..., 0] > 60) & (rgb.sum(-1) < 500)


def split_mouth(body):
    """嘴巴和底线粘在一起：逐列只保留最底下一段底线（厚度按嘴巴两侧的底线估计），其余算嘴。"""
    x0, x1, ytop = 553, 766, 780
    ys = np.arange(body.shape[0])

    def bottom_run(col):
        idx = ys[col]
        if idx.size == 0:
            return None
        end = idx.max()
        start = end
        while start - 1 >= 0 and col[start - 1]:
            start -= 1
        return start, end

    # 嘴巴左右两侧的底线厚度
    thick = []
    for x in list(range(x0 - 30, x0 - 5)) + list(range(x1 + 5, x1 + 30)):
        r = bottom_run(body[:, x])
        if r:
            thick.append(r[1] - r[0] + 1)
    t = int(np.median(thick))

    mouth = np.zeros_like(body)
    for x in range(x0, x1):
        col = body[:, x].copy()
        r = bottom_run(col)
        if not r:
            continue
        keep_from = max(r[0], r[1] - t + 1)
        m = col.copy()
        m[keep_from:] = False
        m[:ytop] = False
        mouth[:, x] = m
    body = body & ~mouth
    # 切口处略修圆，避免底线上留毛刺
    body = ndimage.binary_closing(body, structure=np.ones((5, 5)))
    return body, mouth


def trace(mask):
    """potrace 描边 → 归一化后的 SVG path d（外轮廓 + 洞，evenodd 填充）。"""
    # potracer 把 False 当墨迹（与 PIL 位图习惯一致），这里取反
    bmp = potrace.Bitmap(~mask)
    plist = bmp.trace(turdsize=8, alphamax=1.0, opticurve=True, opttolerance=0.3)

    def p(pt):
        x = (pt.x - (CX - CANVAS / 2)) * SCALE
        y = (pt.y - (CY - CANVAS / 2)) * SCALE
        return f"{x:.1f} {y:.1f}"

    d = []
    for curve in plist:
        d.append("M" + p(curve.start_point))
        for seg in curve.segments:
            if seg.is_corner:
                d.append("L" + p(seg.c) + "L" + p(seg.end_point))
            else:
                d.append("C" + p(seg.c1) + " " + p(seg.c2) + " " + p(seg.end_point))
        d.append("Z")
    return "".join(d)


def main():
    ink = load_ink()
    lab, _ = ndimage.label(ink)
    comps = ndimage.find_objects(lab)

    def pick(pred):
        m = np.zeros_like(ink)
        for i, sl in enumerate(comps, 1):
            if (lab[sl] == i).sum() < 20:
                continue
            y, x = sl
            if pred(x.start, x.stop, y.start, y.stop):
                m |= lab == i
        return m

    steam = pick(lambda x0, x1, y0, y1: y1 < 370)
    big = pick(lambda x0, x1, y0, y1: (x1 - x0) > 400)
    eyes = pick(lambda x0, x1, y0, y1: 640 < y0 < 700 and (x1 - x0) < 40)
    cheeks = pick(lambda x0, x1, y0, y1: 730 < y0 < 780 and (x1 - x0) < 45)
    body, mouth = split_mouth(big)

    layers = {name: trace(m) for name, m in
              [("steam", steam), ("body", body), ("eyes", eyes), ("cheeks", cheeks), ("grin", mouth)]}
    # 笔画粗细（归一化后）：新画的脸用同样粗细的描边，才接得上原作笔触
    dt = ndimage.distance_transform_edt(body)
    skeleton_w = float(np.median(dt[dt > 0][dt[dt > 0] > np.percentile(dt[dt > 0], 60)]) * 2)
    layers["_strokeWidth"] = round(skeleton_w * SCALE, 2)
    OUT.write_text(json.dumps(layers, ensure_ascii=False, indent=1), encoding="utf-8")
    print("stroke", layers["_strokeWidth"], {k: len(v) for k, v in layers.items() if k[0] != "_"})


if __name__ == "__main__":
    main()
