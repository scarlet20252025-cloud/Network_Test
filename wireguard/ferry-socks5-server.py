"""渡口 · 远端 SOCKS5 服务端（给 WireGuard 线路用）

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

  代价：只有「认系统代理」的程序能走这条路。游戏那种直接连服务器的还是不行。
  但对浏览器 / 下载 / 视频来说，延迟从 132ms 降到 10ms 已经很有价值。

部署：
  放在远端跑，监听 0.0.0.0:1080（或指定 --bind）。
  只接受来自隧道网段的连接（默认 10.66.67.0/24）。
"""
import argparse
import asyncio
import ipaddress
import sys
import time


def log(msg):
    print('[%s] %s' % (time.strftime('%H:%M:%S'), msg), flush=True)


def allowed_peer(ip, nets):
    try:
        addr = ipaddress.ip_address(ip)
    except ValueError:
        return False
    return any(addr in n for n in nets)


async def pipe(reader, writer, label, counter):
    try:
        while True:
            data = await reader.read(65536)
            if not data:
                break
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


async def handle(client_reader, client_writer, nets):
    peer = client_writer.get_extra_info('peername')
    peer_ip = peer[0] if peer else '?'
    if not allowed_peer(peer_ip, nets):
        log('拒绝 %s（不在允许的隧道网段）' % peer_ip)
        client_writer.close()
        return

    counter = [0]
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
        if cmd != 1:  # 只支持 CONNECT
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

        await asyncio.gather(
            pipe(client_reader, up_writer, 'up', counter),
            pipe(up_reader, client_writer, 'down', counter),
        )
        log('%s → %s:%d 结束，%d 字节' % (peer_ip, host, port, counter[0]))
    except (asyncio.IncompleteReadError, ConnectionResetError):
        pass
    except Exception as e:
        log('%s 异常: %s' % (peer_ip, e))
    finally:
        try:
            client_writer.close()
        except Exception:
            pass


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--bind', default='0.0.0.0')
    ap.add_argument('--port', type=int, default=1080)
    ap.add_argument('--allow', default='10.66.67.0/24',
                    help='允许的来源网段，逗号分隔。默认只留隧道段')
    args = ap.parse_args()

    nets = []
    for part in args.allow.split(','):
        part = part.strip()
        if part:
            nets.append(ipaddress.ip_network(part, strict=False))

    server = await asyncio.start_server(
        lambda r, w: handle(r, w, nets), args.bind, args.port)
    addrs = ', '.join(str(s.getsockname()) for s in server.sockets)
    log('SOCKS5 监听 %s，允许来源 %s' % (addrs, ', '.join(str(n) for n in nets)))
    async with server:
        await server.serve_forever()


if __name__ == '__main__':
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
