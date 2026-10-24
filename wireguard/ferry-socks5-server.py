"""渡口 · 远端 SOCKS5 服务端（TCP CONNECT + UDP ASSOCIATE）

为什么需要它：
  用户在局域网里要「断网能用 + 低延迟」。
  低延迟意味着不能走 SSH 隧道（实测抖动放大 4.55 倍：29ms → 132ms），
  必须走 WireGuard（实测 UDP 往返 10.1ms）。

  但 WireGuard 只把包送到远端，远端得有能力把包发出去 —— 那需要 NAT。
  Windows 客户端版的 WinNAT 在很多机器上根本不存在
  （New-NetNat 报 0x80041010 WBEM_E_INVALID_CLASS）。

  绕开 NAT 的办法：**不在 IP 层转发，而在应用层做代理**。
    客户端系统代理 → 10.66.67.1:1080（走 WG）→ 这个服务直连目标
  这样不需要改任何包的源 IP，也就不需要 NAT。

  ── v2 新增 UDP ASSOCIATE ──
  以前这里只实现 CONNECT（`if cmd != 1: 拒绝`），所以整条链路只有 TCP：
  游戏、QUIC、部分视频通话全部不能用。
  现在补上 UDP ASSOCIATE（RFC 1928 CMD=0x03），UDP 也能中继。

  为什么不用「pcs 上装 mihomo 开 socks inbound」那条路：
    mihomo 二进制 61 MB，而这个脚本几十 KB；pcs 已有 Python 3.14，
    标准库零依赖。同样的能力，传输和部署成本差三个数量级。

  ⚠ 关键前提（已实测，不是假设）：
    UDP 中继要求**客户端能直接向本服务发 UDP**。
    SOCKS5 的 UDP 不走 TCP 控制连接，是独立的 UDP 数据报。
    实测本机 10.141.59.235 → pcs 10.13.24.1:10820 五发五收、平均 6.8ms，
    校园网并没有只放行 TCP。如果哪天这条路被封，UDP 会退化成不可用，
    但 TCP 部分不受影响。

部署：
  放在远端跑，监听 0.0.0.0:1080（或指定 --bind）。
  默认只接受私网来源（--allow 可改）。

  常驻（pcs，Windows）：
    python ferry-socks5-server.py --port 1080 --udp
"""
import argparse
import asyncio
import ipaddress
import socket
import struct
import sys
import time

# UDP 会话多久没流量就回收。
# 为什么不设很短：DNS 查询间隔可能几十秒，游戏心跳也可能几秒一次，
# 回收太快会导致「刚才还通，现在要重新握手」。60 秒是经验值。
UDP_IDLE_TIMEOUT = 60.0

# ── 每 IP 限速 ──
#
# 为什么限速必须放在服务端：
#   客户端是用户自己的机器，改个 config 就能绕过去。
#   只有所有流量都必须经过的这个点才真正有效 —— 也就是这里。
#   （TCP 以前走 ssh -D，sshd 转发时源 IP 全变成 127.0.0.1，根本没法按机器区分，
#     所以那时候限速其实无处可放。现在 TCP 也走这个 SOCKS5，才有了统一的限速点。）
#
# 为什么用 GCRA 而不是令牌桶：
#   令牌桶初始给满一桶，实测会瞬间超速 2.4 倍（项目里 old dual-proxy.mjs 踩过）。
#   GCRA 只维护「下一个包最早可发时间」，天然没有初始突发。
GCRA_TOLERANCE = 0.05   # 允许 50ms 突发，避免 TCP 窗口塌陷


class RateLimiter:
    """对某个源 IP 的带宽限制。rate_bps<=0 表示不限速。

    用法：每发一块数据前 `await limiter.throttle(nbytes)`。
    """

    __slots__ = ('rate', 'tat')

    def __init__(self, rate_bps):
        self.rate = float(rate_bps or 0)
        self.tat = 0.0          # theoretical arrival time（单调时钟）

    def cost(self, nbytes):
        """返回为了不超速，发这么多字节需要等多久（秒）。"""
        if self.rate <= 0:
            return 0.0
        now = time.monotonic()
        cost = nbytes / self.rate
        earliest = max(now - GCRA_TOLERANCE, self.tat)
        self.tat = earliest + cost
        return max(0.0, self.tat - now)

    async def throttle(self, nbytes):
        delay = self.cost(nbytes)
        if delay > 0:
            await asyncio.sleep(delay)


class RateTable:
    """ip -> RateLimiter。按源 IP 共享，不是按连接。

    为什么按 IP 而不是按连接：一个人能开 40 条连接（maxConnsPerUser），
    按连接限速等于把总配额乘 40，限了跟没限一样。
    """

    def __init__(self, rate_bps):
        self.rate_bps = rate_bps
        self.lock = asyncio.Lock()
        self.map = {}

    async def get(self, ip):
        if self.rate_bps <= 0:
            return None
        async with self.lock:
            lim = self.map.get(ip)
            if lim is None:
                lim = self.map[ip] = RateLimiter(self.rate_bps)
            return lim


# 全局的限速表。main() 里按 --rate-mb 初始化。
RATES = RateTable(0)


def log(msg):
    print('[%s] %s' % (time.strftime('%H:%M:%S'), msg), flush=True)


def allowed_peer(ip, nets):
    try:
        addr = ipaddress.ip_address(ip)
    except ValueError:
        return False
    return any(addr in n for n in nets)


async def pipe(reader, writer, label, counter, limiter=None):
    try:
        while True:
            data = await reader.read(65536)
            if not data:
                break
            # 先限速再写：反过来的话数据已经进内核缓冲区了，
            # 那一段等于没限（实测双方向都这样会明显超速）。
            if limiter is not None:
                await limiter.throttle(len(data))
            writer.write(data)
            await writer.drain()
            counter[0] += len(data)
    except (asyncio.IncompleteReadError, ConnectionResetError, BrokenPipeError):
        pass
    except Exception:
        pass
    finally:
        try:
            writer.close()
        except Exception:
            pass


# ────────────────────────── UDP 中继 ──────────────────────────

def parse_socks5_udp(data):
    """解析 SOCKS5 UDP 请求头，返回 (atyp, host, port, payload)。

    格式（RFC 1928 §7）：
      +----+------+------+----------+----------+----------+
      |RSV | FRAG | ATYP | DST.ADDR | DST.PORT |   DATA   |
      +----+------+------+----------+----------+----------+
      | 2  |  1   |  1   | Variable |    2     | Variable |
    用 `>HBB` 一次读 4 字节更省事：RSV 占 2 字节，FRAG 1 字节，ATYP 1 字节。
    """
    if len(data) < 4:
        raise ValueError('UDP 头过短')
    _rsv, frag, atyp = struct.unpack('>HBB', data[:4])
    if frag != 0:
        # 分片（FRAG != 0）需要客户端自己重组，本服务不支持。
        # 实际绝大多数客户端（含 mihomo）都不分片，直接丢弃即可。
        raise ValueError('不支持 UDP 分片')
    off = 4
    if atyp == 1:                                   # IPv4
        host = socket.inet_ntoa(data[off:off + 4])
        off += 4
    elif atyp == 3:                                 # 域名
        n = data[off]
        host = data[off + 1:off + 1 + n].decode('idna', 'replace')
        off += 1 + n
    elif atyp == 4:                                 # IPv6
        host = socket.inet_ntop(socket.AF_INET6, data[off:off + 16])
        off += 16
    else:
        raise ValueError('未知 ATYP=%d' % atyp)
    if len(data) < off + 2:
        raise ValueError('UDP 头缺端口')
    port = struct.unpack('>H', data[off:off + 2])[0]
    return atyp, host, port, data[off + 2:]


def build_socks5_udp(addr, port, payload):
    """把目标回包封装成 SOCKS5 UDP 响应头（IPv4）。"""
    try:
        packed = socket.inet_aton(addr)
        head = b'\x00\x00\x00\x01' + packed
    except OSError:
        # IPv6 或异常地址：用域名形式兜底
        b = addr.encode()
        head = b'\x00\x00\x00\x03' + bytes([len(b)]) + b
    return head + struct.pack('>H', port) + payload


class UdpRelay:
    """一个客户端的一条 UDP 关联。

    为什么用**两个** UDP socket（client_sock / target_sock）而不是一个：
      单 socket 方案要靠「来源地址是不是已知目标」来判断包是请求还是响应，
      当目标恰好和客户端同地址时会误判。
      两个 socket 各收各的：client_sock 只收客户端发来的带 SOCKS5 头的包，
      target_sock 只收目标发回的裸包 —— 结构上就不可能混淆。

    为什么每个关联独立开 socket 而不是全局共享一个：
      隔离简单。客户端断开时直接 close 两个 socket，不用维护复杂的
      「哪些目标映射到哪个客户端」的全局表。代价是每个关联占两个端口，
      对个人工具（几十个关联顶天）完全不是问题。
    """

    def __init__(self, client_ip, client_port, on_close=None):
        self.client_addr = (client_ip, client_port)
        # 实际收到 UDP 包的源地址。**必须**用它回发，不能用 client_addr。
        #
        # 踩过的坑：一开始把回包发到 client_addr（= TCP 控制连接的源地址），
        # 服务端日志显示「上行 93 B / 下行 270 B / 3 包」一切正常，
        # 但客户端全部超时 —— 因为客户端的 UDP socket 是独立创建的，
        # 源端口和 TCP 源端口不是一个。包发到了客户端根本没在监听的端口上。
        # RFC 1928 只要求同一 IP，端口可以不同。
        self.udp_peer = None
        self.on_close = on_close
        self.client_sock = None
        self.target_sock = None
        self.closed = False
        self.last_active = time.time()
        self.stats = {'up': 0, 'down': 0, 'pkts': 0}

    async def start(self):
        loop = asyncio.get_running_loop()

        # client_sock：绑定 0.0.0.0:0，把得到的端口告诉客户端。
        self.client_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.client_sock.setblocking(False)
        self.client_sock.bind(('0.0.0.0', 0))

        # target_sock：专门和目标通信
        self.target_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.target_sock.setblocking(False)
        self.target_sock.bind(('0.0.0.0', 0))

        self._client_proto = _UdpProto(self._on_client_datagram)
        self._target_proto = _UdpProto(self._on_target_datagram)
        self._client_transport, _ = await loop.create_datagram_endpoint(
            lambda: self._client_proto, sock=self.client_sock)
        self._target_transport, _ = await loop.create_datagram_endpoint(
            lambda: self._target_proto, sock=self.target_sock)

        self.port = self.client_sock.getsockname()[1]
        return self.port

    # ── 客户端 → 目标 ──
    def _on_client_datagram(self, data, addr):
        self.last_active = time.time()
        # 只接受控制连接那个 IP 发来的包。别的机器猜到端口也不能白嫖。
        if addr[0] != self.client_addr[0]:
            return
        # 记住真实 UDP 源端口，回包用这个
        if self.udp_peer != addr:
            self.udp_peer = addr
        try:
            _atyp, host, port, payload = parse_socks5_udp(data)
        except ValueError as e:
            log('UDP 头解析失败: %s' % e)
            return
        if not payload:
            return
        self.stats['up'] += len(payload)
        self.stats['pkts'] += 1
        try:
            self._target_transport.sendto(payload, (host, port))
        except Exception as e:
            log('UDP 发往 %s:%d 失败: %s' % (host, port, e))

    # ── 目标 → 客户端 ──
    def _on_target_datagram(self, data, addr):
        self.last_active = time.time()
        self.stats['down'] += len(data)
        if not self.udp_peer:
            return   # 客户端还没发过包，不知道回给谁
        pkt = build_socks5_udp(addr[0], addr[1], data)
        try:
            self._client_transport.sendto(pkt, self.udp_peer)
        except Exception as e:
            log('UDP 回发客户端失败: %s' % e)

    def close(self):
        if self.closed:
            return
        self.closed = True
        for t in (getattr(self, '_client_transport', None),
                  getattr(self, '_target_transport', None)):
            try:
                if t:
                    t.close()
            except Exception:
                pass
        log('UDP 关联关闭 %s:%d  上行 %d B / 下行 %d B / %d 包'
            % (self.client_addr[0], self.port,
               self.stats['up'], self.stats['down'], self.stats['pkts']))
        if self.on_close:
            self.on_close()


class _UdpProto(asyncio.DatagramProtocol):
    """把 asyncio 的 DatagramProtocol 转成普通回调。

    必须实现 error_received：Windows 在目标端口关闭时会把 ICMP
    port unreachable 变成 WSAECONNRESET（10054）抛给下一次 recv，
    不吞掉的话事件循环会一直报错。实测踩过这个。
    """

    def __init__(self, on_data):
        self.on_data = on_data

    def datagram_received(self, data, addr):
        try:
            self.on_data(data, addr)
        except Exception as e:
            log('UDP 回调异常: %s' % e)

    def error_received(self, exc):
        # 静默忽略：这是「对端端口不可达」，属于正常的 UDP 语义
        pass


# ────────────────────────── TCP CONNECT ──────────────────────────

async def handle(client_reader, client_writer, nets, udp_enabled):
    peer = client_writer.get_extra_info('peername')
    peer_ip = peer[0] if peer else '?'
    if not allowed_peer(peer_ip, nets):
        log('拒绝 %s（不在允许的网段）' % peer_ip)
        client_writer.close()
        return

    counter = [0]
    relay = None
    try:
        # ── 握手：只接受「无认证」 ──
        head = await client_reader.readexactly(2)
        if head[0] != 5:
            log('%s 不是 SOCKS5（ver=%d）' % (peer_ip, head[0]))
            client_writer.close()
            return
        nmethods = head[1]
        methods = await client_reader.readexactly(nmethods)
        if 0 not in methods:
            client_writer.write(b'\x05\xff')
            await client_writer.drain()
            client_writer.close()
            return
        client_writer.write(b'\x05\x00')
        await client_writer.drain()

        # ── 请求 ──
        ver, cmd, _rsv, atyp = await client_reader.readexactly(4)

        if cmd == 3:  # ── UDP ASSOCIATE ──
            if not udp_enabled:
                client_writer.write(b'\x05\x07\x00\x01' + b'\x00' * 6)
                await client_writer.drain()
                client_writer.close()
                return
            # 请求里的地址字段客户端一般填 0.0.0.0:0（它不知道自己会从哪发），
            # 忽略它，直接读掉保持协议同步。
            if atyp == 1:
                await client_reader.readexactly(4)
            elif atyp == 3:
                n = (await client_reader.readexactly(1))[0]
                await client_reader.readexactly(n)
            elif atyp == 4:
                await client_reader.readexactly(16)
            await client_reader.readexactly(2)

            relay = UdpRelay(peer_ip, peer[1] if peer else 0)
            port = await relay.start()
            log('%s UDP ASSOCIATE → 中继端口 %d' % (peer_ip, port))

            # 返回 BND.ADDR / BND.PORT。这里用 0.0.0.0 让客户端自己
            # 决定连哪个地址 —— 客户端可能是通过 WG、也可能是直连来的，
            # 服务端不知道它眼里自己的地址是什么。
            client_writer.write(b'\x05\x00\x00\x01' + b'\x00\x00\x00\x00'
                                + struct.pack('>H', port))
            await client_writer.drain()

            # 关联的生命周期绑定控制连接（RFC 1928）：
            # 一直读到 EOF 或出错，说明客户端断开了，再回收 UDP socket。
            while True:
                chunk = await client_reader.read(1024)
                if not chunk:
                    break
            return

        if cmd != 1:  # 只支持 CONNECT / UDP ASSOCIATE
            client_writer.write(b'\x05\x07\x00\x01' + b'\x00' * 6)
            await client_writer.drain()
            client_writer.close()
            return

        if atyp == 1:
            host = '.'.join(str(b) for b in await client_reader.readexactly(4))
        elif atyp == 3:
            n = (await client_reader.readexactly(1))[0]
            raw = await client_reader.readexactly(n)
            # SOCKS5 的域名是 IDNA 编码。注意 'idna' 是**编码名**，
            # 不能当 errors 参数用 —— 写成 decode('idna','ignore') 会抛
            # "Unsupported error handling: ignore"，连接直接断。
            try:
                host = raw.decode('idna')
            except Exception:
                host = raw.decode('ascii', 'ignore')
        elif atyp == 4:
            raw = await client_reader.readexactly(16)
            host = str(ipaddress.ip_address(raw))
        else:
            client_writer.write(b'\x05\x08\x00\x01' + b'\x00' * 6)
            await client_writer.drain()
            client_writer.close()
            return
        port = int.from_bytes(await client_reader.readexactly(2), 'big')

        # ── 连目标 ──
        try:
            up_reader, up_writer = await asyncio.wait_for(
                asyncio.open_connection(host, port), timeout=10)
        except Exception as e:
            log('%s → %s:%d 失败: %s' % (peer_ip, host, port, e))
            client_writer.write(b'\x05\x05\x00\x01' + b'\x00' * 6)
            await client_writer.drain()
            client_writer.close()
            return

        client_writer.write(b'\x05\x00\x00\x01' + b'\x00' * 6)
        await client_writer.drain()

        # 按源 IP 限速。上行下行**共用同一个 limiter** —— 它们是一个用户的
        # 总带宽，分别算等于把配额翻倍。
        limiter = await RATES.get(peer_ip)
        await asyncio.gather(
            pipe(client_reader, up_writer, 'up', counter, limiter),
            pipe(up_reader, client_writer, 'down', counter, limiter),
        )
        log('%s → %s:%d 结束，%d 字节' % (peer_ip, host, port, counter[0]))
    except (asyncio.IncompleteReadError, ConnectionResetError):
        pass
    except Exception as e:
        log('%s 异常: %s' % (peer_ip, e))
    finally:
        if relay:
            relay.close()
        try:
            client_writer.close()
        except Exception:
            pass


async def main():
    global RATES
    ap = argparse.ArgumentParser()
    ap.add_argument('--bind', default='0.0.0.0')
    ap.add_argument('--port', type=int, default=1080)
    ap.add_argument('--allow', default='10.0.0.0/8,172.16.0.0/12,192.168.0.0/16',
                    help='允许的来源网段，逗号分隔。默认全部私网')
    ap.add_argument('--udp', action='store_true',
                    help='启用 UDP ASSOCIATE（默认关闭，因为它是可选的暴露面）')
    ap.add_argument('--rate-mb', type=float, default=10.0,
                    help='每个源 IP 的带宽上限（MB/s）。0 = 不限速。默认 10')
    args = ap.parse_args()

    nets = []
    for part in args.allow.split(','):
        part = part.strip()
        if part:
            nets.append(ipaddress.ip_network(part, strict=False))

    rate_bps = int(max(0.0, args.rate_mb) * 1024 * 1024)
    RATES = RateTable(rate_bps)

    server = await asyncio.start_server(
        lambda r, w: handle(r, w, nets, args.udp), args.bind, args.port)
    addrs = ', '.join(str(s.getsockname()) for s in server.sockets)
    log('SOCKS5 监听 %s，允许来源 %s，UDP %s，限速 %s'
        % (addrs, ', '.join(str(n) for n in nets),
           '已启用' if args.udp else '未启用',
           ('%.1f MB/s / IP' % args.rate_mb) if rate_bps > 0 else '不限'))
    async with server:
        await server.serve_forever()


if __name__ == '__main__':
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
