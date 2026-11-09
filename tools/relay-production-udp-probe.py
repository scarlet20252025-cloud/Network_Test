"""Probe production SOCKS5 UDP with an ATYP=3 DNS destination."""
import socket
import struct
import sys
import time

RELAY = ('10.13.24.1', int(sys.argv[1]) if len(sys.argv) > 1 else 1080)
TARGET = '223.5.5.5'
PORT = 53


def recvn(sock, n):
    out = bytearray()
    while len(out) < n:
        part = sock.recv(n - len(out))
        if not part:
            raise ConnectionError('EOF')
        out.extend(part)
    return bytes(out)


def read_addr(sock, atyp):
    if atyp == 1:
        return socket.inet_ntoa(recvn(sock, 4))
    if atyp == 3:
        return recvn(sock, recvn(sock, 1)[0]).decode('ascii')
    if atyp == 4:
        return socket.inet_ntop(socket.AF_INET6, recvn(sock, 16))
    raise ValueError('BND ATYP=%d' % atyp)


def dns_query(txid):
    # Query www.qq.com A, with a unique transaction id per attempt.
    q = struct.pack('>HHHHHH', txid, 0x0100, 1, 0, 0, 0)
    q += b'\x03www\x02qq\x03com\x00' + struct.pack('>HH', 1, 1)
    return q


def main():
    ctrl = socket.create_connection(RELAY, 8)
    ctrl.settimeout(3)
    try:
        ctrl.sendall(b'\x05\x01\x00')
        if recvn(ctrl, 2) != b'\x05\x00':
            raise RuntimeError('greeting failed')
        ctrl.sendall(b'\x05\x03\x00\x01' + b'\x00' * 6)
        head = recvn(ctrl, 4)
        if head[1] != 0:
            raise RuntimeError('associate REP=%d' % head[1])
        relay_ip = read_addr(ctrl, head[3])
        relay_port = struct.unpack('>H', recvn(ctrl, 2))[0]
        if relay_ip in ('0.0.0.0', '::'):
            relay_ip = RELAY[0]
        print('ASSOCIATE relay=%s:%d' % (relay_ip, relay_port))

        udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        udp.settimeout(2)
        try:
            ok = 0
            for i in range(10):
                txid = 0x4000 + i
                query = dns_query(txid)
                packet = (b'\x00\x00\x00\x03' + bytes([len(TARGET)]) +
                          TARGET.encode('ascii') + struct.pack('>H', PORT) + query)
                t0 = time.monotonic()
                udp.sendto(packet, (relay_ip, relay_port))
                try:
                    data, _ = udp.recvfrom(4096)
                except socket.timeout:
                    print('TRY %d timeout' % (i + 1))
                    continue
                elapsed = (time.monotonic() - t0) * 1000
                if len(data) < 12:
                    print('TRY %d short=%dB' % (i + 1, len(data)))
                    continue
                atyp = data[3]
                off = 4 + (4 if atyp == 1 else 1 + data[4] if atyp == 3 else 16) + 2
                dns = data[off:]
                got = struct.unpack('>H', dns[:2])[0] if len(dns) >= 2 else -1
                rcode = dns[3] & 0x0f if len(dns) >= 4 else -1
                print('TRY %d reply=%dB txid=0x%04x rcode=%d %.1fms' %
                      (i + 1, len(data), got, rcode, elapsed))
                if got == txid:
                    ok += 1
            print('UDP ATYP=3 DNS replies: %d/10' % ok)
            return 0 if ok > 0 else 1
        finally:
            udp.close()
    finally:
        ctrl.close()


if __name__ == '__main__':
    raise SystemExit(main())
