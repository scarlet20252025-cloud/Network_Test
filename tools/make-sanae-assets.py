"""从东风谷早苗立绘生成渡口的图标与头像资产。

为什么单独写脚本而不是手工导一张图：
  · 图标要 7 个尺寸，16px 和 256px 的观感差很多，缩放策略得固定下来；
  · 头像要在侧边栏里做圆形裁切，白底必须抠掉，否则暗色侧栏上会露白边；
  · 配色要从原图采样，不能凭印象写，否则界面和立绘不是一个色系。

输出：
  ferry-client/app.ico            程序图标（exe / 任务栏 / 托盘共用）
  ferry-client/avatar.png         侧边栏圆形头像用（透明底，512px）
  ferry-client/palette.json       从原图采样出来的印象色

用法：
  python tools/make-sanae-assets.py
  python tools/make-sanae-assets.py --src <别的图>
"""
import argparse
import json
import os
from collections import Counter

from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLIENT = os.path.join(ROOT, 'ferry-client')

DEFAULT_SRC = os.path.join(
    os.path.expanduser('~'), '.workbuddy', 'clipboard-images',
    'clipboard-2026-11-10T08-14-19-922Z-33410ce0.jpg')


def near_white(px, tol=18):
    r, g, b = px[0], px[1], px[2]
    return r >= 255 - tol and g >= 255 - tol and b >= 255 - tol


def flood_clear_white(img):
    """把**和边缘连通的**白像素抠成透明。

    不能直接按「白」判：裙子和衬衣也是白的，一刀切会把衣服打穿。
    从四条边往里扫，只清掉外面那圈背景。
    """
    img = img.convert('RGBA')
    w, h = img.size
    px = img.load()
    seen = bytearray(w * h)
    stack = []

    for x in range(w):
        stack.append((x, 0))
        stack.append((x, h - 1))
    for y in range(h):
        stack.append((0, y))
        stack.append((w - 1, y))

    while stack:
        x, y = stack.pop()
        if x < 0 or y < 0 or x >= w or y >= h:
            continue
        i = y * w + x
        if seen[i]:
            continue
        seen[i] = 1
        if not near_white(px[x, y]):
            continue
        px[x, y] = (255, 255, 255, 0)
        stack.append((x + 1, y))
        stack.append((x - 1, y))
        stack.append((x, y + 1))
        stack.append((x, y - 1))

    # 抠完边缘会有 1px 的灰边（抗锯齿过渡色），把它羽化成半透明
    alpha = img.split()[3]
    alpha = alpha.filter(ImageFilter.MinFilter(3)).filter(ImageFilter.GaussianBlur(1.0))
    img.putalpha(alpha)
    return img


def trim(img, pad=8):
    box = img.split()[3].getbbox()
    if not box:
        return img
    x0, y0, x1, y1 = box
    x0, y0 = max(0, x0 - pad), max(0, y0 - pad)
    x1, y1 = min(img.width, x1 + pad), min(img.height, y1 + pad)
    return img.crop((x0, y0, x1, y1))


def sample_palette(img):
    """取面积最大的几种颜色，再挑出绿 / 蓝两簇当印象色。"""
    small = img.convert('RGB').resize((160, 160), Image.LANCZOS)
    cnt = Counter(small.getdata())
    total = 160 * 160
    palette = []
    for (r, g, b), n in cnt.most_common(64):
        if near_white((r, g, b), 24):
            continue
        # 近黑是描边，不进色板
        if r < 70 and g < 70 and b < 70:
            continue
        if n / total < 0.004:
            continue
        palette.append({'hex': '#%02X%02X%02X' % (r, g, b), 'share': round(n / total, 4)})
        if len(palette) >= 12:
            break

    def pick(pred):
        for p in palette:
            r = int(p['hex'][1:3], 16)
            g = int(p['hex'][3:5], 16)
            b = int(p['hex'][5:7], 16)
            if pred(r, g, b):
                return p['hex']
        return None

    return {
        'hair': pick(lambda r, g, b: g > r + 8 and g > b + 8),
        'eye': pick(lambda r, g, b: b > r + 25 and b > g + 15),
        'all': palette,
    }


def head_crop(src):
    """框住角色头部（含一点肩膀 / 青蛙 / 蛇），图标和头像共用同一块。
    这张立绘的角色略偏右、左边是御币，正中对半裁会把脸切掉一半。
    """
    art = src.convert('RGBA')
    w, h = art.size
    side = int(w * 0.62)
    cx, cy = int(w * 0.52), int(h * 0.46)
    box = (cx - side // 2, max(0, cy - side // 2),
           cx + side // 2, min(h, cy + side // 2))
    return art.crop(box)


def make_icon(src, out):
    """透明底 + 角色本身。不套圆角方块，也不做圆形遮罩。

    用户明确要求「用原来的透明图」：深绿圆角块 + 圆形裁切在 16~32px 下既糊
    又抢戏，还和深绿界面重复了一层「框」。现在只把角色等比放进正方形画布，
    让原图自带的深色描边撑住轮廓。

    小尺寸（16/24/32）把内容再放大一点点 —— 透明底本身就少了一圈衬色，
    不补一点面积的话，任务栏上只剩几根线。
    """
    art = trim(flood_clear_white(head_crop(src)), pad=2)

    sizes = [256, 128, 64, 48, 32, 24, 16]
    frames = []
    for s in sizes:
        S = s * 4 if s <= 64 else s          # 小图 4 倍超采样，缩下去边缘才干净
        canvas = Image.new('RGBA', (S, S), (0, 0, 0, 0))

        box = int(S * 0.94)
        a = art.copy()
        a.thumbnail((box, box), Image.LANCZOS)
        if s <= 32:
            a = a.resize((max(1, int(a.width * 1.08)), max(1, int(a.height * 1.08))),
                         Image.LANCZOS)

        canvas.paste(a, ((S - a.width) // 2, (S - a.height) // 2), a)
        frames.append(canvas.resize((s, s), Image.LANCZOS))

    frames[0].save(out, format='ICO', sizes=[(s, s) for s in sizes],
                   append_images=frames[1:])
    return out, sizes


def make_avatar(src, out, size=320):
    """侧栏头像：**头部特写**，不是整张立绘。

    早先直接缩整张全身图，78px 的圆圈里角色只有绿豆大，细节全糊成一团噪点，
    看着就是「图片模糊、有锯齿」。头像框只需要脸，所以和图标共用同一块裁切。
    输出 320px：侧栏显示 78 DIP，200% 缩放下是 156px，320 已有 2 倍余量；
    再大只是让 exe 变胖。
    """
    art = flood_clear_white(head_crop(src))
    # 贴到正方形画布，保证圆形裁切时四周不切头发
    side = max(art.size)
    canvas = Image.new('RGBA', (side, side), (0, 0, 0, 0))
    canvas.paste(art, ((side - art.width) // 2, (side - art.height) // 2), art)
    canvas = canvas.resize((size, size), Image.LANCZOS)
    canvas.save(out, format='PNG')
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', default=DEFAULT_SRC)
    args = ap.parse_args()

    if not os.path.exists(args.src):
        raise SystemExit('找不到素材：' + args.src)

    src = Image.open(args.src)
    pal = sample_palette(src)
    print('采样色：')
    for p in pal['all'][:8]:
        print('  ', p['hex'], p['share'])
    print('  发色候选', pal['hair'], ' 瞳色候选', pal['eye'])

    ico = os.path.join(CLIENT, 'app.ico')
    avatar = os.path.join(CLIENT, 'avatar.png')
    print('图标 ->', make_icon(src, ico))
    print('头像 ->', make_avatar(src, avatar), os.path.getsize(avatar), '字节')

    with open(os.path.join(CLIENT, 'palette.json'), 'w', encoding='utf-8') as f:
        json.dump(pal, f, ensure_ascii=False, indent=2)


if __name__ == '__main__':
    main()
