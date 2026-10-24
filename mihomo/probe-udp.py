#!/usr/bin/env python3
"""探测本机出站 UDP 是否可用。

为什么先测这个：所有「借 UDP」的方案都建立在「远端能自己发 UDP」之上。
如果远端出站 UDP 被封，那不管用 mihomo 还是 python 中继，都是白搭 ——
先在 30 秒内确认这个前提，比花两小时搭好中继再发现不通要划算。

测法：向多个公共 DNS 各发一个真实 DNS 查询（A 记录），看有没有合法响应。
不只看「有没有回包」，还校验 DNS 事务 ID 和响应码 —— 有些网络会回一个
伪造的空包，只看「收到数据」会误判成通。
"""
import socket
import struct
import sys

# 多源交叉：任何一个通就说明出站 UDP 可用。
# 只测一个目标容易误判（单个 IP 被封 ≠ UDP 被封）。
TARGETS = [
    ('223.5.5.5', 53, '阿里 DNS'),
    ('119.29.29.29', 53, '腾讯 DNS'),
    ('114.114.114.114', 53, '114 DNS'),
    ('8.8.8.8', 53, 'Google DNS'),
]


def build_dns_query(txid, name='www.baidu.com'):
    """构造一个最小的 A 记录查询。"""
    header = struct.pack('>HHHHHH', txid, 0x0100, 1, 0, 0, 0)
    q = b''
    for part in name.split('.'):
        q += bytes([len(part)]) + part.encode()
    q += b'\x00'
    q += struct.pack('>HH', 1, 1)  # QTYPE=A, QCLASS=IN
    return header + q


def probe(host, port, label, timeout=4.0):
    txid = 0x4242
    pkt = build_dns_query(txid)
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.settimeout(timeout)
    try:
        s.sendto(pkt, (host, port))
        data, peer = s.recvfrom(2048)
        if len(data) < 12:
            return (False, f'{label}: 回包过短({len(data)}B)，不是 DNS 响应')
        rid, flags = struct.unpack('>HH', data[:4])
        if rid != txid:
            return (False, f'{label}: 事务 ID 不匹配({rid:#x}≠{txid:#x})，可能是伪造包')
        rcode = flags & 0x000F
        if rcode != 0:
            return (False, f'{label}: DNS 响应码 rcode={rcode}')
        ancount = struct.unpack('>H', data[6:8])[0]
        return (True, f'{label}: 收到合法 DNS 响应 {len(data)}B, 应答 {ancount} 条, 来自 {peer[0]}')
    except socket.timeout:
        return (False, f'{label}: 超时（{timeout}s 无响应）')
    except Exception as e:
        return (False, f'{label}: {type(e).__name__}: {e}')
    finally:
        s.close()


def main():
    print(f'出站 UDP 探测 —— 目标 {len(TARGETS)} 个公共 DNS')
    print('-' * 62)
    ok = 0
    for host, port, label in TARGETS:
        good, msg = probe(host, port, label)
        print(('  [通] ' if good else '  [断] ') + msg)
        if good:
            ok += 1
    print('-' * 62)
    if ok:
        print(f'结论：出站 UDP 可用（{ok}/{len(TARGETS)} 通）—— 可以搭 UDP 中继')
        return 0
    print('结论：出站 UDP 全部不通 —— UDP 中继方案在这里不成立')
    return 1


if __name__ == '__main__':
    sys.exit(main())
