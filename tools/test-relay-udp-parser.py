"""SOCKS5 UDP 头解析 + 域名编解码回归测试（relay / wireguard 两份副本都跑）。

用法：python tools/test-relay-udp-parser.py

为什么要两份都测：wireguard/ferry-socks5-server.py 是同步副本，历史上
只改一处、另一处回归过。域名编解码尤其要一起测 —— TCP CONNECT 和 UDP 头
必须用同一套严格规则，否则会出现「TCP 拒绝、UDP 放行」的分流绕过。
"""
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
     (3, 'b\u00fccher.example', 443, b'probe')),
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

# 域名编解码：核心回归是「非法字节绝不能被静默删掉，把包发到另一台主机」。
domain_valid = [
    ('ASCII domain', b'game.example.com', 'game.example.com'),
    ('IDNA A-label', b'xn--bcher-kva.example', 'b\u00fccher.example'),
]
domain_invalid = [
    ('Malformed IDNA byte', b'evil\xff.example'),
    ('Empty domain', b''),
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

    for name, raw, expected in domain_valid:
        actual = module.decode_socks5_domain(raw)
        assert actual == expected, 'decode %s: expected %r, got %r' % (name, expected, actual)
        print('PASS decode', name)
    for name, raw in domain_invalid:
        try:
            module.decode_socks5_domain(raw)
        except ValueError:
            print('PASS decode', name, '(rejected)')
        else:
            raise AssertionError('decode %s was accepted' % name)

    hb = module.encode_socks5_domain('b\u00fccher.example')
    assert hb == b'xn--bcher-kva.example', 'encode IDNA: got %r' % hb
    print('PASS encode IDNA roundtrip')
    try:
        module.encode_socks5_domain('x' * 300)
    except ValueError:
        print('PASS encode overlong (rejected)')
    else:
        raise AssertionError('overlong domain was accepted')

    # 静态兜底：任何一处重新引入 ascii+ignore 静默降级，直接让回归失败。
    src = source.read_text(encoding='utf-8', errors='replace')
    for banned in ("decode('ascii', 'ignore')", "encode('ascii', 'ignore')"):
        assert banned not in src, '%s 又出现了静默降级 %s' % (source, banned)
    assert 'host = decode_socks5_domain(raw)' in src, '%s 的 TCP CONNECT 未走严格解码' % source
    print('PASS no ascii+ignore fallback')

print('PASS %d valid + %d invalid UDP parser, %d valid + %d invalid domain cases per source' %
      (len(valid_cases), len(invalid_cases), len(domain_valid), len(domain_invalid)))
