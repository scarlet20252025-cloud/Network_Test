"""在远端跑一个自包含的故障模式测试（单脚本，远端本地完成）。

上一版从本机 ssh 启动测试实例失败（hs-fail 5 秒 = 连接超时，实例没起来）。
WMI 创建带多个参数的命令行容易被拆坏。

这版：把一个完整的 Python 测试脚本传上去，在远端本地跑 ——
它自己 subprocess 启动 relay、自己测、自己清理。
不经过 WMI，不经过命令行引号解析。
"""
import base64
import subprocess

TEST_SCRIPT = r'''
import os
import socket
import subprocess
import sys
import threading
import time

PY = sys.executable
RELAY_PY = r"C:\Users\PCserver\AppData\Local\Ferry\ferry-socks5-server.py"
RELAY_PORT = 10997
REP = {0: "ok", 1: "general", 2: "not-allowed", 3: "net-unreach",
       4: "host-unreach", 5: "refused", 6: "ttl", 7: "cmd-unsupported",
       8: "atyp-unsupported"}


def start_relay(clash_spec):
    p = subprocess.Popen(
        [PY, "-u", RELAY_PY, "--port", str(RELAY_PORT), "--udp",
         "--rate-mb", "0", "--clash-proxy", clash_spec],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        cwd=os.path.dirname(RELAY_PY))
    for _ in range(40):
        try:
            s = socket.create_connection(("127.0.0.1", RELAY_PORT), 0.3)
            s.close()
            return p
        except OSError:
            if p.poll() is not None:
                out = p.stdout.read(3000).decode("utf-8", "replace")
                raise RuntimeError("relay exited: %s" % out[-600:])
            time.sleep(0.25)
    p.kill()
    raise RuntimeError("relay timeout")


def stop_relay(p):
    try:
        p.kill()
        p.wait(timeout=5)
    except Exception:
        pass


def socks_try(host, port=443, timeout=25):
    t0 = time.time()
    try:
        s = socket.create_connection(("127.0.0.1", RELAY_PORT), timeout)
    except OSError as e:
        return ("tcp-fail:%s" % str(e)[:20], round(time.time() - t0, 2))
    s.settimeout(timeout)
    try:
        s.sendall(b"\x05\x01\x00")
        if s.recv(2) != b"\x05\x00":
            return ("hs-fail", round(time.time() - t0, 2))
        hb = host.encode("ascii")
        s.sendall(bytes([5, 1, 0, 3, len(hb)]) + hb + port.to_bytes(2, "big"))
        h = s.recv(4)
        if len(h) < 4:
            return ("short:%r" % h, round(time.time() - t0, 2))
        return (h[1], round(time.time() - t0, 2))
    except socket.timeout:
        return ("timeout", round(time.time() - t0, 2))
    except Exception as e:
        return ("err:%s" % str(e)[:25], round(time.time() - t0, 2))
    finally:
        try:
            s.close()
        except Exception:
            pass


class FakeClash:
    def __init__(self, mode, port):
        self.mode = mode
        self.port = port
        self.sock = None
        self.running = False

    def start(self):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind(("127.0.0.1", self.port))
        self.sock.listen(64)
        self.running = True
        threading.Thread(target=self._serve, daemon=True).start()

    def _serve(self):
        while self.running:
            try:
                c, _ = self.sock.accept()
            except OSError:
                break
            threading.Thread(target=self._handle, args=(c,), daemon=True).start()

    def _handle(self, c):
        try:
            if self.mode == "silent":
                time.sleep(20)
                c.close()
                return
            if self.mode == "slow":
                time.sleep(3.0)
                c.close()
                return
            if self.mode == "reject":
                c.recv(3)
                c.sendall(b"\x05\x00")
                c.recv(512)
                c.sendall(b"\x05\x05\x00\x01" + b"\x00" * 6)
                c.close()
                return
            c.close()
        except Exception:
            try:
                c.close()
            except Exception:
                pass

    def stop(self):
        self.running = False
        try:
            self.sock.close()
        except Exception:
            pass


def show(label, host):
    rep, t = socks_try(host)
    if isinstance(rep, int):
        print("    %-24s REP=%d(%-9s) %ss" % (host, rep, REP.get(rep, "?"), t))
        return rep, t
    print("    %-24s %-14s %ss" % (host, rep, t))
    return rep, t


# ── 场景 ──
def run_case(name, clash_spec, fake_mode=None, fake_port=None):
    print()
    print("=== %s ===" % name)
    fc = None
    if fake_mode:
        fc = FakeClash(fake_mode, fake_port)
        fc.start()
    relay = None
    try:
        relay = start_relay(clash_spec)
        time.sleep(0.3)
        show("baidu (direct 应通)", "www.baidu.com")
        show("google (direct 不通)", "www.google.com")
    except Exception as e:
        print("    [SETUP FAIL] %s" % str(e)[:200])
    finally:
        if relay:
            stop_relay(relay)
        if fc:
            fc.stop()


run_case("A. Clash 端口没人听（进程挂了）", "127.0.0.1:17999")
run_case("B. Clash 假死（连上不回）", "127.0.0.1:17998", "silent", 17998)
run_case("C. Clash 拒绝 CONNECT", "127.0.0.1:17997", "reject", 17997)
run_case("D. Clash 慢 3 秒", "127.0.0.1:17996", "slow", 17996)
run_case("E. 正常（真实 7890）", "127.0.0.1:7890")
run_case("F. 显式关闭", "off")
print()
print("done")
'''


def run_remote(ps, timeout=300):
    enc = base64.b64encode(ps.encode('utf-16-le')).decode()
    r = subprocess.run(
        ['ssh', '-o', 'ConnectTimeout=8', '-o', 'BatchMode=yes',
         '-o', 'LogLevel=ERROR', 'pcs',
         'powershell -NoProfile -EncodedCommand ' + enc],
        capture_output=True, text=True, timeout=timeout)
    return (r.stdout or '')


# 把测试脚本用 base64 传上去再跑（避免编码/引号问题）
b64 = base64.b64encode(TEST_SCRIPT.encode('utf-8')).decode()
PS = (
    "$p = 'C:\\Users\\PCserver\\AppData\\Local\\Ferry\\_fail_test.py'\n"
    "[IO.File]::WriteAllBytes($p, [Convert]::FromBase64String('" + b64 + "'))\n"
    "$py = 'C:\\Users\\PCserver\\AppData\\Local\\Python\\pythoncore-3.14-64\\python.exe'\n"
    "& $py -u $p\n"
    "Remove-Item $p -Force -ErrorAction SilentlyContinue\n"
)

print(run_remote(PS, timeout=420))
