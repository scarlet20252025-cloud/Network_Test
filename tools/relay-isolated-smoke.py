"""End-to-end isolated SOCKS5 TCP/UDP smoke test for the ferry relay."""
import socket
import struct
import sys
import threading
import time

HOST = '10.13.24.1'
PORT = int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 1081
ECHO_PORT = 19081


def run_echo_server():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(('127.0.0.1', ECHO_PORT))
    while True:
        data, peer = sock.recvfrom(4096)
        sock.sendto(data, peer)


def recvn(sock, size):
    data = bytearray()
    while len(data) < size:
        part = sock.recv(size - len(data))
        if not part:
            raise ConnectionError('unexpected EOF')
        data.extend(part)
    return bytes(data)


def socks_tcp_connect():
    sock = socket.create_connection((HOST, PORT), timeout=8)
    sock.settimeout(8)
    try:
        sock.sendall(b'\x05\x01\x00')
        if recvn(sock, 2) != b'\x05\x00':
            return False
        host = b'www.baidu.com'
        sock.sendall(b'\x05\x01\x00\x03' + bytes([len(host)]) + host + (80).to_bytes(2, 'big'))
        reply = recvn(sock, 4)
        atyp = reply[3]
        if atyp == 1:
            recvn(sock, 6)
        elif atyp == 3:
            recvn(sock, recvn(sock, 1)[0] + 2)
        elif atyp == 4:
            recvn(sock, 18)
        if reply[1] != 0:
            return False
        sock.sendall(b'GET / HTTP/1.0\r\nHost: www.baidu.com\r\nConnection: close\r\n\r\n')
        response = bytearray()
        while len(response) < 128:
            chunk = sock.recv(128 - len(response))
            if not chunk:
                break
            response.extend(chunk)
            if b'\r\n' in response:
                break
        return response.startswith(b'HTTP/')
    finally:
        sock.close()


def udp_domain_echo():
    ctrl = socket.create_connection((HOST, PORT), timeout=8)
    ctrl.settimeout(8)
    ctrl.sendall(b'\x05\x01\x00')
    if recvn(ctrl, 2) != b'\x05\x00':
        raise RuntimeError('SOCKS greeting failed')
    ctrl.sendall(b'\x05\x03\x00\x01' + b'\x00' * 6)
    header = recvn(ctrl, 4)
    if header[1] != 0:
        raise RuntimeError('UDP ASSOCIATE failed: %r' % header)
    if header[3] == 1:
        relay_ip = socket.inet_ntoa(recvn(ctrl, 4))
    elif header[3] == 3:
        relay_ip = recvn(ctrl, recvn(ctrl, 1)[0]).decode('ascii')
    elif header[3] == 4:
        relay_ip = socket.inet_ntop(socket.AF_INET6, recvn(ctrl, 16))
    else:
        raise RuntimeError('bad BND ATYP')
    relay_port = struct.unpack('>H', recvn(ctrl, 2))[0]
    if relay_ip in ('0.0.0.0', '::'):
        relay_ip = HOST
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.settimeout(3)
    domain = b'localhost'
    payload = b'IDNA-PARSER-UDP-PROBE'
    packet = b'\x00\x00\x00\x03' + bytes([len(domain)]) + domain + ECHO_PORT.to_bytes(2, 'big') + payload
    try:
        # The relay intentionally drops the first packet for a cold DNS name
        # while resolving it asynchronously. Retry until the cache is warm.
        for _ in range(8):
            udp.sendto(packet, (relay_ip, relay_port))
            try:
                data, _ = udp.recvfrom(4096)
            except socket.timeout:
                continue
            if len(data) < 4:
                continue
            if data[3] == 1:
                offset = 4 + 4 + 2
            elif data[3] == 3:
                if len(data) < 5:
                    continue
                offset = 4 + 1 + data[4] + 2
            elif data[3] == 4:
                offset = 4 + 16 + 2
            else:
                continue
            if len(data) >= offset and data[offset:] == payload:
                return True
        return False
    finally:
        udp.close()
        ctrl.close()


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == '--echo-server':
        run_echo_server()
    else:
        if len(sys.argv) > 1 and sys.argv[1].isdigit():
            PORT = int(sys.argv[1])
            mode = sys.argv[2] if len(sys.argv) > 2 else 'all'
        else:
            mode = sys.argv[1] if len(sys.argv) > 1 else 'all'
        tcp_ok = 0
        if mode in ('all', '--tcp-only'):
            for _ in range(100):
                try:
                    tcp_ok += int(socks_tcp_connect())
                except Exception:
                    pass
            print('TCP full CONNECT + HTTP response: %d/100' % tcp_ok)
        udp_ok = 0
        if mode in ('all', '--udp-only'):
            try:
                for _ in range(100):
                    udp_ok += int(udp_domain_echo())
            except Exception as e:
                print('UDP domain echo stopped: %s' % e)
            print('UDP ASSOCIATE + ATYP=3 localhost echo: %d/100' % udp_ok)
        if (mode in ('all', '--tcp-only') and tcp_ok < 100) or (mode == '--udp-only' and udp_ok < 100):
            raise SystemExit(1)
