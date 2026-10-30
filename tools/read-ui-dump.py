# -*- coding: utf-8 -*-
"""把 uiautomator dump 出来的界面 XML 里的文本按顺序列出来。

为什么需要它：手机上抓 UI 只能 `uiautomator dump`，出来的是一坨 XML；
而直接用 python 在 GBK 控制台里 print 中文/符号（✓、•）会
UnicodeEncodeError —— 报错看起来像「脚本错了」，其实只是终端编码。
这个脚本强制 stdout 用 UTF-8，并顺便按出现顺序输出，便于核对界面。

用法（Windows 控制台是 GBK 时也能跑）：
    python tools/read-ui-dump.py scratch/ui-dump.xml
    python tools/read-ui-dump.py scratch/ui-dump.xml --with-bounds
"""
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    path = sys.argv[1]
    show_bounds = '--with-bounds' in sys.argv

    raw = open(path, encoding='utf-8', errors='replace').read()

    # 按节点切，保留 text / resource-id / bounds 三个属性
    nodes = re.findall(r'<node[^>]*>', raw)
    count = 0
    for n in nodes:
        text = re.search(r'text="([^"]*)"', n)
        rid = re.search(r'resource-id="([^"]*)"', n)
        bounds = re.search(r'bounds="([^"]*)"', n)
        clicked = re.search(r'clickable="([^"]*)"', n)
        t = text.group(1) if text else ''
        if not t.strip():
            continue
        count += 1
        line = '  %2d. %s' % (count, t)
        if show_bounds:
            extra = []
            if rid and rid.group(1):
                extra.append(rid.group(1).split('/')[-1])
            if bounds:
                extra.append(bounds.group(1))
            if clicked and clicked.group(1) == 'true':
                extra.append('可点击')
            if extra:
                line += '   [' + ' | '.join(extra) + ']'
        print(line)

    if count == 0:
        print('  （没有找到任何带 text 的节点 —— 界面可能还在加载，或者控件用的是 content-desc）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
