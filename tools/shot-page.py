# -*- coding: utf-8 -*-
"""用 CDP 在精确视口下截图（Edge headless 的 --window-size 有最小宽度限制，
截 390px 会失真，必须走 CDP 的 Emulation.setDeviceMetricsOverride）。

用法：
    python tools/shot-page.py URL OUT.png [宽] [高]
    python tools/shot-page.py http://127.0.0.1:18092/download-page C:\\tmp\\m.png 390 1000
"""
import base64
import json
import os
import subprocess
import sys
import time
import urllib.request

import websocket

sys.stdout.reconfigure(encoding='utf-8')

EDGE = [
    r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    r'C:\Program Files\Microsoft\Edge\Application\msedge.exe',
]


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    url, out = sys.argv[1], sys.argv[2]
    vw = int(sys.argv[3]) if len(sys.argv) > 3 else 390
    vh = int(sys.argv[4]) if len(sys.argv) > 4 else 900

    edge = next((p for p in EDGE if os.path.exists(p)), None)
    if not edge:
        print('找不到 Edge')
        return 2

    port = 9334
    proc = subprocess.Popen([
        edge, '--headless=new', '--disable-gpu', '--no-first-run',
        '--remote-debugging-port=%d' % port,
        '--user-data-dir=' + os.path.join(os.environ.get('TEMP', '.'), 'edge-cdp-shot'),
        'about:blank',
    ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    ws_url = None
    for _ in range(40):
        time.sleep(0.25)
        try:
            tabs = json.loads(urllib.request.urlopen(
                'http://127.0.0.1:%d/json' % port, timeout=2).read())
            for t in tabs:
                if t.get('type') == 'page':
                    ws_url = t['webSocketDebuggerUrl']
                    break
            if ws_url:
                break
        except Exception:
            continue
    if not ws_url:
        proc.kill()
        print('拿不到 CDP')
        return 2

    ws = websocket.create_connection(ws_url, timeout=30)
    mid = [0]

    def call(m, p=None):
        mid[0] += 1
        ws.send(json.dumps({'id': mid[0], 'method': m, 'params': p or {}}))
        while True:
            r = json.loads(ws.recv())
            if r.get('id') == mid[0]:
                return r.get('result')

    call('Page.enable')
    call('Emulation.setDeviceMetricsOverride', {
        'width': vw, 'height': vh, 'deviceScaleFactor': 2, 'mobile': vw < 600,
    })
    call('Page.navigate', {'url': url})
    time.sleep(3)
    shot = call('Page.captureScreenshot', {'format': 'png', 'captureBeyondViewport': True})
    with open(out, 'wb') as f:
        f.write(base64.b64decode(shot['data']))
    print('saved %s (%d bytes) %dx%d' % (out, os.path.getsize(out), vw, vh))
    ws.close()
    proc.kill()
    return 0


if __name__ == '__main__':
    sys.exit(main())
