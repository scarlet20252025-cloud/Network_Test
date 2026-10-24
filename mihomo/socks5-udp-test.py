#!/usr/bin/env python3
"""SOCKS5 UDP ASSOCIATE 客户端 —— 验证服务端是否真的支持 UDP 中继。

为什么必须真跑一次：mihomo 的二进制里有 Socks5UDPAssociate 符号，监听端口
也同时开了 UDP，但「端口在听」和「UDP 数据真的能穿过去」是两件事。
很多实现会接受 ASSOCIATE 握手然后静默丢弃数据报。

协议流程（RFC 1928）：
  1. 建立 TCP 控制连接，握手 → 认证
  2. 发 UDP ASSOCIATE 请求（CMD=0x03），服务端回一个中继地址
  3. 把目标地址和端口封进 SOCKS5 UDP 头，发到那个中继地址
  4. 服务端代发，回来的包也带同样的头

用法：
    python socks5-udp-test.py --proxy 127.0.0.1:10821 --dns 223.5.5.5:53
"""
import argparse
import socket
import struct
import sys
import time


def build_dns_query(txid, name='www.baidu.com'):
    header = struct.pack('>HHHHHH', txid, 0x0100, 1, 0, 0, 0)
    q = b''
    for part in name.split('.'):
        q += bytes([len(part)]) + part.encode()
    q += b'\x00' + struct.pack('>HH', 1, 1)
    return header + q


def wrap_udp(atyp, addr, port, payload):
    """按 RFC1928 第 7 章封装 UDP 头。"""
    if atyp == 1:      # IPv4
        head = b'\x00\x00\x00\x01' + socket.inet_aton(addr)
    elif atyp == 3:    # 域名
        b = addr.encode()
        head = b'\x00\x00\x00\x03' + bytes([len(b)]) + b
    elif atyp == 4:    # IPv6
        head = b'\x00\x00\x00\x04' + socket.inet_pton(socket.AF_INET6, addr)
    else:
        raise ValueError(atyp)
    return head + struct.pack('>H', port) + payload


def parse_udp(data):
    """返回 (atyp, addr, port, payload)。"""
    if len(data) < 10:
        raise ValueError(f'过短 {len(data)}B')
    _rsv, _frag, atyp = data[0], data[1], data[3]
    if atyp == 1:
        addr = socket.inet_ntoa(data[4:8])
        port = struct.unpack('>H', data[8:10])[0]
        return atyp, addr, port, data[10:]
    if atyp == 3:
        n = data[4]
        addr = data[5:5 + n].decode('ascii', 'replace')
        off = 5 + n
        port = struct.unpack('>H', data[off:off + 2])[0]
        return atyp, addr, port, data[off + 2:]
    if atyp == 4:
        addr = socket.inet_ntop(socket.AF_INET6, data[4:20])
        port = struct.unpack('>H', data[20:22])[0]
        return atyp, addr, port, data[22:]
    raise ValueError(f'未知 atyp={atyp}')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--proxy', default='127.0.0.1:10821')
    ap.add_argument('--dns', default='223.5.5.5:53')
    ap.add_argument('--count', type=int, default=3)
    ap.add_argument('--timeout', type=float, default=6.0)
    args = ap.parse_args()

    phost, pport = args.proxy.rsplit(':', 1)
    pport = int(pport)
    dhost, dport = args.dns.rsplit(':', 1)
    dport = int(dport)

    print(f'SOCKS5 代理 {phost}:{pport} → UDP 目标 {dhost}:{dport}')

    # ── 1. TCP 控制连接 + 握手 ──
    ctrl = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    ctrl.settimeout(args.timeout)
    try:
        ctrl.connect((phost, pport))
    except Exception as e:
        print(f'控制连接失败: {e}')
        return 1

    ctrl.sendall(b'\x05\x01\x00')          # VER=5, NMETHODS=1, METHOD=no-auth
    resp = ctrl.recv(2)
    if len(resp) < 2 or resp[0] != 5:
        print(f'握手响应异常: {resp!r}')
        return 1
    if resp[1] != 0:
        print(f'服务端要求认证 method={resp[1]:#x}，本测试只支持 no-auth')
        return 1
    print('  握手 OK（no-auth）')

    # ── 2. UDP ASSOCIATE ──
    # 请求里的地址填 0.0.0.0:0，表示「我自己也不知道会从哪个端口发」。
    ctrl.sendall(b'\x05\x03\x00\x01' + b'\x00\x00\x00\x00' + b'\x00\x00')
    rep = ctrl.recv(10)
    if len(rep) < 10:
        print(f'ASSOCIATE 响应过短: {rep!r}')
        return 1
    if rep[1] != 0:
        # REP: 0x07 = Command not supported（这正是现有 ferry-socks5-server.py 的返回）
        names = {1: 'general failure', 2: 'not allowed', 3: 'network unreachable',
                 4: 'host unreachable', 5: 'connection refused',
                 6: 'TTL expired', 7: 'Command not supported',
                 8: 'Address type not supported'}
        print(f'  ASSOCIATE 被拒绝: REP={rep[1]:#x} ({names.get(rep[1], "unknown")})')
        print('  结论：服务端不支持 UDP ASSOCIATE')
        return 1

    atyp = rep[3]
    if atyp == 1:
        relay_host = socket.inet_ntoa(rep[4:8])
        relay_port = struct.unpack('>H', rep[8:10])[0]
    else:
        relay_host, relay_port = phost, pport
    # 服务端回 0.0.0.0 表示「用你连我的那个地址」
    if relay_host in ('0.0.0.0', '::'):
        relay_host = phost
    print(f'  ASSOCIATE OK，中继地址 {relay_host}:{relay_port}')

    # ── 3. 发 UDP ──
    u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    u.settimeout(args.timeout)
    ok = 0
    rtts = []
    for i in range(args.count):
        txid = 0x1000 + i
        dns = build_dns_query(txid)
        pkt = wrap_udp(1, dhost, dport, dns)
        t0 = time.time()
        try:
            u.sendto(pkt, (relay_host, relay_port))
            data, peer = u.recvfrom(65535)
            dt = (time.time() - t0) * 1000
            _a, raddr, rport, payload = parse_udp(data)
            if len(payload) >= 12:
                rid = struct.unpack('>H', payload[:2])[0]
                if rid == txid:
                    ok += 1
                    rtts.append(dt)
                    print(f'  #{i+1} 通  {dt:.1f}ms  DNS {len(payload)}B 来自 {raddr}:{rport}')
                else:
                    print(f'  #{i+1} 事务 ID 不匹配 {rid:#x}≠{txid:#x}')
            else:
                print(f'  #{i+1} 载荷过短 {len(payload)}B')
        except socket.timeout:
            print(f'  #{i+1} 超时（{args.timeout}s）')
        except Exception as e:
            print(f'  #{i+1} 错误: {type(e).__name__}: {e}')
        time.sleep(0.3)

    u.close()
    ctrl.close()
    print('-' * 55)
    if ok:
        print(f'结论：SOCKS5 UDP 中继可用 {ok}/{args.count}，平均 {sum(rtts)/len(rtts):.1f}ms')
        return 0
    print(f'结论：UDP 中继不通 0/{args.count}')
    return 1


if __name__ == '__main__':
    sys.exit(main())
