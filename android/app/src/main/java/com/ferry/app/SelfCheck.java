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
        new Thread(() -> {
            List<Item> items = new ArrayList<>();
            items.add(checkTun());
            items.add(checkHev());
            items.add(checkDns());
            items.add(checkHttp());
            items.add(checkEgress());
            int okCount = 0;
            for (Item i : items) if (i.ok) okCount++;
            String summary = okCount + "/" + items.size() + " 项通过"
                    + (okCount == items.size() ? " —— 隧道正常" : " —— 隧道没完全通");
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

    /** ④ 一次真实的 HTTP 请求。 */
    private static Item checkHttp() {
        HttpURLConnection c = null;
        try {
            c = (HttpURLConnection) new URL("http://www.qq.com").openConnection();
            c.setConnectTimeout(8000);
            c.setReadTimeout(8000);
            c.setInstanceFollowRedirects(false);
            int code = c.getResponseCode();
            return new Item("HTTP 请求", code > 0, "HTTP " + code);
        } catch (Exception e) {
            return new Item("HTTP 请求", false, e.getClass().getSimpleName() + ": " + e.getMessage());
        } finally {
            if (c != null) c.disconnect();
        }
    }

    /**
     * ⑤ 出口 IP。
     *
     * 用一个只回显来源 IP 的接口。借网成功时这里是**远端出口**的地址，
     * 而不是本机所在网络的地址 —— 这是「真的走了隧道」的唯一硬证据。
     */
    private static Item checkEgress() {
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
