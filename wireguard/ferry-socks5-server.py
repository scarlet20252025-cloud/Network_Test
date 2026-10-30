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
import os
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


# ═══════════════════════════════════════════════════════════════════
# UDP 目标的 DNS 解析缓存 —— 修「延迟缓慢爬升再骤降」
# ═══════════════════════════════════════════════════════════════════
#
# **问题（2026-10-30 实测）**：
#   UDP 转发走的是 `_target_transport.sendto(payload, (host, port))`，而
#   `host` 来自 SOCKS5 请求头的 ATYP=3（域名）。asyncio 的
#   `DatagramTransport.sendto()` 在地址不是 IP 字面量时会**同步**调用
#   `socket.getaddrinfo()` —— 实测冷域名 38.90ms，传 IP 只要 0.38ms。
#
#   中继是**单线程 asyncio**：这 38ms 里整个事件循环停摆，所有其它关联的
#   包全部排队。而 mihomo 给每个 UDP 目标各建一条 ASSOCIATE，域名各不相同，
#   每个包都是冷解析。
#
#   表现（用户实测）：延迟从 10ms 稳定爬升到 50-70ms，然后突然降回 10ms，
#   循环往复 —— 正是「解析任务堆积 → 队列变长 → 缓存命中/队列清空」的形态。
#
# **修法**：
#   1. 本地维护 host→IP 缓存（带 TTL），命中就不再进 getaddrinfo；
#   2. 未命中时把解析丢到线程池（`run_in_executor`），**不阻塞事件循环**；
#   3. 解析完成前该包先丢弃（UDP 本来就允许丢包，比卡住所有人好），
#      但同时把解析请求合并 —— 同一个域名的后续包不会重复触发解析。
#
#   这不是「优化」，是修一个有明确测量支撑的阻塞缺陷。
#
# 缓存 TTL 取值依据：UDP 目标多是游戏/语音/QUIC，DNS 记录变化不频繁。
# 300 秒足够覆盖一局游戏，又不会让解析结果过时太久。
DNS_CACHE_TTL = 300.0

# host -> (ip, 过期时间戳)
_dns_cache = {}
# host -> True，表示解析正在进行（去重，避免同一个域名并发解析几十次）
_dns_pending = set()
_dns_executor = None


def _dns_executor_global():
    """懒加载线程池。放独立函数里，避免模块级创建线程（fork/重载时更安全）。"""
    global _dns_executor
    if _dns_executor is None:
        from concurrent.futures import ThreadPoolExecutor
        _dns_executor = ThreadPoolExecutor(max_workers=8,
                                           thread_name_prefix='ferry-dns')
    return _dns_executor


def _dns_cache_get(host):
    """缓存命中返回 IP，否则 None。"""
    e = _dns_cache.get(host)
    if e is None:
        return None
    ip, exp = e
    if exp < time.monotonic():
        _dns_cache.pop(host, None)
        return None
    return ip


def _dns_cache_put(host, ip):
    if len(_dns_cache) > 4096:          # 简单上限，防止无限增长
        # 清掉已过期的
        now = time.monotonic()
        for k in [k for k, v in _dns_cache.items() if v[1] < now]:
            _dns_cache.pop(k, None)
        if len(_dns_cache) > 4096:
            _dns_cache.clear()
    _dns_cache[host] = (ip, time.monotonic() + DNS_CACHE_TTL)


def _resolve_sync(host, port):
    """在线程池里跑的阻塞解析。"""
    try:
        infos = socket.getaddrinfo(host, port, socket.AF_INET, socket.SOCK_DGRAM)
        if infos:
            return infos[0][4][0]
    except Exception:
        pass
    return None


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

    def set_rate(self, rate_bps):
        """热更新限速值。

        为什么需要：管理员在面板上改限速时，不能重启中继 ——
        那会掐断所有正在传的连接（用户正在下载的文件直接断）。
        这里就地改所有已有 limiter 的 rate，下个包立刻按新速率走。

        rate 从有到无（设 0）时，已有 limiter 的 rate 也置 0，
        `throttle()` 会立刻返回不等待，效果等同解除限速。
        """
        self.rate_bps = rate_bps
        for lim in self.map.values():
            lim.rate = float(rate_bps or 0)

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

# ── UDP 关联登记 ──
#
# 每个关联占 **2 个 UDP 端口**（client_sock + target_sock）。
# Windows 动态端口范围默认 49152~65535 = 16384 个，
# 也就是最多约 8000 个关联 —— 听起来很多，但客户端如果每次都新建关联
# （mihomo 在某些情况下会），几分钟就能堆到几千，然后新连接全部 bind 失败。
#
# 更要命的是原来**没有回收**：UDP_IDLE_TIMEOUT 定义了却没被任何代码使用，
# last_active 只写不读。客户端异常断开（不发 FIN）时关联会一直挂到
# TCP 超时（几分钟）。这就是「正常使用不下载也会卡」的潜在原因之一。
_relays = set()
RELAY_HARD_LIMIT = 3000     # 硬上限，超了直接拒绝新的 ASSOCIATE
_relay_reaper_started = False

# 全局总带宽上限（所有 IP 加起来）。0 = 不限。
#
# 为什么需要这一层：每 IP 限 10 MB/s，5 个人同时满速就是 50 MB/s ——
# 远端的物理出口（校园网到联通）根本扛不住，结果是所有人都卡。
# 每人都有了配额之后，还要有个「总阀门」兜住出口。
GLOBAL = RateLimiter(0)

# 热重载的配置文件。两行：
#   第 1 行：每 IP 多少 MB/s（0 = 不限）
#   第 2 行：全局多少 MB/s（0 = 不限，可省略）
# 跟中继脚本同目录，部署脚本会建。
RATE_CONF_NAME = 'rate-mb.txt'
_rate_conf_mtime = -1.0


# --- Outbound bypass: fall back to local Clash when direct fails ---
#
# Why: the school blocks direct access to overseas sites (google/youtube all
# time out), while the remote host runs Clash which CAN reach them. Borrowing
# that exit means users do not have to configure a second proxy themselves.
#
# Why "concurrent race" instead of "direct first, Clash on failure":
#   Serial would make blocked sites wait for the direct timeout (~3s), so a
#   page load would just sit there. With racing: domestic sites win via direct
#   (~100ms), blocked sites win via Clash (~300ms). Nobody waits.
#
# Why not "decide by target IP (CN vs overseas)":
#   Maintaining an IP table is unrealistic, and a wrong guess makes domestic
#   sites SLOWER (a pointless round trip abroad). Measured conclusion:
#   "if direct works, Clash works too; if direct fails, only Clash works".
#   Racing costs domestic sites nothing and covers blocked sites for free.
CLASH_PROXY = None              # (host, port) or None (disabled)
CLASH_FALLBACK_TIMEOUT = 0.9    # direct attempt timeout
DIRECT_HEAD_START = 0.20        # how long direct may win exclusively

# Timing budget (measured on the real link):
#   domestic sites complete the TCP connect in ~100ms -> 0.20s head start
#     is still a comfortable win for them.
#   blocked sites fail the direct attempt within ~0.9s (either no route or
#     RST), so a 0.9s cap costs nothing but keeps the worst case near 1.1s
#     instead of the 2.0s that 0.35+1.2 produced.
#
# Two separate timeouts, because the two phases are fundamentally different
# (this was measured -- using one value for both caused an 8s stall):
#   * connect to local Clash + SOCKS5 handshake = pure loopback, milliseconds.
#     Giving it a long timeout means a dead-but-listening Clash stalls us.
#   * waiting for the CONNECT reply = Clash has to actually reach the target,
#     which can take seconds on a slow node.
# TCP connect to the local proxy. Loopback either accepts in <5ms or the
# listener is gone; measured: connecting to a dead local port on Windows
# does NOT return RST immediately, it burns the whole timeout. So this value
# IS the failure latency -- keep it tight (0.8s = 160x margin over 5ms).
CLASH_TCP_TIMEOUT = 0.8
# Waiting for the SOCKS5 greeting reply. A real Clash under load may take a
# moment; this one can be generous because we only reach it after TCP is up.
CLASH_HANDSHAKE_TIMEOUT = 2.0
# Waiting for Clash to actually reach the target (slow nodes need seconds).
CLASH_CONNECT_TIMEOUT = 8.0

# Direct-only timeout when the bypass is disabled. 10s feels broken to users;
# domestic sites answer within ~100ms, so 3s is plenty to call it dead.
DIRECT_ONLY_TIMEOUT = 3.0

_clash_ok_cache = {'v': None, 'at': 0.0}


def _probe_tcp(host, port, timeout=0.5):
    """Synchronous port probe. Startup only, never in the event loop."""
    try:
        s = socket.create_connection((host, port), timeout)
        s.close()
        return True
    except OSError:
        return False


def _resolve_clash_proxy(spec):
    """Parse --clash-proxy into (host, port) or None.

    auto: probe common ports (7890 Clash/mihomo, 7891 socks-port,
          7897 Verge, 10808). Returns None when nothing answers, which
          silently disables the bypass instead of breaking borrowing.
    off:  disabled explicitly.
    else: parse "host:port".
    """
    spec = (spec or '').strip().lower()
    if spec in ('off', ''):
        return None
    if spec == 'auto':
        for port in (7890, 7891, 7897, 10808):
            if _probe_tcp('127.0.0.1', port):
                return ('127.0.0.1', port)
        return None
    if ':' in spec:
        h, _, p = spec.rpartition(':')
        try:
            return (h or '127.0.0.1', int(p))
        except ValueError:
            return None
    return None


async def _open_via_clash(host, port):
    """Connect to target through the local Clash SOCKS5 port.

    Clash's mixed-port speaks both HTTP and SOCKS5, so we just talk SOCKS5.
    Returns None on any failure (caller treats it as "this path is down").
    """
    if not CLASH_PROXY:
        return None
    chost, cport = CLASH_PROXY
    try:
        r, w = await asyncio.wait_for(
            asyncio.open_connection(chost, cport), timeout=CLASH_TCP_TIMEOUT)
    except Exception:
        return None
    try:
        w.write(b'\x05\x01\x00')
        await w.drain()
        resp = await asyncio.wait_for(r.readexactly(2), timeout=CLASH_HANDSHAKE_TIMEOUT)
        if resp[0] != 5 or resp[1] != 0:
            w.close()
            return None

        req = bytearray([5, 1, 0])
        ipobj = None
        try:
            ipobj = ipaddress.ip_address(host)
        except ValueError:
            pass
        if ipobj is not None and ipobj.version == 4:
            req.append(1)
            req += ipobj.packed
        elif ipobj is not None and ipobj.version == 6:
            req.append(4)
            req += ipobj.packed
        else:
            try:
                hb = host.encode('idna') if not host.isascii() else host.encode('ascii')
            except Exception:
                hb = host.encode('ascii', 'ignore')
            if len(hb) > 255:
                w.close()
                return None
            req.append(3)
            req.append(len(hb))
            req += hb
        req += port.to_bytes(2, 'big')
        w.write(bytes(req))
        await w.drain()

        head = await asyncio.wait_for(r.readexactly(4), timeout=CLASH_CONNECT_TIMEOUT)
        if head[1] != 0:
            w.close()
            return None
        atyp = head[3]
        if atyp == 1:
            await asyncio.wait_for(r.readexactly(6), timeout=CLASH_HANDSHAKE_TIMEOUT)
        elif atyp == 3:
            ln = (await asyncio.wait_for(r.readexactly(1), timeout=CLASH_HANDSHAKE_TIMEOUT))[0]
            await asyncio.wait_for(r.readexactly(ln + 2), timeout=CLASH_HANDSHAKE_TIMEOUT)
        elif atyp == 4:
            await asyncio.wait_for(r.readexactly(18), timeout=CLASH_HANDSHAKE_TIMEOUT)
        else:
            w.close()
            return None
        return r, w
    except Exception as e:
        # 记日志：之前这里静默 return None，排查「为什么没走 Clash」时
        # 完全看不出原因（可能是节点挂了、被限流、握手被拒）。
        #
        # 去重键也必须用 _exc_text：原先用 str(e)，而超时的 str() 是空串，
        # 于是 `last_err` 一旦被置成 ''，后面所有超时都被当成「已经记过」
        # 而永远不再输出 —— 正是最该看到的那一类被永久静音。
        key = _exc_text(e)[:60]
        if _clash_ok_cache.get('last_err') != key:
            _clash_ok_cache['last_err'] = key
            log('clash path failed: %s' % key)
        try:
            w.close()
        except Exception:
            pass
        return None


async def _open_upstream(host, port, peer_ip):
    """Connect to target: direct gets a head start, Clash is the fallback.

    Why a head start instead of a pure race:
      Clash answers its own local handshake almost instantly, so a pure race
      lets it steal DOMESTIC sites too -- that routes baidu abroad and back:
      slower AND it burns the proxy plan's quota. Measured exactly that.

    Why not "wait for direct to fully fail":
      That is back to waiting the full 1.2s timeout on every blocked site.

    Note: asyncio.wait(timeout=...) does NOT cancel on timeout (unlike
    wait_for), so the direct attempt keeps running into phase 2.

    Returns (reader, writer, via), via is 'direct' or 'clash'.
    """
    if not CLASH_PROXY:
        r, w = await asyncio.wait_for(
            asyncio.open_connection(host, port), timeout=DIRECT_ONLY_TIMEOUT)
        return r, w, 'direct'

    async def try_direct():
        return await asyncio.wait_for(
            asyncio.open_connection(host, port),
            timeout=CLASH_FALLBACK_TIMEOUT)

    async def try_clash():
        got = await _open_via_clash(host, port)
        if got is None:
            raise ConnectionError('clash unavailable')
        return got

    t_direct = asyncio.ensure_future(try_direct())
    t_clash = asyncio.ensure_future(try_clash())
    by_task = {t_direct: 'direct', t_clash: 'clash'}

    winner = None
    errors = []
    try:
        # ---- Phase 1: direct has a head start ----
        # Wait for direct to finish OR for the window to expire.
        # asyncio.wait_for here wraps only the *wait*, so the direct task
        # itself keeps running into phase 2 (wait_for cancels what it wraps,
        # and what it wraps is the inner wait coroutine, not the task).
        try:
            await asyncio.wait_for(
                asyncio.wait({t_direct}, return_when=asyncio.FIRST_COMPLETED),
                timeout=DIRECT_HEAD_START)
        except asyncio.TimeoutError:
            pass

        # direct wins outright if it connected
        if t_direct.done() and not t_direct.cancelled() and t_direct.exception() is None:
            winner = t_direct
        else:
            if t_direct.done() and not t_direct.cancelled() and t_direct.exception() is not None:
                errors.append(t_direct.exception())

            # ---- Phase 2 ----
            # First: did Clash already finish during phase 1? If so, use it.
            # (An earlier version filtered completed tasks out here and lost
            #  the ready Clash connection -- every blocked site then failed.)
            if t_clash.done() and not t_clash.cancelled():
                exc = t_clash.exception()
                if exc is None:
                    winner = t_clash
                else:
                    errors.append(exc)

            # Otherwise wait for whichever of the two is still running.
            if winner is None:
                race = set(t for t in (t_direct, t_clash) if not t.done())
                while race and winner is None:
                    done2, race = await asyncio.wait(
                        race, return_when=asyncio.FIRST_COMPLETED)
                    for t in done2:
                        if t.cancelled():
                            continue
                        exc = t.exception()
                        if exc is not None:
                            errors.append(exc)
                            continue
                        winner = t
                        break

        # ---- Cancel everything except the winner ----
        for t in (t_direct, t_clash):
            if winner is not None and t is winner:
                continue
            if not t.done():
                t.cancel()
        for t in (t_direct, t_clash):
            if winner is not None and t is winner:
                continue
            try:
                await asyncio.gather(t, return_exceptions=True)
            except Exception:
                pass

        if winner is None:
            raise errors[0] if errors else ConnectionError('all upstreams failed')

        via = by_task[winner]
        r, w = winner.result()

        if via == 'clash' and _clash_ok_cache['v'] is not True:
            _clash_ok_cache['v'] = True
            _clash_ok_cache['at'] = time.time()
            log('direct failed, auto-routing via local Clash (e.g. %s:%d)'
                % (host, port))

        return r, w, via
    finally:
        for t in (t_direct, t_clash):
            if not t.done():
                t.cancel()


def log(msg):
    print('[%s] %s' % (time.strftime('%H:%M:%S'), msg), flush=True)


def _exc_text(e):
    """把异常变成一行有意义的原因文字。

    为什么必须有这个（2026-10-30 实测）：
      `asyncio.TimeoutError` 在 3.11+ 就是内置 `TimeoutError`，而
      **`str(TimeoutError())` 是空字符串** —— 它是 OSError 家族里唯一这样的。
      所以 `log('... 失败: %s' % e)` 会输出「失败: 」，后面什么都没有。

      实测 pcs 上 2 MB 日志（28792 行）里，失败/异常行共 3685 条，
      其中 **3625 条原因为空（98%）**。这些行正好是最需要诊断的那批
      （googleapis / chatgpt 之类连不上），结果等于只告诉你有失败、
      不告诉你为什么失败 —— 分不清是超时、被拒、还是 DNS 挂了。

    为什么还要拼上类型名：
      同一个空原因既可能是 TimeoutError（等满了 0.9s 超时），
      也可能是 OSError 的空串分支，只有类型名能区分。有内容的异常
      （如 `[WinError 10054] 远程主机强迫关闭了一个现有的连接`）已经
      自带类型线索，就不要再前缀一遍，否则变成
      「ConnectionResetError: ConnectionResetError [WinError...]」。
    """
    name = type(e).__name__
    text = str(e).strip()
    if not text:
        return name
    if text.startswith(name):
        return text
    return '%s: %s' % (name, text)


# ── UDP 关联建立的日志降噪 ──
#
# 为什么：mihomo 给每个 UDP 目标各建一条 ASSOCIATE，单机每秒能建十几条。
# 原来每条都 log()，而 log() 是 print(flush=True) —— 强制刷盘。
# 中继是单线程 asyncio，刷盘期间整个事件循环停摆，所有 UDP 转发都要等，
# 表现为「几百毫秒的周期性大跳」，日志文件几分钟涨到 62 MB。
#
# 改成每 30 秒最多一行，并且只在「关联数创新高」时报告峰值。
_ASSOC_LOG_INTERVAL = 30.0
_assoc_log_state = {'next': 0.0, 'opened': 0, 'peak': 0}


def _log_assoc_open(peer_ip, port, cur):
    st = _assoc_log_state
    st['opened'] += 1
    if cur > st['peak']:
        st['peak'] = cur
    now = time.monotonic()
    if now < st['next']:
        return
    st['next'] = now + _ASSOC_LOG_INTERVAL
    log('UDP 关联 %d 条（峰值 %d），本周期新建 %d 条（最近一条 %s:%d）'
        % (cur, st['peak'], st['opened'], peer_ip, port))
    st['opened'] = 0


# 回环永远放行。
#
# 为什么：本机连自己的端口时源 IP 是 127.0.0.1，而 --allow 默认只有私网段
# （10/8、172.16/12、192.168/16），于是「自己探测自己」被自己的规则拒绝 ——
# 实测日志里堆了 60 多条「拒绝 127.0.0.1」，把真正的问题都埋了。
#
# 放行回环不增加任何暴露面：能连 127.0.0.1 的进程本来就已经在这台机上，
# 有完全控制权。
_LOOPBACK = None

# 已经记过「拒绝」的源 IP。只用于抑制日志噪音，不参与鉴权。
_rejected_seen = set()


def _is_loopback(addr):
    global _LOOPBACK
    try:
        return addr.is_loopback
    except Exception:
        return False


def allowed_peer(ip, nets):
    try:
        addr = ipaddress.ip_address(ip)
    except ValueError:
        return False
    if _is_loopback(addr):
        return True
    return any(addr in n for n in nets)


async def pipe(reader, writer, label, counter, limiter=None):
    try:
        while True:
            data = await reader.read(65536)
            if not data:
                break
            # 先限速再写：反过来的话数据已经进内核缓冲区了，
            # 那一段等于没限（实测双方向都这样会明显超速）。
            #
            # 两层限速串联：先过「全站总阀门」，再过「这个 IP 的配额」。
            # 顺序不重要（都是等待），但两级都过才能同时满足
            # 「每人不超过 X」和「所有人加起来不超过 Y」。
            if GLOBAL.rate > 0:
                await GLOBAL.throttle(len(data))
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
        self._on_close_cb = on_close
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
            log('UDP 头解析失败: %s' % _exc_text(e))
            return
        if not payload:
            return
        self.stats['up'] += len(payload)
        self.stats['pkts'] += 1
        # 目标可能是域名（ATYP=3）。**绝对不能**直接 sendto(host, port) ——
        # asyncio 会在事件循环里同步 getaddrinfo，实测 38.90ms 全停摆。
        # 见文件上方 DNS 缓存那段的注释。
        self._send_to_target(host, port, payload)

    def _send_to_target(self, host, port, payload):
        """
        发往目标。域名先查缓存；未命中就异步解析 + 丢这一包。

        为什么丢包而不是排队：UDP 本来允许丢包，游戏/QUIC 都有重传。
        排队会让**所有关联**一起等，丢一个包只影响这一个流 —— 后者代价小得多。
        解析完成后同一个域名的后续包立刻走缓存，只丢第一次。
        """
        # 是不是 IP 字面量？是就直接发（最常见，零开销）
        try:
            socket.inet_aton(host)
            try:
                self._target_transport.sendto(payload, (host, port))
            except Exception as e:
                log('UDP 发往 %s:%d 失败: %s' % (host, port, _exc_text(e)))
            return
        except OSError:
            pass

        ip = _dns_cache_get(host)
        if ip is not None:
            try:
                self._target_transport.sendto(payload, (ip, port))
            except Exception as e:
                log('UDP 发往 %s:%d 失败: %s' % (ip, port, _exc_text(e)))
            return

        # 域名且未缓存：触发一次异步解析，这一包丢弃
        if host not in _dns_pending:
            _dns_pending.add(host)

            def _done(fut, h=host):
                _dns_pending.discard(h)
                try:
                    got = fut.result()
                except Exception:
                    got = None
                if got:
                    _dns_cache_put(h, got)

            loop = asyncio.get_event_loop()
            fut = loop.run_in_executor(_dns_executor_global(), _resolve_sync, host, port)
            fut.add_done_callback(_done)

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
            log('UDP 回发客户端失败: %s' % _exc_text(e))

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
            self.on_close(self)


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
            log('UDP 回调异常: %s' % _exc_text(e))

    def error_received(self, exc):
        # 静默忽略：这是「对端端口不可达」，属于正常的 UDP 语义
        pass


# ────────────────────────── TCP CONNECT ──────────────────────────

async def handle(client_reader, client_writer, nets, udp_enabled):
    peer = client_writer.get_extra_info('peername')
    peer_ip = peer[0] if peer else '?'
    if not allowed_peer(peer_ip, nets):
        # 同一个 IP 只记第一次拒绝。
        # 扫描器/误配置会连续撞很多次，全记下来会把日志刷满，
        # 真正有用的记录（正常连接的断开、限速命中）就被埋了。
        if peer_ip not in _rejected_seen:
            _rejected_seen.add(peer_ip)
            log('拒绝 %s（不在允许的网段，后续同 IP 不再记录）' % peer_ip)
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

            if len(_relays) >= RELAY_HARD_LIMIT:
                log('%s UDP ASSOCIATE 被拒：关联数已达上限 %d'
                    % (peer_ip, RELAY_HARD_LIMIT))
                client_writer.write(b'\x00' + b'\x00' * 6)
                await client_writer.drain()
                client_writer.close()
                return

            relay = UdpRelay(peer_ip, peer[1] if peer else 0,
                             on_close=lambda r: _relays.discard(r))
            port = await relay.start()
            _relays.add(relay)
            # ⚠ 这里**不能**每建一条关联就写日志。
            #
            # 2026-10-30 实测：mihomo 给每个 UDP 目标各建一条 ASSOCIATE，
            # 单机 1 秒能建 16 条、常驻 138 条；这个 log() 是
            # print(..., flush=True) —— 每条都强制刷盘，而中继是单线程 asyncio，
            # 刷盘期间**整个事件循环停摆**，所有 UDP 包的转发都得等。
            # 表现就是「平时还行，偶尔几百毫秒大跳」，日志文件几分钟涨到 62 MB。
            #
            # 改成「每 30 秒汇总一行」，只在关联数创新高时提一句峰值。
            _log_assoc_open(peer_ip, port, len(_relays))

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

        # -- connect target (direct vs Clash race) --
        try:
            up_reader, up_writer, via = await _open_upstream(host, port, peer_ip)
        except Exception as e:
            log('%s → %s:%d 失败: %s' % (peer_ip, host, port, _exc_text(e)))
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
        log('%s -> %s:%d done, %d bytes (%s)'
            % (peer_ip, host, port, counter[0], via))
    except (asyncio.IncompleteReadError, ConnectionResetError):
        pass
    except Exception as e:
        log('%s 异常: %s' % (peer_ip, _exc_text(e)))
    finally:
        if relay:
            relay.close()
        try:
            client_writer.close()
        except Exception:
            pass


async def reap_idle_relays():
    """定期回收空闲的 UDP 关联。

    为什么必须有这个：
      每个关联占 2 个 UDP 端口（client_sock + target_sock）。
      关联的生命周期绑定 TCP 控制连接 —— 客户端正常断开时控制连接 EOF，
      关联立刻关闭。但客户端**异常**断开（进程被杀、网线拔掉、休眠）时
      没有 FIN，TCP 要等好几分钟才超时，这期间那两个端口一直占着。

      原来 UDP_IDLE_TIMEOUT 常量定义了却没人用，last_active 只写不读 ——
      等于没有回收。实测日志里几秒一个 ASSOCIATE，正常用一整天能堆几千个，
      Windows 动态端口（默认 16384 个）迟早被吃光，之后新连接全部 bind 失败。

    为什么按「最近有没有流量」而不是「创建了多久」：
      一个正在挂着长连接的游戏会一直有包（心跳），不该被回收；
      而创建了很久但早就没流量的关联才是垃圾。
    """
    global _relay_reaper_started
    if _relay_reaper_started:
        return
    _relay_reaper_started = True
    while True:
        await asyncio.sleep(30)
        try:
            now_t = time.time()
            dead = [r for r in list(_relays)
                    if not r.closed and now_t - r.last_active > UDP_IDLE_TIMEOUT]
            for r in dead:
                try:
                    r.close()
                except Exception:
                    pass
            if dead:
                log('回收空闲 UDP 关联 %d 个（剩 %d）' % (len(dead), len(_relays)))
        except Exception as e:
            log('UDP 关联回收出错（忽略）: %s' % _exc_text(e))


async def watch_rate_conf(conf_path, initial_mb):
    """盯着限速配置文件，变了就地热更新。

    文件格式（纯文本两行数字）：
        10      <- 每 IP 上限 MB/s（0 = 不限）
        0       <- 全站总上限 MB/s（0 或省略 = 不限）

    为什么用文件而不是控制端口：
      门户和中继都在同一台机器上，文件是最简单、最不会挂的通道 ——
      不需要额外监听端口（少一个暴露面），也不用处理鉴权。
      门户写文件、这里读，中间没有任何协议。

    为什么每 5 秒轮询而不是文件通知：
      Windows 上跨进程文件变更通知要走 win32 API，标准库里没有；
      5 秒延迟对「调限速」这种低频操作完全够用。
    """
    global _rate_conf_mtime
    while True:
        try:
            if os.path.exists(conf_path):
                mt = os.stat(conf_path).st_mtime
                if mt != _rate_conf_mtime:
                    _rate_conf_mtime = mt
                    raw = open(conf_path, encoding='utf-8').read()
                    lines = [x.strip() for x in raw.splitlines()]

                    def num(idx, dflt):
                        if len(lines) <= idx or not lines[idx]:
                            return dflt
                        try:
                            return max(0.0, float(lines[idx].split()[0]))
                        except (ValueError, IndexError):
                            return dflt

                    per_ip = num(0, initial_mb)
                    total = num(1, 0.0)

                    RATES.set_rate(int(per_ip * 1024 * 1024))
                    # 全局没有 per-IP 那种对象表要同步，直接改一个 float
                    GLOBAL.rate = float(int(total * 1024 * 1024))

                    log('限速已热更新: 每 IP %s，全站 %s'
                        % ((('%.2f MB/s' % per_ip) if per_ip > 0 else '不限'),
                           (('%.2f MB/s' % total) if total > 0 else '不限')))
        except Exception as e:
            # 文件正被写一半、或内容不是数字 —— 忽略这一轮，下轮再试。
            # 绝不能因为读配置失败就让这个循环退出（那就再也不热更新了）。
            log('读限速配置失败（忽略）: %s' % _exc_text(e))
        await asyncio.sleep(5)

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
                    help='per-IP bandwidth cap in MB/s. 0 = unlimited. Default 10')
    # 默认 **off**：渡口只做「借一台能上网的校内主机」这一个用途，
    # 不做翻墙出口。历史默认是 auto —— 直连失败就把流量交给本机 Clash 竞速，
    # 等于任何被墙的站点都能借这条路出去，这正是 2026-10-27 用户要求取消的。
    # 需要时可以用 --clash-proxy auto 显式开回来。
    ap.add_argument('--clash-proxy', default='off',
                    help='local proxy used when direct fails, e.g. 127.0.0.1:7890. '
                         'auto (default) = probe common ports; off = disable')
    args = ap.parse_args()

    nets = []
    for part in args.allow.split(','):
        part = part.strip()
        if part:
            nets.append(ipaddress.ip_network(part, strict=False))

    rate_bps = int(max(0.0, args.rate_mb) * 1024 * 1024)
    RATES = RateTable(rate_bps)

    global CLASH_PROXY
    CLASH_PROXY = _resolve_clash_proxy(args.clash_proxy)

    conf_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), RATE_CONF_NAME)

    server = await asyncio.start_server(
        lambda r, w: handle(r, w, nets, args.udp), args.bind, args.port)
    addrs = ', '.join(str(s.getsockname()) for s in server.sockets)
    log('SOCKS5 监听 %s，允许来源 %s，UDP %s，限速 %s'
        % (addrs, ', '.join(str(n) for n in nets),
           '已启用' if args.udp else '未启用',
           ('%.1f MB/s / IP' % args.rate_mb) if rate_bps > 0 else '不限'))
    log('rate config: %s (hot reload, no restart needed)' % conf_path)
    if CLASH_PROXY:
        log('outbound bypass: race to %s:%d when direct fails' % CLASH_PROXY)
    else:
        log('outbound bypass: disabled (no local proxy found, or --clash-proxy off)')

    watcher = asyncio.ensure_future(watch_rate_conf(conf_path, args.rate_mb))
    reaper = asyncio.ensure_future(reap_idle_relays())
    try:
        async with server:
            await server.serve_forever()
    finally:
        watcher.cancel()
        reaper.cancel()


if __name__ == '__main__':
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
