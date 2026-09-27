# /// script
# requires-python = ">=3.10"
# ///
"""由 traced.json（原作描摹）+ 下面手写的表情，拼出 web/mascot.svg。

一个 SVG 里放齐所有表情层，页面用 data-face 属性切换（CSS 控制显隐），
这样换状态不用重新请求、也能对单层做动画。颜色全走 currentColor /
CSS 变量，亮暗主题只换变量。

坐标系：128×128。原作参考点——左眼 (49.5,77)、右眼 (81.5,77)、
嘴中心 (65,106)、头顶尖 (57,21)、原作笔画粗细约 4.75。

用法（仓库根目录）：uv run tools/mascot/build.py
"""
import json
import re
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
T = json.loads((HERE / "traced.json").read_text(encoding="utf-8"))

# 新画的线条比原作略细：原作是填充轮廓（圆珠笔压出来的粗细变化），
# 描边等宽，同样数值视觉上会显得更粗
SW = 4.2


def stroke(d, cls=""):
    c = f' class="{cls}"' if cls else ""
    return (f'<path{c} d="{d}" fill="none" stroke="currentColor" stroke-width="{SW}" '
            f'stroke-linecap="round" stroke-linejoin="round"/>')


def fill(d, cls=""):
    c = f' class="{cls}"' if cls else ""
    return f'<path{c} d="{d}" fill="currentColor" fill-rule="evenodd"/>'


def sparkle(x, y, s):
    """就绪时头顶的闪光：描边的凹边四角星（漫画里的「闪闪」）。
    试过填充四角星（像剪贴画）和十字（读成加号），都和手绘身体不搭。"""
    k = s * 0.22
    return (f"M{x} {y - s}Q{x + k} {y - k} {x + s} {y}Q{x + k} {y + k} {x} {y + s}"
            f"Q{x - k} {y + k} {x - s} {y}Q{x - k} {y - k} {x} {y - s}Z")


def outer_contour(d):
    """身体描摹 = 外轮廓 + 若干内洞；取包围盒最大的子路径当剪影，垫在线条下面做底色。"""
    subs = re.findall(r"M[^M]*", d)

    def area(sp):
        n = [float(v) for v in re.findall(r"-?\d+\.?\d*", sp)]
        xs, ys = n[0::2], n[1::2]
        return (max(xs) - min(xs)) * (max(ys) - min(ys))

    return max(subs, key=area)


EYES_DOT = fill(T["eyes"])
CHEEKS = fill(T["cheeks"], "m-cheeks")
STEAM = f'<g class="m-steam">{fill(T["steam"])}</g>'

FACES = {
    # 等待任务：原作的眼睛和气，嘴换成浅浅的笑
    "idle": [
        f'<g class="m-eyes">{EYES_DOT}</g>',
        CHEEKS,
        stroke("M57 104Q65 111.5 74 104"),
        STEAM,
    ],
    # 转码中：> < 使劲，嘴嘟成 o，头顶的气在抖（动画在页面 CSS 里）
    "busy": [
        stroke("M45 73.5L53 77.5L45 81.5"),
        stroke("M86 73.5L78 77.5L86 81.5"),
        CHEEKS,
        '<ellipse cx="65.5" cy="106" rx="3.6" ry="4.6" fill="none" stroke="currentColor" '
        f'stroke-width="{SW}"/>',
        STEAM,
    ],
    # 就绪 / 送到了：^ ^ 眯眼 + 原作的龇牙笑（就是 Logo 那张脸），头顶冒星星
    "ready": [
        stroke("M44.5 79.5Q49.5 70.5 54.5 79.5"),
        stroke("M76.5 79.5Q81.5 70.5 86.5 79.5"),
        CHEEKS,
        fill(T["grin"]),
        '<g class="m-sparkles">'
        + stroke(sparkle(36, 12, 7), "m-spark") + stroke(sparkle(61, 5.5, 4.5), "m-spark")
        + stroke(sparkle(83, 14, 5.5), "m-spark") + "</g>",
    ],
    # 出错：× × 眼、波浪嘴、额头一滴汗；不冒气
    "error": [
        stroke("M45.5 73.5L53.5 81.5M53.5 73.5L45.5 81.5"),
        stroke("M77.5 73.5L85.5 81.5M85.5 73.5L77.5 81.5"),
        stroke("M53 107Q56 103 59 107T65 107T71 107T77 107"),
        f'<g class="m-sweat">{stroke("M92 30Q98.5 39.5 95.5 43.5Q92 47 88.5 43.5Q85.5 39.5 92 30Z")}</g>',
    ],
    # 空文件夹：抬眼往上瞅、嘴抿成一条线，头顶一个问号
    "empty": [
        '<circle cx="51.5" cy="74.5" r="2.6" fill="currentColor"/>',
        '<circle cx="83.5" cy="74.5" r="2.6" fill="currentColor"/>',
        CHEEKS,
        stroke("M60 107.5Q65 106 70.5 107"),
        '<g class="m-question">'
        + stroke("M59.5 7.5Q59.5 2.5 65 2.5Q70.5 2.5 70.5 7Q70.5 10.5 65.5 12L65.5 14.5")
        + stroke("M65.5 19.5L65.5 19.6") + "</g>",
    ],
}


def build_svg():
    faces = "".join(f'<g class="m-face m-{k}">{"".join(v)}</g>' for k, v in FACES.items())
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128" class="pu-mascot" '
        'data-face="idle" role="img" aria-label="噗噗">'
        f'<path class="m-fill" d="{outer_contour(T["body"])}" fill="var(--mascot-fill, #fff)"/>'
        f'<g class="m-body">{fill(T["body"])}</g>'
        f"{faces}</svg>\n"
    )


def main():
    out = ROOT / "web" / "mascot.svg"
    svg = build_svg()
    out.write_text(svg, encoding="utf-8")
    print(f"{out.relative_to(ROOT)}  {len(svg.encode()) / 1024:.1f} KB  faces={list(FACES)}")


if __name__ == "__main__":
    main()
