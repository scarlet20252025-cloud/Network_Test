# -*- coding: utf-8 -*-
"""量某个页面在指定视口下有没有横向溢出（overflow）。

为什么需要这个：
  截图看不准 —— Edge headless 的 --window-size 有最小宽度，实际渲染宽度
  可能不是你要的 390，于是"看起来被裁"其实是截图问题而不是页面问题。
  真正要问的是：document.scrollWidth > clientWidth 吗？

做法：起一个 headless Edge，用 CDP（DevTools Protocol）设精确视口、
读 scrollWidth/clientWidth，并把超宽元素列出来。

用法：
    python tools/check-overflow.py http://127.0.0.1:18092/download-page
    python tools/check-overflow.py URL 390 844
"""
import json
import os
import subprocess
import sys
import time
import urllib.request
import websocket  # websocket-client

sys.stdout.reconfigure(encoding='utf-8')

EDGE_CANDIDATES = [
    r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    r'C:\Program Files\Microsoft\Edge\Application\msedge.exe',
]

JS = r"""
(function(){
  var d = document.documentElement;
  var out = {scrollW: d.scrollWidth, clientW: d.clientWidth,
             scrollH: d.scrollHeight, clientH: d.clientHeight, wide: []};
  var vw = d.clientWidth;
  document.querySelectorAll('*').forEach(function(el){
    var r = el.getBoundingClientRect();
    if (r.width > vw + 1 || r.right > vw + 1) {
      out.wide.push({
        tag: el.tagName.toLowerCase(),
        cls: (el.className && String(el.className).slice(0,40)) || '',
        w: Math.round(r.width),
        right: Math.round(r.right),
        text: (el.textContent || '').trim().slice(0, 40)
      });
    }
  });
  out.wide = out.wide.slice(0, 12);
  return JSON.stringify(out);
})()
"""


def find_edge():
    for p in EDGE_CANDIDATES:
        if os.path.exists(p):
            return p
    return None


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    url = sys.argv[1]
    vw = int(sys.argv[2]) if len(sys.argv) > 2 else 390
    vh = int(sys.argv[3]) if len(sys.argv) > 3 else 844

    edge = find_edge()
    if not edge:
        print('找不到 Edge')
        return 2

    port = 9333
    proc = subprocess.Popen([
        edge, '--headless=new', '--disable-gpu', '--no-first-run',
        '--remote-debugging-port=%d' % port,
        '--user-data-dir=' + os.path.join(os.environ.get('TEMP', '.'), 'edge-cdp-chk'),
        'about:blank',
    ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    ws_url = None
    for _ in range(40):
        time.sleep(0.25)
        try:
            raw = urllib.request.urlopen('http://127.0.0.1:%d/json' % port, timeout=2).read()
            tabs = json.loads(raw)
            for t in tabs:
                if t.get('type') == 'page':
                    ws_url = t['webSocketDebuggerUrl']
                    break
            if ws_url:
                break
        except Exception:
            continue
    if not ws_url:
        print('拿不到 CDP 端点')
        proc.kill()
        return 2

    ws = websocket.create_connection(ws_url, timeout=20)
    mid = [0]

    def call(method, params=None):
        mid[0] += 1
        ws.send(json.dumps({'id': mid[0], 'method': method, 'params': params or {}}))
        while True:
            msg = json.loads(ws.recv())
            if msg.get('id') == mid[0]:
                return msg.get('result')

    call('Page.enable')
    call('Emulation.setDeviceMetricsOverride', {
        'width': vw, 'height': vh, 'deviceScaleFactor': 1, 'mobile': vw < 600,
    })
    call('Page.navigate', {'url': url})
    time.sleep(2.5)

    res = call('Runtime.evaluate', {'expression': JS, 'returnByValue': True})
    val = res['result']['value']
    data = json.loads(val)

    print('视口 %dx%d' % (vw, vh))
    print('  clientWidth  =', data['clientW'])
    print('  scrollWidth  =', data['scrollW'])
    over = data['scrollW'] - data['clientW']
    print('  横向溢出     =', over, 'px', '  ✅ 无溢出' if over <= 1 else '  ❌ 有溢出')
    if data['wide']:
        print('  超宽元素：')
        for w in data['wide']:
            print('    <%-8s class="%s"> w=%d right=%d  %r'
                  % (w['tag'], w['cls'], w['w'], w['right'], w['text']))
    else:
        print('  超宽元素：无')

    ws.close()
    proc.kill()
    return 0 if over <= 1 else 1


if __name__ == '__main__':
    sys.exit(main())
