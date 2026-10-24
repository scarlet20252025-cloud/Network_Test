#!/usr/bin/env python3
"""经 mihomo 端点（10810）验证 UDP 真的能走通。

为什么要单独写这个而不是直接用 socks5-udp-test.py：
  socks5-udp-test.py 直连远端 relay，验证的是「relay 支持 UDP」。
  这里要验证的是**完整链路**：客户端 → mihomo(10810) → 规则分流
  → ferry-udp → 远端 relay → 互联网。中间任何一环断了都会失败。

它做的事：
  1. 和 10810 做 SOCKS5 握手 + UDP ASSOCIATE
  2. 发一个真实 DNS 查询
  3. 校验回来的 DNS 事务 ID 和应答内容

用法：
    python verify-udp-path.py
    python verify-udp-path.py --proxy 127.0.0.1:10810 --dns 223.5.5.5:53
"""
import argparse
import socket
import struct
import sys
import time


def build_dns_query(txid, name='www.qq.com'):
    header = struct.pack('>HHHHHH', txid, 0x0100, 1, 0, 0, 0)
    q = b''
    for part in name.split('.'):
        q += bytes([len(part)]) + part.encode()
    q += b'\x00' + struct.pack('>HH', 1, 1)
    return header + q


def wrap_udp_ipv4(addr, port, payload):
    return b'\x00\x00\x00\x01' + socket.inet_aton(addr) + struct.pack('>H', port) + payload


def parse_udp(data):
    atyp = data[3]
    if atyp == 1:
        addr = socket.inet_ntoa(data[4:8])
        port = struct.unpack('>H', data[8:10])[0]
        return addr, port, data[10:]
    if atyp == 3:
        n = data[4]
        addr = data[5:5 + n].decode('ascii', 'replace')
        off = 5 + n
        port = struct.unpack('>H', data[off:off + 2])[0]
        return addr, port, data[off + 2:]
    if atyp == 4:
        addr = socket.inet_ntop(socket.AF_INET6, data[4:20])
        port = struct.unpack('>H', data[20:22])[0]
        return addr, port, data[22:]
    raise ValueError(f'未知 atyp={atyp}')


def parse_dns_answer(payload):
    """从 DNS 响应里取出第一个 A 记录。返回 (rcode, [ip...])。"""
    if len(payload) < 12:
        return None, []
    _tid, flags, qd, an, _ns, _ar = struct.unpack('>HHHHHH', payload[:12])
    rcode = flags & 0x000F
    off = 12
    # 跳过问题段
    for _ in range(qd):
        while off < len(payload) and payload[off] != 0:
            off += 1 + payload[off]
        off += 1 + 4
    ips = []
    for _ in range(an):
        if off >= len(payload):
            break
        # 名称可能是压缩指针（2 字节）或标签序列
        if payload[off] & 0xC0 == 0xC0:
            off += 2
        else:
            while off < len(payload) and payload[off] != 0:
                off += 1 + payload[off]
            off += 1
        if off + 10 > len(payload):
            break
        rtype, _rclass, _ttl, rdlen = struct.unpack('>HHIH', payload[off:off + 10])
        off += 10
        if rtype == 1 and rdlen == 4:
            ips.append(socket.inet_ntoa(payload[off:off + 4]))
        off += rdlen
    return rcode, ips


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--proxy', default='127.0.0.1:10810')
    ap.add_argument('--dns', default='223.5.5.5:53')
    ap.add_argument('--count', type=int, default=5)
    ap.add_argument('--timeout', type=float, default=8.0)
    args = ap.parse_args()

    phost, pport = args.proxy.rsplit(':', 1)
    pport = int(pport)
    dhost, dport = args.dns.rsplit(':', 1)
    dport = int(dport)

    print(f'完整 UDP 链路验证：{args.proxy} → {args.dns}')
    print('-' * 58)

    ctrl = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    ctrl.settimeout(args.timeout)
    try:
        ctrl.connect((phost, pport))
    except Exception as e:
        print(f'[FAIL] 连不上端点 {args.proxy}: {e}')
        return 1

    ctrl.sendall(b'\x05\x01\x00')
    resp = ctrl.recv(2)
    if len(resp) < 2 or resp[1] != 0:
        print(f'[FAIL] 握手失败: {resp!r}')
        return 1
    print('  [1/4] SOCKS5 握手 OK')

    ctrl.sendall(b'\x05\x03\x00\x01' + b'\x00\x00\x00\x00' + b'\x00\x00')
    rep = ctrl.recv(10)
    if len(rep) < 10 or rep[1] != 0:
        code = rep[1] if len(rep) > 1 else '?'
        print(f'[FAIL] UDP ASSOCIATE 被拒: REP={code}')
        return 1
    relay_host = socket.inet_ntoa(rep[4:8])
    relay_port = struct.unpack('>H', rep[8:10])[0]
    if relay_host in ('0.0.0.0', '::'):
        relay_host = phost
    print(f'  [2/4] UDP ASSOCIATE OK，中继 {relay_host}:{relay_port}')

    u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    u.settimeout(args.timeout)
    ok = 0
    rtts = []
    got_ips = []
    for i in range(args.count):
        txid = 0x3000 + i
        pkt = wrap_udp_ipv4(dhost, dport, build_dns_query(txid))
        t0 = time.time()
        try:
            u.sendto(pkt, (relay_host, relay_port))
            data, _peer = u.recvfrom(65535)
            dt = (time.time() - t0) * 1000
            raddr, rport, payload = parse_udp(data)
            if len(payload) < 12:
                print(f'  #{i+1} 载荷过短')
                continue
            rid = struct.unpack('>H', payload[:2])[0]
            if rid != txid:
                print(f'  #{i+1} 事务 ID 不匹配')
                continue
            rcode, ips = parse_dns_answer(payload)
            if rcode != 0:
                print(f'  #{i+1} DNS rcode={rcode}')
                continue
            ok += 1
            rtts.append(dt)
            got_ips.extend(ips)
            print(f'  #{i+1} 通  {dt:.1f}ms  来自 {raddr}:{rport}  解析 {",".join(ips) or "(无 A 记录)"}')
        except socket.timeout:
            print(f'  #{i+1} 超时')
        except Exception as e:
            print(f'  #{i+1} 错误: {type(e).__name__}: {e}')
        time.sleep(0.2)

    u.close()
    ctrl.close()
    print('-' * 58)
    if ok:
        print(f'[OK] [3/4] UDP 数据报 {ok}/{args.count} 通，平均 {sum(rtts)/len(rtts):.1f}ms')
        if got_ips:
            print(f'[OK] [4/4] DNS 解析成功：{", ".join(sorted(set(got_ips)))}')
            print('结论：完整 UDP 链路可用 —— 游戏/QUIC 具备基础')
            return 0
        print('! [4/4] 收到了响应但没有 A 记录（可能被 DNS 劫持）')
        return 0
    print(f'[FAIL] UDP 不通 0/{args.count}')
    print('排查顺序：')
    print('  1. mihomo-keeper.ps1 Status 看 ferry-udp-group 是否为 ferry-udp')
    print('  2. 直连测 relay：python socks5-udp-test.py --proxy 10.13.24.1:1080')
    print('  3. 远端日志：pcs 上 %LOCALAPPDATA%\\Ferry\\logs\\udp-relay.log')
    return 1


if __name__ == '__main__':
    sys.exit(main())
