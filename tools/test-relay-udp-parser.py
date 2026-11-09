"""Regression tests for SOCKS5 UDP address parsing in both server copies."""
import importlib.util
import socket
import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCES = [ROOT / 'relay' / 'ferry-socks5-server.py',
           ROOT / 'wireguard' / 'ferry-socks5-server.py']


def packet(atyp, address, port=443, payload=b'probe'):
    return b'\x00\x00\x00' + bytes([atyp]) + address + struct.pack('>H', port) + payload


valid_cases = [
    ('ASCII domain', packet(3, bytes([len(b'game.example.com')]) + b'game.example.com'),
     (3, 'game.example.com', 443, b'probe')),
    ('IDNA A-label', packet(3, bytes([len(b'xn--bcher-kva.example')]) + b'xn--bcher-kva.example'),
     (3, 'bücher.example', 443, b'probe')),
    ('IPv4', packet(1, socket.inet_aton('203.0.113.7')),
     (1, '203.0.113.7', 443, b'probe')),
    ('IPv6', packet(4, socket.inet_pton(socket.AF_INET6, '2001:db8::7')),
     (4, '2001:db8::7', 443, b'probe')),
]

invalid_cases = [
    ('Malformed IDNA', packet(3, b'\x01\xff')),
    ('Truncated domain length', b'\x00\x00\x00\x03'),
    ('Truncated domain bytes', b'\x00\x00\x00\x03\x05ab'),
    ('Empty domain', packet(3, b'\x00')),
    ('Truncated IPv4', b'\x00\x00\x00\x01\x01\x02'),
    ('Truncated IPv6', b'\x00\x00\x00\x04' + b'\x00' * 4),
    ('Nonzero RSV', b'\x00\x01\x00\x01' + socket.inet_aton('203.0.113.7') + b'\x01\xbb'),
]

for source in SOURCES:
    spec = importlib.util.spec_from_file_location('ferry_relay_' + source.parent.name, source)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    print('SOURCE', source.relative_to(ROOT))
    for name, raw, expected in valid_cases:
        actual = module.parse_socks5_udp(raw)
        assert actual == expected, '%s: expected %r, got %r' % (name, expected, actual)
        print('PASS', name)
    for name, raw in invalid_cases:
        try:
            module.parse_socks5_udp(raw)
        except ValueError:
            print('PASS', name, '(rejected)')
        else:
            raise AssertionError('%s was accepted' % name)

print('PASS %d valid + %d invalid UDP parser cases per source' %
      (len(valid_cases), len(invalid_cases)))
