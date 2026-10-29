# -*- coding: utf-8 -*-
"""查线上门户库：这次 Android 激活在服务端留下了什么。"""
import sqlite3
import sys
import time

DB = r'C:\ferry-portal\portal.db'
IP = '10.141.56.25'      # 手机的局域网地址


def main():
    conn = sqlite3.connect(DB)
    cur = conn.cursor()

    print('=== invites（测试码）===')
    for r in cur.execute('SELECT id, code_hint, note, used_by_ip, used_at, revoked, '
                         'license_days, expires_at FROM invites '
                         'WHERE note = ? ORDER BY id DESC LIMIT 3', ('android-m3-test',)):
        used = time.strftime('%H:%M:%S', time.localtime(r[4])) if r[4] else '-'
        print('  id=%s hint=%s note=%s used_by_ip=%r used_at=%s revoked=%s days=%s'
              % (r[0], r[1], r[2], r[3], used, r[5], r[6]))

    print('=== clients（这台手机）===')
    for r in cur.execute('SELECT ip, token, enabled, expires_at FROM clients WHERE ip = ?', (IP,)):
        exp = time.strftime('%Y-%m-%d %H:%M', time.localtime(r[3]))
        print('  ip=%s token=%s enabled=%s expires=%s' % (r[0], r[1], r[2], exp))

    print('=== 最近 12 条日志 ===')
    for r in cur.execute('SELECT ts, kind, ip, detail FROM logs ORDER BY id DESC LIMIT 12'):
        t = time.strftime('%H:%M:%S', time.localtime(r[0]))
        print('  %s  %-16s %-15s %s' % (t, r[1], r[2], (r[3] or '')[:40]))

    conn.close()
    return 0


if __name__ == '__main__':
    sys.exit(main())
