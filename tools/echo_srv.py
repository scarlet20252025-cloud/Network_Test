"""TCP echo 服务，给 tools/measure-64hz.py 量链路往返用。

为什么要在 pcs 上跑：measure-64hz 要的是「本机 → pcs」这一段的纯网络往返，
echo 必须在 pcs 本机，否则量到的是 echo 服务与 pcs 之间的走法。

为什么用计划任务而不是 ssh 里 Start-Process：SSH 会话断开时 Windows 的
Job Object 会把子进程一起回收，服务看着起来了、实际已经没了。

用法（在 pcs 上）：
    schtasks /create /tn FerryEcho /tr "python C:\\Windows\\Temp\\echo_srv.py" /sc once /st 00:00 /f
    schtasks /run /tn FerryEcho
"""
import socket
import sys
import threading

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 19100


def handle(c):
    try:
        while True:
            d = c.recv(4096)
            if not d:
                break
            c.sendall(d)
    except Exception:
        pass
    finally:
        try:
            c.close()
        except Exception:
            pass


def main():
    s = socket.socket()
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(('0.0.0.0', PORT))
    s.listen(64)
    print('echo listening on %d' % PORT, flush=True)
    while True:
        try:
            c, _ = s.accept()
            # TCP_NODELAY：不然 32 字节的小包会被 Nagle 攒着，
            # 量出来的 RTT 里混进 40ms 的延迟，看起来像网络卡。
            c.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            threading.Thread(target=handle, args=(c,), daemon=True).start()
        except Exception as e:
            print('accept error: %s' % e, flush=True)


if __name__ == '__main__':
    main()
