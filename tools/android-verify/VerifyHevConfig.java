import com.ferry.app.vpn.HevConfig;
import com.ferry.app.vpn.TunConfig;

/**
 * 校验发给 hev-socks5-tunnel 的 YAML 与路由表常量。
 *
 * 为什么要在电脑上跑这个（而不是只靠手机上试）：
 *   TunConfig / HevConfig 都是**纯 Java**（不 import 任何 Android 类），
 *   所以能用 javac 直接编出来跑。而 YAML 里任何一处写错（键名拼错、
 *   段名写错、MTU 与 Builder 不一致）的症状都是「手机上隧道起不来、
 *   或者起来了但 DNS 全失败」—— 在真机上排查这两个都要来回装包，
 *   在这里几十毫秒就能查出。
 *
 * ⚠ 键名不靠猜：允许的键是从 hev 自己的解析器 src/hev-config.c 里
 *   逐个 grep 出来的（见 tools/verify-android-hevconfig.py 的 ALLOWED_KEYS）。
 *   还确认了一个容易搞错的地方：connect-timeout 属于 misc 段
 *   （hev_config_parse_misc），不是 tunnel 段。
 *
 * 用法（配合 tools/verify-android-hevconfig.py 跑全套）：
 *   javac -d /tmp/out TunConfig.java HevConfig.java VerifyHevConfig.java
 *   java -cp /tmp/out VerifyHevConfig
 */
public class VerifyHevConfig {

    static int fails = 0;

    static void ok(boolean cond, String name) {
        System.out.println((cond ? "PASS  " : "FAIL  ") + name);
        if (!cond) fails++;
    }

    static void ok(boolean cond, String name, String detail) {
        System.out.println((cond ? "PASS  " : "FAIL  ") + name
                + (cond || detail == null ? "" : ("  -- " + detail)));
        if (!cond) fails++;
    }

    public static void main(String[] args) {
        String yaml = HevConfig.render("10.13.24.1", 1080,
                "/data/data/com.ferry.app/files/hev/hev.log");

        System.out.println("=== 生成的 YAML ===");
        System.out.println(yaml);
        System.out.println("=== 检查 ===");

        // ── 段 ──
        for (String sec : new String[]{"tunnel:", "socks5:", "mapdns:", "misc:"}) {
            ok(yaml.contains(sec), "包含段 " + sec);
        }

        // ── tunnel ──
        ok(yaml.contains("  mtu: " + TunConfig.MTU),
                "mtu 与 TunConfig.MTU 一致（" + TunConfig.MTU + "）");
        ok(yaml.contains("  ipv4: " + TunConfig.TUN_ADDRESS_V4),
                "ipv4 与 TunConfig.TUN_ADDRESS_V4 一致（" + TunConfig.TUN_ADDRESS_V4 + "）");
        ok(HevConfig.render("h", 1, "l").contains("  name: tun0"), "有 name: tun0");

        // ── socks5：上游必须是远端，且开了 UDP ──
        ok(yaml.contains("  address: 10.13.24.1"), "socks5.address 是远端 IP");
        ok(yaml.contains("  port: 1080"), "socks5.port 是 1080");
        ok(yaml.contains("  udp: 'udp'"), "socks5.udp 是 'udp'（走 UDP ASSOCIATE）");

        // ── mapdns ──
        ok(yaml.contains("  address: " + TunConfig.DNS_ADDRESS),
                "mapdns.address 与 TunConfig.DNS_ADDRESS 一致");
        ok(yaml.contains("  network: " + TunConfig.MAPDNS_NETWORK), "mapdns.network 一致");
        ok(yaml.contains("  netmask: " + TunConfig.MAPDNS_NETMASK), "mapdns.netmask 一致");

        // ── 数值一致性：fake-ip 池必须容得下 cache-size ──
        // 池是 /18 = 16384 个地址，cache-size 10000 —— 池必须更大，
        // 否则地址会被回收复用，长连接会串到别的域名上。
        int cacheSize = 0;
        for (String line : yaml.split("\n")) {
            String t = line.trim();
            if (t.startsWith("cache-size:")) {
                cacheSize = Integer.parseInt(t.substring("cache-size:".length()).trim());
            }
        }
        ok(cacheSize > 0, "解析到 cache-size = " + cacheSize);
        int poolSize = poolAddressCount(TunConfig.MAPDNS_NETWORK, TunConfig.MAPDNS_NETMASK);
        ok(cacheSize < poolSize,
                "cache-size(" + cacheSize + ") < fake-ip 池容量(" + poolSize + ")，地址不会复用",
                "池太小会让不同域名共用地址");

        // ── 路由表 ──
        String[][] routes = TunConfig.routesV4();
        ok(routes.length == 46, "路由条目 46 条", "实际 " + routes.length);
        boolean hasDefault = false;
        for (String[] r : routes) {
            if ("0.0.0.0".equals(r[0]) && "0".equals(r[1])) hasDefault = true;
        }
        ok(!hasDefault, "路由表里没有 0.0.0.0/0（否则局域网会被塞进隧道）");

        // 远端 SOCKS5 必须直连，不能被路由进隧道
        ok(!covers(routes, "10.13.24.1"), "远端 10.13.24.1 不进隧道（能直连）");
        ok(covers(routes, "8.8.8.8"), "8.8.8.8 进隧道");
        ok(!covers(routes, "192.168.1.1"), "192.168.1.1 不进隧道（局域网直连）");

        // ── 一致性：mapdns 的地址必须真的会进隧道，否则 DNS 走不通 ──
        //     198.18.0.2 落在 196.0.0.0/6 里 → 会进隧道。这条断言防的是
        //     「以后有人把 fake-ip 池挪到某私网段，DNS 就再也送不进隧道的」。
        ok(covers(routes, TunConfig.DNS_ADDRESS),
                "mapdns 的 " + TunConfig.DNS_ADDRESS + " 会进隧道（DNS 才通得过）");
        ok(covers(routes, TunConfig.MAPDNS_NETWORK),
                "fake-ip 池 " + TunConfig.MAPDNS_NETWORK + " 会进隧道");

        System.out.println();
        System.out.println(fails == 0 ? "ALL PASS" : (fails + " FAILED"));
        System.exit(fails == 0 ? 0 : 1);
    }

    /** address/prefix 是否落在路由表内。 */
    static boolean covers(String[][] routes, String ip) {
        long a = ip2long(ip);
        for (String[] r : routes) {
            int prefix = Integer.parseInt(r[1]);
            long base = ip2long(r[0]);
            long mask = prefix == 0 ? 0L : (0xFFFFFFFFL << (32 - prefix)) & 0xFFFFFFFFL;
            if ((a & mask) == (base & mask)) return true;
        }
        return false;
    }

    static long ip2long(String ip) {
        String[] p = ip.split("\\.");
        return ((Long.parseLong(p[0]) & 0xFF) << 24)
                | ((Long.parseLong(p[1]) & 0xFF) << 16)
                | ((Long.parseLong(p[2]) & 0xFF) << 8)
                | (Long.parseLong(p[3]) & 0xFF);
    }

    /** 按 mask 算池里有几个地址。 */
    static int poolAddressCount(String network, String netmask) {
        long m = ip2long(netmask);
        int prefix = Long.bitCount(m);
        return 1 << (32 - prefix);
    }
}
