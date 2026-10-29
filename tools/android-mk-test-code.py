# -*- coding: utf-8 -*-
"""在线上门户库里造一个测试激活码（给 Android M3 联调用）。

为什么直接写库而不是走管理面板：
  管理面板的新建密钥要过 CSRF + 会话，用 curl 走一遍要抓两次 token，
  容易出错且不好复查。这里把 handle_admin_newcode 做的事情原样做一遍：
  生成 code → sha256 → 插一行 invites。字段与那个 handler 完全一致。

注意：note 写成 android-m3-test，方便日后一眼认出并清理。
"""
import hashlib
import os
import sqlite3
import sys
import time

DB = r'C:\ferry-portal\portal.db'
CODE = 'TEST2026'          # 8 位大写字母数字，符合门户的 [A-Z0-9]{6,16}
NOTE = 'android-m3-test'
LICENSE_DAYS = 1           # 只给 1 天，用完自然过期
VALID_HOURS = 24           # 密钥本身 24 小时内可用


def main():
    if not os.path.exists(DB):
        print('!! 找不到库:', DB)
        return 1
    code_hash = hashlib.sha256(CODE.encode('utf-8')).hexdigest()
    now = int(time.time())

    conn = sqlite3.connect(DB)
    cur = conn.cursor()
    row = cur.execute('SELECT id, revoked, used_by_ip FROM invites WHERE code_hash = ?',
                      (code_hash,)).fetchone()
    if row:
        print('已存在，先重置：id=%s revoked=%s used_by_ip=%s' % row)
        cur.execute('UPDATE invites SET revoked = 0, used_by_ip = ?, used_at = 0, '
                    'expires_at = ?, license_days = ?, note = ? WHERE code_hash = ?',
                    ('', now + VALID_HOURS * 3600, LICENSE_DAYS, NOTE, code_hash))
    else:
        cur.execute('INSERT INTO invites (code_hash, code_hint, note, created_at, '
                    'expires_at, license_days) VALUES (?,?,?,?,?,?)',
                    (code_hash, CODE[:4] + '****', NOTE, now,
                     now + VALID_HOURS * 3600, LICENSE_DAYS))
    conn.commit()

    row = cur.execute('SELECT id, code_hint, note, expires_at, used_by_ip, revoked, license_days '
                      'FROM invites WHERE code_hash = ?', (code_hash,)).fetchone()
    print('OK  id=%s hint=%s note=%s expires=%s used_by_ip=%r revoked=%s days=%s'
          % row)
    print('CODE =', CODE)
    conn.close()
    return 0


if __name__ == '__main__':
    sys.exit(main())
