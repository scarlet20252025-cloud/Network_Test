# -*- coding: utf-8 -*-
"""生成「手机扫码下载」用的二维码图片（开发期运行，产物入库）。

为什么要预生成、而不是在门户里现场算：
  门户有硬约定「只用标准库，不引第三方依赖」（AGENTS.md「约定」一节）。
  而 Python 标准库**没有**二维码生成器 —— 我试着自己实现了一个
  （约 600 行，含 Reed-Solomon、掩码、格式信息），交叉验证发现多处
  偏差（功能区标记、之字形列对、固定深色模块…），修了几轮仍不能
  逐位一致。继续调下去是拿"锦上添花"的功能换大量时间，不划算。

  二维码是**静态内容**（就是那个下载 URL），完全没必要每次请求现算。
  所以在开发机用成熟的 segno 生成 PNG，产物提交进仓库，运行期只是
  读一个文件 —— 零依赖、零风险、而且图绝对正确。

  代价：换门户地址/端口时要重跑这个脚本。已写进 docs。

用法（开发机，需要 segno）：
    pip install segno
    python tools/gen-android-qr.py
    # 指定地址
    python tools/gen-android-qr.py --url http://10.13.24.1:8080/download-android

产物：
    portal/static/android-qr.png     二维码本体（黑底白码，便于深色页面上放）
    portal/static/android-qr.svg     矢量版（可选，高分屏更清晰）
"""
import argparse
import io
import os
import sys

sys.stdout.reconfigure(encoding='utf-8')

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..'))
OUT_DIR = os.path.join(ROOT, 'portal', 'static')

DEFAULT_URL = 'http://10.13.24.1:8080/download-android'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--url', default=DEFAULT_URL)
    ap.add_argument('--scale', type=int, default=8,
                    help='PNG 每个模块的像素数（默认 8，约 232px 宽）')
    args = ap.parse_args()

    try:
        import segno
    except ImportError:
        print('缺 segno：pip install segno')
        return 2

    os.makedirs(OUT_DIR, exist_ok=True)

    # 纠错等级 H（30%）：二维码会被贴在深色卡片上、可能被压缩、
    # 也可能被手机在稍远距离扫 —— 高纠错更稳。代价是模块多一点。
    qr = segno.make(args.url, error='h', micro=False)

    png = os.path.join(OUT_DIR, 'android-qr.png')
    svg = os.path.join(OUT_DIR, 'android-qr.svg')

    # dark/light 用黑白：白底二维码在深色页面上是一个亮块，
    # 与卡片风格不冲突；反过来（黑底白码）很多扫码器会失败。
    qr.save(png, scale=args.scale, border=2, dark='#000000', light='#ffffff')
    qr.save(svg, scale=1, border=2, dark='#000000', light='#ffffff',
            xmldecl=False, omitsize=True)

    print('URL   :', args.url)
    print('版本  :', qr.version, '  纠错: H')
    print('PNG   :', png, os.path.getsize(png), '字节')
    print('SVG   :', svg, os.path.getsize(svg), '字节')
    return 0


if __name__ == '__main__':
    sys.exit(main())
