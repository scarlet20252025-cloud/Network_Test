package com.ferry.app.vpn;

/**
 * TUN 与 hev-socks5-tunnel 的常量。
 *
 * 这些值有三处必须完全一致，改一处就要三处一起改：
 *   1. 这里的常量
 *   2. VpnService.Builder 里的 addAddress / setMtu
 *   3. 生成给 hev 的 YAML（HevConfig.render）
 *
 * 不一致的表现：hev 起不来（TProxyStartService 返回 false），
 * 或者起来了但 DNS 全失败、只有纯 IP 能通。
 */
public final class TunConfig {

    private TunConfig() {
    }

    /**
     * MTU 为什么是 1400 而不是 1500（2026-10-29 从 pingtunnel-client 学到的实测结论）：
     *
     * 两条独立的上限，取小的那个：
     *   ① hev 的回程 UDP 缓冲**写死 1500 字节**（含 SOCKS5 头），更大的报文会被截断；
     *   ② QUIC 禁止 IP 分片，服务端把 UDP 载荷原样发出，公网路径大约只有 1500。
     *
     * 1400 时 IPv4 载荷约 1372，加上域名型 SOCKS5 头仍低于 1500，
     * 且不低于 QUIC 要求的 1200。
     *
     * ⚠ 设大了的症状非常难查：TCP 浏览完全正常，只有 UDP（游戏/语音/QUIC）
     *   偶尔卡一下、丢包 —— 看着像「服务器慢」，其实是本地截断。
     */
    public static final int MTU = 1400;

    /** TUN 网卡自己的地址（不是网关）。RFC 2544 基准测试段，公网无真实地址。 */
    public static final String TUN_ADDRESS_V4 = "198.18.0.1";
    public static final int TUN_PREFIX_V4 = 30;

    /**
     * mapdns 的虚拟 DNS 地址。
     * DNS 查询由 hev 在本地应答（返回 fake-ip），真实域名由 SOCKS5 服务端解析，
     * 所以系统 DNS 永远不会直连运营商 —— 没有 DNS 泄漏。
     */
    public static final String DNS_ADDRESS = "198.18.0.2";

    /**
     * fake-ip 池。
     *
     * 为什么是 198.18.64.0/18 而不是常见的 100.64.0.0/10：
     *   100.64/10 是运营商 CGNAT 段（RFC 6598）。旁路内网时真实的 100.64
     *   专网流量需要在用户态直连，fake-ip 池与它重叠会互相串流。
     *   198.18/15 是 RFC 2544 基准测试段，公网绝无真实主机。
     *
     * /18 = 16384 个地址，大于 hev 的 mapdns cache-size（10000）。
     */
    public static final String MAPDNS_NETWORK = "198.18.64.0";
    public static final String MAPDNS_NETMASK = "255.255.192.0";

    /**
     * 不进隧道的网段（局域网直连）。
     *
     * 必须排除，否则「连监控摄像头/打印机/路由器后台」这类局域网访问
     * 全部跑到远端去绕一圈，要么超时要么不通。
     * 远端主机 10.13.24.1 本身也必须直连 —— 否则隧道自己绕进隧道。
     */
    public static final String[] BYPASS_CIDRS = new String[]{
            "10.0.0.0/8",        // 校园网内网 + 远端 pcs 都在这里
            "172.16.0.0/12",
            "192.168.0.0/16",
            "127.0.0.0/8",
    };
}
