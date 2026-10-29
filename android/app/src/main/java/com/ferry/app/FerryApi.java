package com.ferry.app;

import android.util.Log;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/**
 * 门户 API。协议和 Windows 客户端完全一致（见 borrow-net/ferry-client/src/Api.cs），
 * 服务端不用改一行。
 *
 * 为什么自己拼 JSON 而不用 Gson/OkHttp：
 *   手机端请求就那么几个，依赖越少越好（APK 体积、构建失败风险）。
 *   org.json 是 Android 自带的，够用。
 */
public class FerryApi {

    private static final String TAG = "FerryApi";

    public static class Result {
        public boolean ok;
        public String error = "";
        public String token = "";
        public String remoteHost = "";
        public String portalUrl = "";
        public int remotePort = 0;
        public long expiresAt = 0;

        @Override
        public String toString() {
            return ok ? "ok" : ("失败：" + error);
        }
    }

    /**
     * 用激活码换 token。
     *
     * 注意：服务端是按**来源 IP** 发授权的（api/activate 里记 accountIp）。
     * 手机在校园网里和电脑共用同一个 NAT 出口 IP，所以两边的授权会互相顶掉。
     * 这是已知的产品问题，见 android/DESIGN.md 第八节。
     */
    public static Result activate(String portalUrl, String code, int timeoutMs) {
        Result r = new Result();
        String url = trimSlash(portalUrl) + "/api/activate";
        String body = "{\"code\":\"" + esc(code) + "\"}";
        try {
            HttpURLConnection c = open(url, "POST", timeoutMs);
            c.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
            c.setRequestProperty("Content-Length", String.valueOf(bytes.length));
            c.setDoOutput(true);
            OutputStream os = c.getOutputStream();
            os.write(bytes);
            os.flush();
            os.close();

            int code2 = c.getResponseCode();
            String resp = readAll(code2 >= 400 ? c.getErrorStream() : c.getInputStream());
            fill(r, resp);
            if (code2 >= 500 && r.error.isEmpty()) {
                r.error = "服务端出错了（HTTP " + code2 + "）";
            }
            c.disconnect();
        } catch (Exception e) {
            Log.w(TAG, "activate failed", e);
            r.error = "请求失败：" + e.getMessage();
        }
        return r;
    }

    /** 查授权状态（服务端按 IP 查）。用来自检「这张网还剩几天」。 */
    public static Result checkLicense(String portalUrl, String ip, int timeoutMs) {
        Result r = new Result();
        String url = trimSlash(portalUrl) + "/api/license?ip=" + enc(ip);
        try {
            HttpURLConnection c = open(url, "GET", timeoutMs);
            String resp = readAll(c.getInputStream());
            fill(r, resp);
            c.disconnect();
        } catch (Exception e) {
            r.error = "查询失败：" + e.getMessage();
        }
        return r;
    }

    /** 心跳。服务端用它统计在线，也用它下发最新授权状态。 */
    public static Result heartbeat(String portalUrl, String token, int timeoutMs) {
        Result r = new Result();
        try {
            HttpURLConnection c = open(trimSlash(portalUrl) + "/api/heartbeat", "POST", timeoutMs);
            c.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            String body = "{\"token\":\"" + esc(token) + "\"}";
            byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
            c.setRequestProperty("Content-Length", String.valueOf(bytes.length));
            c.setDoOutput(true);
            OutputStream os = c.getOutputStream();
            os.write(bytes);
            os.flush();
            os.close();
            fill(r, readAll(c.getInputStream()));
            c.disconnect();
        } catch (Exception e) {
            r.error = "心跳失败：" + e.getMessage();
        }
        return r;
    }

    // ── 内部工具 ──

    private static HttpURLConnection open(String url, String method, int timeoutMs) throws Exception {
        HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
        c.setRequestMethod(method);
        c.setConnectTimeout(timeoutMs);
        c.setReadTimeout(timeoutMs);
        c.setInstanceFollowRedirects(true);
        return c;
    }

    private static void fill(Result r, String body) {
        if (body == null) return;
        try {
            JSONObject o = new JSONObject(body);
            r.ok = o.optBoolean("ok", false);
            r.error = o.optString("error", "");
            r.token = o.optString("token", "");
            r.remoteHost = o.optString("remoteHost", "");
            r.portalUrl = o.optString("portalUrl", "");
            r.remotePort = o.optInt("relayPort", 0);
            r.expiresAt = o.optLong("expiresAt", 0);
        } catch (Exception e) {
            // 服务端 500 时返回的是 HTML/traceback，直接显示给用户没法看
            if (r.error.isEmpty()) {
                r.error = body.trim().startsWith("<")
                        ? "服务端返回了网页而不是数据，请确认门户地址"
                        : ("无法解析服务端响应：" + body.substring(0, Math.min(120, body.length())));
            }
        }
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

    private static String trimSlash(String s) {
        if (s == null) return "";
        return s.endsWith("/") ? s.substring(0, s.length() - 1) : s;
    }

    private static String esc(String s) {
        if (s == null) return "";
        return s.replace("\\", "\\\\").replace("\"", "\\\"");
    }

    private static String enc(String s) {
        try {
            return java.net.URLEncoder.encode(s == null ? "" : s, "UTF-8");
        } catch (Exception e) {
            return "";
        }
    }
}
