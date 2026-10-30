# -*- coding: utf-8 -*-
"""判断一张手机截图是「白屏/错误页」还是「真正渲染出了页面」。

为什么需要它（2026-10-30）：
  用户报「手机浏览器打不开门户」，而同一地址 curl 完全正常。
  要判断浏览器里到底是白屏、报错页、还是正常渲染，最直接的办法是看图 ——
  但自动化流程里不能靠人眼，所以改用**颜色分布**做判据：

    白屏 / 空白错误页  → 绝大多数像素是纯白 (255,255,255) 或浅灰
    真正渲染的页面      → 深色底（本项目 --bg:#0f1216）+ 少量强调色 #3ddc97

  本项目门户是深色主题，所以「深色像素占比」是很好的判据。

用法：
    python tools/classify-shot.py C:\\tmp\\shot.png
"""
import sys
from collections import Counter

sys.stdout.reconfigure(encoding='utf-8')

try:
    from PIL import Image
except ImportError:
    print('缺 Pillow')
    raise SystemExit(2)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    path = sys.argv[1]
    im = Image.open(path).convert('RGB')
    w, h = im.size
    # 缩样统计，避免大图慢
    step = max(1, min(w, h) // 300)
    c = Counter()
    total = 0
    for y in range(0, h, step):
        for x in range(0, w, step):
            c[im.getpixel((x, y))] += 1
            total += 1

    white = sum(n for (r, g, b), n in c.items() if r > 245 and g > 245 and b > 245)
    dark = sum(n for (r, g, b), n in c.items() if r < 60 and g < 60 and b < 60)
    # 强调绿 #3ddc97 (61,220,151) 附近
    green = sum(n for (r, g, b), n in c.items()
                if 40 <= r <= 90 and 190 <= g <= 245 and 120 <= b <= 190)

    print('图片 %s  (%dx%d)' % (path, w, h))
    print('  采样点 %d' % total)
    print('  纯白占比  %.1f%%' % (100.0 * white / total))
    print('  深色占比  %.1f%%' % (100.0 * dark / total))
    print('  强调绿    %.1f%%' % (100.0 * green / total))
    print()
    print('  最常见的 6 种颜色：')
    for (rgb, n) in c.most_common(6):
        print('    %-18s %5.1f%%' % (str(rgb), 100.0 * n / total))
    print()

    if white / total > 0.75:
        print('判读：❌ 基本是白屏 / 空白错误页')
        verdict = 'blank'
    elif dark / total > 0.4 and green / total > 0.001:
        print('判读：✅ 深色底 + 强调绿 → 页面真的渲染出来了')
        verdict = 'rendered'
    elif dark / total > 0.4:
        print('判读：⚠ 深色底但没有强调色 → 可能渲染了但没有按钮/标题')
        verdict = 'partial'
    else:
        print('判读：⚠ 不确定（可能是图片型错误页或半加载）')
        verdict = 'unknown'
    return 0 if verdict == 'rendered' else 1


if __name__ == '__main__':
    sys.exit(main())
