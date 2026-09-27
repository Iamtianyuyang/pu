# /// script
# requires-python = ">=3.10"
# ///
"""由 traced.json（原作描摹）+ 下面手写的表情，同时生成：
  web/mascot.svg          网页内联用（页面用 data-face 属性切换，CSS 控制显隐和动画）
  src/Pu.App/Ui/Mascot.xaml  WPF 用（Face 属性切换，动画在 Mascot.xaml.cs）

两端共用同一份路径数据，噗噗在手机上和电脑上长得一模一样。颜色都不写死：
网页走 currentColor / CSS 变量，WPF 走 DynamicResource（InkBrush / MascotFillBrush），
亮暗主题只换 token。

坐标系：128×128。原作参考点——左眼 (49.5,77)、右眼 (81.5,77)、
嘴中心 (65,106)、头顶尖 (57,21)、原作笔画粗细约 4.75。

用法（仓库根目录）：uv run tools/mascot/build.py
"""
import json
import re
from dataclasses import dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
T = json.loads((HERE / "traced.json").read_text(encoding="utf-8"))

# 新画的线条比原作略细：原作是填充轮廓（圆珠笔压出来的粗细变化），
# 描边等宽，同样数值视觉上会显得更粗
SW = 4.2


# ── 表情元素：结构化描述，分别渲染成 SVG 和 XAML ──

@dataclass
class Fill:
    d: str
    cls: str = ""


@dataclass
class Stroke:
    d: str
    cls: str = ""


@dataclass
class Ellipse:
    cx: float
    cy: float
    rx: float
    ry: float
    stroked: bool = False


@dataclass
class Group:
    """带动画的一组。name 是 WPF 里的 x:Name；origin 是缩放/旋转中心（网页用 transform-box: fill-box 自动算）。"""
    cls: str
    children: list
    name: str = ""
    origin: tuple = (64, 64)


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


def cheeks():
    return Fill(T["cheeks"], "m-cheeks")


def steam(name):
    return Group("m-steam", [Fill(T["steam"])], name, (53, 11))


FACES = {
    # 等待任务：原作的眼睛和气，嘴换成浅浅的笑
    "idle": [
        Group("m-eyes", [Fill(T["eyes"])], "IdleEyes", (65.5, 77)),
        cheeks(),
        Stroke("M57 104Q65 111.5 74 104"),
        steam("IdleSteam"),
    ],
    # 转码中：> < 使劲，嘴嘟成 o，头顶的气在抖
    "busy": [
        Stroke("M45 73.5L53 77.5L45 81.5"),
        Stroke("M86 73.5L78 77.5L86 81.5"),
        cheeks(),
        Ellipse(65.5, 106, 3.6, 4.6, stroked=True),
        steam("BusySteam"),
    ],
    # 就绪 / 送到了：^ ^ 眯眼 + 原作的龇牙笑（就是 Logo 那张脸），头顶冒星星
    "ready": [
        Stroke("M44.5 79.5Q49.5 70.5 54.5 79.5"),
        Stroke("M76.5 79.5Q81.5 70.5 86.5 79.5"),
        cheeks(),
        Fill(T["grin"]),
        Group("m-sparkles", [
            Group("m-spark", [Stroke(sparkle(36, 12, 7))], "Spark1", (36, 12)),
            Group("m-spark", [Stroke(sparkle(61, 5.5, 4.5))], "Spark2", (61, 5.5)),
            Group("m-spark", [Stroke(sparkle(83, 14, 5.5))], "Spark3", (83, 14)),
        ]),
    ],
    # 出错：× × 眼、波浪嘴、额头一滴汗；不冒气
    "error": [
        Stroke("M45.5 73.5L53.5 81.5M53.5 73.5L45.5 81.5"),
        Stroke("M77.5 73.5L85.5 81.5M85.5 73.5L77.5 81.5"),
        Stroke("M53 107Q56 103 59 107T65 107T71 107T77 107"),
        Group("m-sweat", [Stroke("M92 30Q98.5 39.5 95.5 43.5Q92 47 88.5 43.5Q85.5 39.5 92 30Z")], "Sweat", (92, 39)),
    ],
    # 空文件夹：抬眼往上瞅、嘴抿成一条线，头顶一个问号
    "empty": [
        Ellipse(51.5, 74.5, 2.6, 2.6),
        Ellipse(83.5, 74.5, 2.6, 2.6),
        cheeks(),
        Stroke("M60 107.5Q65 106 70.5 107"),
        Group("m-question", [
            Stroke("M59.5 7.5Q59.5 2.5 65 2.5Q70.5 2.5 70.5 7Q70.5 10.5 65.5 12L65.5 14.5"),
            Stroke("M65.5 19.5L65.5 19.6"),
        ], "Question", (65, 11)),
    ],
}


# ── SVG ──

def svg_node(n):
    if isinstance(n, Fill):
        c = f' class="{n.cls}"' if n.cls else ""
        return f'<path{c} d="{n.d}" fill="currentColor" fill-rule="evenodd"/>'
    if isinstance(n, Stroke):
        c = f' class="{n.cls}"' if n.cls else ""
        return (f'<path{c} d="{n.d}" fill="none" stroke="currentColor" stroke-width="{SW}" '
                f'stroke-linecap="round" stroke-linejoin="round"/>')
    if isinstance(n, Ellipse):
        paint = f'fill="none" stroke="currentColor" stroke-width="{SW}"' if n.stroked else 'fill="currentColor"'
        if n.rx == n.ry:
            return f'<circle cx="{n.cx}" cy="{n.cy}" r="{n.rx}" {paint}/>'
        return f'<ellipse cx="{n.cx}" cy="{n.cy}" rx="{n.rx}" ry="{n.ry}" {paint}/>'
    if isinstance(n, Group):
        return f'<g class="{n.cls}">{"".join(svg_node(c) for c in n.children)}</g>'
    raise TypeError(n)


def build_svg():
    faces = "".join(f'<g class="m-face m-{k}">{"".join(svg_node(n) for n in v)}</g>' for k, v in FACES.items())
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128" class="pu-mascot" '
        'data-face="idle" role="img" aria-label="噗噗">'
        f'<path class="m-fill" d="{outer_contour(T["body"])}" fill="var(--mascot-fill, #fff)"/>'
        f'<g class="m-body">{svg_node(Fill(T["body"]))}</g>'
        f"{faces}</svg>\n"
    )


# ── XAML ──

INK = '"{DynamicResource InkBrush}"'


def xaml_node(n, ind):
    p = " " * ind
    if isinstance(n, Fill):
        return f'{p}<Path Data="{n.d}" Fill={INK} />'
    if isinstance(n, Stroke):
        return (f'{p}<Path Data="{n.d}" Stroke={INK} StrokeThickness="{SW}" '
                f'StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" />')
    if isinstance(n, Ellipse):
        paint = f'Stroke={INK} StrokeThickness="{SW}"' if n.stroked else f'Fill={INK}'
        return (f'{p}<Path {paint}><Path.Data><EllipseGeometry Center="{n.cx},{n.cy}" '
                f'RadiusX="{n.rx}" RadiusY="{n.ry}" /></Path.Data></Path>')
    if isinstance(n, Group):
        inner = "\n".join(xaml_node(c, ind + 4) for c in n.children)
        if not n.name:
            return f"{p}<Canvas>\n{inner}\n{p}</Canvas>"
        ox, oy = n.origin
        # 缩放/旋转中心写死在变换上，代码里只改 ScaleX/Angle/Y
        return (f'{p}<Canvas x:Name="{n.name}">\n'
                f"{p}    <Canvas.RenderTransform>\n"
                f"{p}        <TransformGroup>\n"
                f'{p}            <ScaleTransform CenterX="{ox}" CenterY="{oy}" />\n'
                f'{p}            <RotateTransform CenterX="{ox}" CenterY="{oy}" />\n'
                f"{p}            <TranslateTransform />\n"
                f"{p}        </TransformGroup>\n"
                f"{p}    </Canvas.RenderTransform>\n"
                f"{inner}\n{p}</Canvas>")
    raise TypeError(n)


def build_xaml():
    faces = []
    for k, v in FACES.items():
        name = "Face" + k.capitalize()
        body = "\n".join(xaml_node(n, 16) for n in v)
        faces.append(f'            <Canvas x:Name="{name}" Visibility="Collapsed">\n{body}\n            </Canvas>')
    return f'''<!-- 由 tools/mascot/build.py 生成，勿手改：改表情请改 build.py 后重跑（网页 web/mascot.svg 同源生成） -->
<UserControl x:Class="Pu.App.Ui.Mascot"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Focusable="False"
             IsTabStop="False">
    <Viewbox Stretch="Uniform">
        <Canvas x:Name="Root" Width="128" Height="128">
            <Canvas.RenderTransform>
                <ScaleTransform x:Name="Squash" CenterX="64" CenterY="123" />
            </Canvas.RenderTransform>
            <Path Data="{outer_contour(T["body"])}" Fill="{{DynamicResource MascotFillBrush}}" />
            <Path Data="{T["body"]}" Fill={INK} />
{chr(10).join(faces)}
        </Canvas>
    </Viewbox>
</UserControl>
'''


def main():
    svg = build_svg()
    out = ROOT / "web" / "mascot.svg"
    out.write_text(svg, encoding="utf-8")
    print(f"{out.relative_to(ROOT)}  {len(svg.encode()) / 1024:.1f} KB  faces={list(FACES)}")
    xaml = build_xaml()
    out = ROOT / "src" / "Pu.App" / "Ui" / "Mascot.xaml"
    out.write_text(xaml, encoding="utf-8")
    print(f"{out.relative_to(ROOT)}  {len(xaml.encode()) / 1024:.1f} KB")


if __name__ == "__main__":
    main()
