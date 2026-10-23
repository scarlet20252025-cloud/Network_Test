"""生成渡口的程序图标 ferry-client/app.ico。

设计：青绿圆角方块底 + 白色双向箭头（⇄）。
  · 青绿就是客户端主色 #20B2AA，图标和界面同色系
  · 双向箭头 = 「借网 / 直连」两条路可切换，一眼能懂
  · 圆角方块在任务栏和桌面上都比圆形更贴合 Win11 的图标语言

多尺寸：16/24/32/48/64/128/256。16 和 24 要单独照顾 ——
直接缩放大图会糊成一团，所以小尺寸用加粗的笔画重新画。
"""
import os
from PIL import Image, ImageDraw

OUT = r'E:/肥鱼不乐园/borrow-net/ferry-client/app.ico'
TEAL = (32, 178, 170, 255)
WHITE = (255, 255, 255, 255)
TEAL_D = (22, 150, 143, 255)


def draw(size):
    """按目标尺寸画一张。size 越大线条越细，视觉重量保持一致。"""
    S = size * 4                      # 4 倍超采样，缩下去才平滑
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # 圆角方块底：小尺寸时圆角要更小，否则圆得像个球
    r = int(S * (0.18 if size <= 32 else 0.22))
    d.rounded_rectangle([0, 0, S - 1, S - 1], radius=r, fill=TEAL)

    # 双向箭头：上箭头朝右、下箭头朝左
    # 笔画宽度按尺寸微调：16px 下太细就看不见了
    w = max(2, int(S * (0.085 if size <= 24 else 0.070)))
    head = int(S * (0.16 if size <= 24 else 0.135))
    left = int(S * 0.22)
    right = int(S * 0.78)
    y1 = int(S * 0.375)
    y2 = int(S * 0.625)

    d.line([left, y1, right - head // 2, y1], fill=WHITE, width=w)
    d.polygon([(right, y1), (right - head, y1 - head), (right - head, y1 + head)], fill=WHITE)

    d.line([left + head // 2, y2, right, y2], fill=WHITE, width=w)
    d.polygon([(left, y2), (left + head, y2 - head), (left + head, y2 + head)], fill=WHITE)

    return img.resize((size, size), Image.LANCZOS)


sizes = [256, 128, 64, 48, 32, 24, 16]
imgs = [draw(s) for s in sizes]
imgs[0].save(OUT, format='ICO', sizes=[(s, s) for s in sizes], append_images=imgs[1:])
print('已生成', OUT, os.path.getsize(OUT), '字节')
print('尺寸:', sizes)
