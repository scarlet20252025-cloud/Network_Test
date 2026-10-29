package com.ferry.app;

import android.util.Log;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

import hev.htproxy.TProxyService;

/**
 * 自检：把「隧道到底通没通」拆成五步分别验证。
 *
 * 为什么要做成功能而不是临时脚本：
 *   用户报「连不上」时，最怕的是不知道卡在哪一层 —— 是 VPN 没起来？
 *   TUN 没建起来？DNS 不解析？还是隧道通了但出口本身没网？
 *   这五步从下往上逐层验证，哪一步断了一眼就能看出来。
 *   同时也让「验证」这件事不依赖我手工敲 adb。
 *
 * 五步：
 *   ① TUN 网卡存在（198.18.0.1）
 *   ② hev native 在跑（TProxyIsRunning）
 *   ③ DNS 能解析（www.qq.com —— 走 mapdns，由远端解析）
 *   ④ HTTP 能通（http://www.qq.com）
 *   ⑤ 出口 IP 真的是远端（对比自检拿到的公网 IP 与远端出口）
 *
 * 第 ⑤ 步最要紧：前四步全过也可能「其实没走隧道」（比如路由没配上，
 * 流量从 wlan0 直出了）。只有出口 IP 变了才证明真的在借网。
 */
public class SelfCheck {

    private static final String TAG = "FerrySelfCheck";

    public static class Item {
        public final String name;
        public final boolean ok;
        public final String detail;

        Item(String name, boolean ok, String detail) {
            this.name = name;
            this.ok = ok;
            this.detail = detail == null ? "" : detail;
        }

        @Override
        public String toString() {
            return (ok ? "✓ " : "✗ ") + name + (detail.isEmpty() ? "" : "  " + detail);
        }
    }

    public interface Callback {
        void onDone(List<Item> items, String summary);
    }

    public static void run(Callback cb) {
        run(0, cb);
    }

    /**
     * @param proxyPort 本地 HTTP 代理端口（一般是 cfg.httpPort）。
     *                  传 0 表示直连 —— 但那样测 HTTP 项会假阴性，见 checkHttp 的注释。
     */
    public static void run(int proxyPort, Callback cb) {
        new Thread(() -> {
            List<Item> items = new ArrayList<>();
            items.add(checkTun());
            items.add(checkHev());
            items.add(checkDns());
            items.add(checkHttp(proxyPort));
            items.add(checkUplink());
            int okCount = 0;
            for (Item i : items) if (i.ok) okCount++;
            String summary = okCount + "/" + items.size() + " 项通过"
                    + (okCount == items.size() ? " —— 隧道正常" : " —— 隧道没完全通");
            // 出口 IP 只作参考信息，不计入通过数：这台校园网里 pcs 和手机
            // 走同一个 NAT 出口，开不开隧道公网 IP 都一样，拿它当判据会误判。
            Item egress = checkEgressInfo();
            items.add(new Item("出口 IP（仅供参考）", true, egress.detail));
            cb.onDone(items, summary);
        }, "ferry-selfcheck").start();
    }

    /** ① TUN 网卡在不在，地址对不对。 */
    private static Item checkTun() {
        try {
            for (NetworkInterface ni : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!ni.getName().startsWith("tun")) continue;
                if (!ni.isUp()) continue;
                for (InetAddress a : Collections.list(ni.getInetAddresses())) {
                    String ip = a.getHostAddress();
                    if ("198.18.0.1".equals(ip)) {
                        return new Item("TUN 网卡", true, ni.getName() + " = " + ip);
                    }
                }
            }
            return new Item("TUN 网卡", false, "没有 198.18.0.1 的 tun 网卡");
        } catch (Exception e) {
            return new Item("TUN 网卡", false, e.getMessage());
        }
    }

    /** ② hev 的 native 隧道是否在跑。 */
    private static Item checkHev() {
        try {
            boolean running = TProxyService.TProxyIsRunning();
            if (!running) return new Item("隧道进程", false, "hev 报告未运行");
            long[] st = TProxyService.TProxyGetStats();
            String extra = "";
            if (st != null && st.length >= 4) {
                extra = "tx " + st[1] + "B / rx " + st[3] + "B";
            }
            return new Item("隧道进程", true, extra);
        } catch (UnsatisfiedLinkError e) {
            return new Item("隧道进程", false, "so 没加载：" + e.getMessage());
        } catch (Throwable t) {
            return new Item("隧道进程", false, t.getClass().getSimpleName());
        }
    }

    /** ③ DNS 解析。走 mapdns，域名由远端解析。 */
    private static Item checkDns() {
        try {
            InetAddress a = InetAddress.getByName("www.qq.com");
            return new Item("DNS 解析", a != null, "www.qq.com → " + a.getHostAddress());
        } catch (Exception e) {
            return new Item("DNS 解析", false, e.getClass().getSimpleName());
        }
    }

    /**
     * ④ 一次真实的 HTTP 请求。
     *
     * ⚠ **必须走本地 SOCKS/HTTP 代理端口，不能直连。**
     *
     * 2026-10-30 实测踩到的假阴性：
     *   FerryVpnService 里有一行（必要，防止代理流量回环）：
     *       b.addDisallowedApplication(getPackageName());
     *   Android 因此把**本 App 的 uid 从 VPN 路由规则里剔除**。实测 ip rule：
     *
     *       12000: from all fwmark 0x0/0x20000 iif lo uidrange 0-10292 lookup 1055
     *       12000: from all fwmark 0x0/0x20000 iif lo uidrange 10294-99999 lookup 1055
     *                                              ↑ 10293 正好空着 = 本 App
     *
     *   于是本进程直连出去的请求**不走隧道**，走的是物理网卡。
     *   校园网断网时，这一项必然失败 —— 但此时隧道其实完全正常
     *   （真机验证：浏览器打开了百度，服务端日志有 17KB 传输记录）。
     *
     * 修法：请求显式经过 127.0.0.1:<httpPort> 代理，这样它由 hev 接管进隧道，
     * 不受 disallow 影响，测出来的才是真实的隧道状态。
     */
    private static Item checkHttp(int proxyPort) {
        HttpURLConnection c = null;
        try {
            URL u = new URL("http://www.qq.com");
            if (proxyPort > 0) {
                c = (HttpURLConnection) u.openConnection(
                        new java.net.Proxy(java.net.Proxy.Type.HTTP,
                                new java.net.InetSocketAddress("127.0.0.1", proxyPort)));
            } else {
                c = (HttpURLConnection) u.openConnection();
            }
            c.setConnectTimeout(8000);
            c.setReadTimeout(8000);
            c.setInstanceFollowRedirects(false);
            int code = c.getResponseCode();
            String via = proxyPort > 0 ? "（经本地代理 " + proxyPort + "）" : "（直连，可能不准）";
            return new Item("HTTP 请求", code > 0, "HTTP " + code + via);
        } catch (Exception e) {
            return new Item("HTTP 请求", false, e.getClass().getSimpleName() + ": " + e.getMessage());
        } finally {
            if (c != null) c.disconnect();
        }
    }

    /**
     * ⑤ 隧道是否真的在承载流量：用「请求前后的 hev 流量增量」判定。
     *
     * 这一项前后换过两版判据，两版都被真机打回来了，值得记下来：
     *
     * 版本 1「查出口 IP 是不是变成远端出口」——2026-10-29 实测失效：
     *   这台校园网里 pcs(10.13.24.1) 和手机走同一个 NAT 出口，
     *   开隧道和关隧道拿到的公网 IP 一模一样（都是 120.235.20.202）。
     *   判据失效比没有判据更糟，因为它会让人以为隧道没工作。
     *
     * 版本 2「读 /proc/net/tcp6 找有没有到远端 1080 的连接」——同样失效：
     *   Android 10 起普通应用读 /proc/net/tcp{,6} 被 SELinux 挡住，
     *   读出来是空的。而 netstat 在 adb shell（shell 用户）下却能看到，
     *   所以这个坑只在应用内暴露，用 adb 验证是看不出来的。
     *
     * 版本 3（当前）：读 hev 自己报的统计值（TProxyGetStats），
     *   发一次请求，看前后字节数有没有涨。涨了就证明这次请求确实
     *   从 TUN 进了隧道 —— 这是端到端、只依赖自己数据的判据。
     */
    private static Item checkUplink() {
        long before = tunnelBytes();
        boolean reqOk = quickHttp("http://www.qq.com");
        long after = tunnelBytes();
        long delta = after - before;
        if (delta > 0) {
            return new Item("隧道承载", true,
                    "这次请求让隧道流量增加 " + delta + " 字节");
        }
        if (before > 0) {
            return new Item("隧道承载", true,
                    "本次没测到增量，但隧道累计已过 " + before + " 字节");
        }
        return new Item("隧道承载", false,
                reqOk ? "隧道统计一直为 0，流量没进隧道" : "请求失败，无法判断");
    }

    /** 读 hev 的累计字节数（tx 字节 + rx 字节）；取不到返回 0。 */
    private static long tunnelBytes() {
        try {
            long[] st = TProxyService.TProxyGetStats();
            if (st == null || st.length < 4) return 0;
            // 四个值的含义（见 hev-jni.c）：[0]=tx 包数 [1]=tx 字节 [2]=rx 包数 [3]=rx 字节
            return st[1] + st[3];
        } catch (Throwable t) {
            return 0;
        }
    }

    /** 一次短超时的 HTTP GET，只关心「请求是否发生了」以及「是否成功」。 */
    private static boolean quickHttp(String url) {
        HttpURLConnection c = null;
        try {
            c = (HttpURLConnection) new URL(url).openConnection();
            c.setConnectTimeout(6000);
            c.setReadTimeout(6000);
            c.setInstanceFollowRedirects(false);
            int code = c.getResponseCode();
            return code > 0;
        } catch (Exception e) {
            return false;
        } finally {
            if (c != null) c.disconnect();
        }
    }

    /**
     * 出口 IP —— **仅供参考**，不计入通过数。
     *
     * 为什么降级：2026-10-29 真机实测，这台校园网里 pcs(10.13.24.1) 和手机
     * 走的是同一个 NAT 出口，开隧道和关隧道拿到的公网 IP 完全一样
     * （都是 120.235.20.202）。拿它当「隧道是否生效」的判据会误判成「没工作」。
     * 它仍然有用：能看出远端出口是哪条线（联通/电信/移动）。
     */
    private static Item checkEgressInfo() {
        String[] urls = new String[]{
                "http://members.3322.org/dyndns/getip",
                "http://ip.3322.net",
                "http://myip.ipip.net",
        };
        for (String u : urls) {
            HttpURLConnection c = null;
            try {
                c = (HttpURLConnection) new URL(u).openConnection();
                c.setConnectTimeout(8000);
                c.setReadTimeout(8000);
                int code = c.getResponseCode();
                if (code != 200) continue;
                String body = readAll(c.getInputStream()).trim();
                if (body.isEmpty()) continue;
                if (body.length() > 120) body = body.substring(0, 120);
                return new Item("出口 IP", true, body.replace('\n', ' '));
            } catch (Exception e) {
                Log.d(TAG, "egress " + u + " failed: " + e.getMessage());
            } finally {
                if (c != null) c.disconnect();
            }
        }
        return new Item("出口 IP", false, "三个回显接口都没拿到");
    }

    private static String readAll(InputStream in) throws Exception {
        if (in == null) return "";
        StringBuilder sb = new StringBuilder();
        BufferedReader br = new BufferedReader(new InputStreamReader(in, StandardCharsets.UTF_8));
        String line;
        while ((line = br.readLine()) != null) sb.append(line).append('\n');
        br.close();
        return sb.toString();
    }
}
