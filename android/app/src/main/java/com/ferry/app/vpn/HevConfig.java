package com.ferry.app.vpn;

/**
 * 生成 hev-socks5-tunnel 的 YAML 配置。
 *
 * 纯函数（不碰文件、不碰 Android API），方便单测比对字符串。
 */
public final class HevConfig {

    private HevConfig() {
    }

    /**
     * @param socksPort   本地/远端 SOCKS5 端口（hev 直接连它）
     * @param socksHost   SOCKS5 主机。手机端直连远端，所以是 10.13.24.1
     * @param logFile     hev 自己的日志文件路径（放 App 私有目录，别放 /sdcard）
     */
    public static String render(String socksHost, int socksPort, String logFile) {
        StringBuilder sb = new StringBuilder();
        // ── TUN ──
        sb.append("tunnel:\n");
        sb.append("  name: tun0\n");
        sb.append("  mtu: ").append(TunConfig.MTU).append('\n');
        sb.append("  ipv4: ").append(TunConfig.TUN_ADDRESS_V4).append('\n');
        sb.append("  # 不配 ipv6：校园网 v6 在禁网时也不通，配上只会多一类超时\n");
        sb.append("  icmp: 'off'\n");

        // ── SOCKS5 上游 ──
        sb.append("socks5:\n");
        sb.append("  address: ").append(socksHost).append('\n');
        sb.append("  port: ").append(socksPort).append('\n');
        // udp: 'udp' = 走远端 SOCKS5 的 UDP ASSOCIATE（RFC 1928 CMD=0x03）。
        // ferry-socks5-server.py v2 实现了它，实测本机→pcs UDP 往返 6.8ms。
        sb.append("  udp: 'udp'\n");
        // ⚠ **必须显式给 udp-address**，否则手机上 UDP 一个包都发不出去。
        //
        // 2026-10-30 实测：不写这一项时，中继日志里手机的 UDP 关联全是
        // 「上行 0 B / 下行 0 B / 0 包」—— 关联建起来了、包一个没进来；
        // 同一时刻 Windows 客户端是「上行 640 B / 下行 640 B / 20 包」，
        // 所以中继侧没问题，问题在客户端。
        //
        // 根因：hev 在 `udp: 'udp'` 下会用**服务端 BND 地址**去 connect()
        // 自己的 UDP socket（hev-socks5-client-udp.c 的
        // hev_socks5_client_udp_set_upstream_addr）。而 RFC 1928 允许服务端
        // 返回 `0.0.0.0:0` 表示「你自己知道该连哪」—— 我们的
        // ferry-socks5-server.py 正是返回 `0.0.0.0`（它不知道客户端眼里自己
        // 是什么地址，这本身完全合规，不算 bug）。
        // 但 Linux/Android 上 connect() 到 0.0.0.0 等价于「连本机」，
        // 于是 UDP 包全被送进本机回环，一个都到不了远端。
        //
        // 修法：`udp-address` 就是官方为这种情况准备的覆盖项，README 原话：
        //   "Override the UDP address provided by the Socks5 server"
        // 值必须与上面 socks5.address 一致（同一个 SOCKS5 服务端）。
        sb.append("  udp-address: ").append(socksHost).append('\n');

        // ── DNS ──
        // 不开 mapdns 的话，DNS 查询会作为原始 IP 包进隧道，
        // 而 SOXKS5 只能按 IP 连，域名信息就丢了（分流也就做不了）。
        sb.append("mapdns:\n");
        sb.append("  address: ").append(TunConfig.DNS_ADDRESS).append('\n');
        sb.append("  port: 53\n");
        sb.append("  network: ").append(TunConfig.MAPDNS_NETWORK).append('\n');
        sb.append("  netmask: ").append(TunConfig.MAPDNS_NETMASK).append('\n');
        sb.append("  cache-size: 10000\n");

        // ── 杂项 ──
        sb.append("misc:\n");
        sb.append("  log-level: info\n");
        sb.append("  log-file: ").append(logFile).append('\n');
        // 连接超时给短一点：校园网禁网时上游常常「连得上但不通」，
        // 10 秒的默认值会让用户以为 App 卡死了。
        sb.append("  connect-timeout: 8000\n");
        return sb.toString();
    }
}
