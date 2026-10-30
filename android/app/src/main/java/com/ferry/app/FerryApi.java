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
 * 门户 API。协议与 Windows 客户端一致（见 borrow-net/ferry-client/src/Api.cs）。
 *
 * 为什么自己拼 JSON 而不用 Gson/OkHttp：
 *   手机端请求就那么几个，依赖越少越好（APK 体积、构建失败风险）。
 *   org.json 是 Android 自带的，够用。
 *
 * 2026-10-29 改动：全部接口都带上 device_id。
 *   授权从「按 IP」改成「按设备」—— 同一局域网里手机和电脑共用一个 NAT 出口 IP，
 *   用 IP 当身份必然互相顶掉。设备身份用 ANDROID_ID（重装不变），
 *   这样重装 App 之后还能自动恢复授权。
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
        public int daysLeft = -1;       // -1 = 永久（服务端约定 expiresAt=0 即永久）
        public boolean expired;
        public boolean suspended;
        public boolean deviceLimit;
        public boolean codeConsumed;
        public int deviceCount;
        public int deviceMax;
        public String deviceMessage = "";
        /** 服务端明确说「这台设备没有登记记录」时为 true，调用方据此提示重新激活。 */
        public boolean deviceUnknown;
        /** 网络层失败（连不上门户），不代表授权无效。 */
        public boolean networkError;

        // ── 更新检查（/android/version）──
        //
        // 判断「要不要更新」**必须用 latestCode 比较**，不能比 latest：
        //   latest 是 versionName（"0.1.0"），字符串比较下 '0.10.0' < '0.9.0'，
        //   用了会永远提示有新版本、或者永远不提示。
        //   latestCode 是 versionCode，单调递增的整数，唯一正确判据。
        /** 服务端最新版本的 versionName（给人看）。 */
        public String latest = "";
        /** 服务端最新版本的 versionCode（**用来比较**）。0 = 服务端没包。 */
        public int latestCode = 0;
        /** APK 下载路径（相对，拼 portalUrl 用）。 */
        public String downloadUrl = "";
        /** APK 字节数。 */
        public long apkSize = 0;
        /** APK 的 sha256（服务端没算则为空）。 */
        public String apkSha256 = "";

        @Override
        public String toString() {
            return ok ? "ok" : ("失败：" + error);
        }
    }

    /**
     * 用激活码换 token，同时把本设备登记到这个账号下。
     *
     * @param deviceId ANDROID_ID（重装不变），服务端据此区分设备
     */
    public static Result activate(String portalUrl, String code, String deviceId,
                                 String model, String appVersion, int timeoutMs) {
        Result r = new Result();
        String url = trimSlash(portalUrl) + "/api/activate";
        JSONObject body = new JSONObject();
        try {
            body.put("code", code);
            body.put("device_id", deviceId);
            body.put("platform", "android");
            body.put("model", model);
            body.put("app_version", appVersion);
        } catch (Exception ignored) {
        }
        postJson(r, url, body.toString(), timeoutMs, "activate");
        return r;
    }

    /**
     * 查授权状态（按 token + device_id，不再按 IP）。
     *
     * 服务端会顺手登记/刷新这台设备的心跳，所以启动时调一次就够了，
     * 不需要额外的「注册设备」接口。
     */
    public static Result checkLicense(String portalUrl, String token, String deviceId,
                                      String model, String appVersion, int timeoutMs) {
        Result r = new Result();
        StringBuilder url = new StringBuilder(trimSlash(portalUrl))
                .append("/api/license?token=").append(enc(token))
                .append("&device_id=").append(enc(deviceId))
                .append("&platform=android")
                .append("&model=").append(enc(model))
                .append("&app_version=").append(enc(appVersion));
        getJson(r, url.toString(), timeoutMs, "license");
        return r;
    }

    /**
     * 重装/新装后凭设备标识自动恢复授权。
     *
     * 场景：用户重装 App，本地 token 没了。这台设备其实早就登记过，
     * 不应该让他再去找管理员要一次性密钥。
     *
     * 服务端有三条校验（见 portal-server.py 的 handle_api_device_restore）：
     * 设备已登记、账号仍有效、来源 IP 与上次在同一个 /24。
     */
    public static Result restoreDevice(String portalUrl, String deviceId,
                                       String model, String appVersion, int timeoutMs) {
        Result r = new Result();
        String url = trimSlash(portalUrl) + "/api/device/restore?device_id=" + enc(deviceId)
                + "&platform=android&model=" + enc(model) + "&app_version=" + enc(appVersion);
        getJson(r, url, timeoutMs, "restore");
        if (!r.ok && "这台设备没有登记记录".equals(r.error)) {
            r.deviceUnknown = true;
        }
        return r;
    }

    /** 心跳。服务端用它统计在线，也用它下发最新授权状态。 */
    public static Result heartbeat(String portalUrl, String token, String deviceId, int timeoutMs) {
        Result r = new Result();
        JSONObject body = new JSONObject();
        try {
            body.put("token", token);
            body.put("device_id", deviceId);
        } catch (Exception ignored) {
        }
        postJson(r, trimSlash(portalUrl) + "/api/heartbeat", body.toString(), timeoutMs, "heartbeat");
        return r;
    }

    // ── 内部工具 ──

    /**
     * 查 Android 端有没有新版本。
     *
     * 失败时 networkError=true，调用方应当**静默忽略** —— 检查更新
     * 失败绝不能打扰用户（校园网抖动很常见，弹个"检查更新失败"只会烦人）。
     */
    public static Result checkUpdate(String portalUrl, int timeoutMs) {
        Result r = new Result();
        getJson(r, trimSlash(portalUrl) + "/android/version", timeoutMs, "android-version");
        return r;
    }

    private static void postJson(Result r, String url, String body, int timeoutMs, String what) {
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

            int httpCode = c.getResponseCode();
            String resp = readAll(httpCode >= 400 ? c.getErrorStream() : c.getInputStream());
            fill(r, resp);
            if (httpCode >= 500 && r.error.isEmpty()) {
                r.error = "服务端出错了（HTTP " + httpCode + "）";
            }
            c.disconnect();
        } catch (Exception e) {
            Log.w(TAG, what + " failed", e);
            r.networkError = true;
            r.error = "连不上门户：" + e.getMessage();
        }
    }

    private static void getJson(Result r, String url, int timeoutMs, String what) {
        try {
            HttpURLConnection c = open(url, "GET", timeoutMs);
            int httpCode = c.getResponseCode();
            String resp = readAll(httpCode >= 400 ? c.getErrorStream() : c.getInputStream());
            fill(r, resp);
            if (httpCode == 404) r.deviceUnknown = true;
            c.disconnect();
        } catch (Exception e) {
            Log.w(TAG, what + " failed", e);
            r.networkError = true;
            r.error = "连不上门户：" + e.getMessage();
        }
    }

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
            r.daysLeft = o.has("daysLeft") ? o.optInt("daysLeft", -1) : -1;
            r.expired = o.optBoolean("expired", false);
            r.suspended = o.optBoolean("suspended", false);
            r.deviceLimit = o.optBoolean("device_limit", false);
            r.codeConsumed = o.optBoolean("code_consumed", false);
            r.deviceCount = o.optInt("deviceCount", 0);
            r.deviceMax = o.optInt("deviceMax", 0);
            r.deviceMessage = o.optString("deviceMessage", "");
            // 更新清单（/android/version 才有，其它接口没有这几个字段就为默认值）
            r.latest = o.optString("latest", "");
            r.latestCode = o.optInt("latestCode", 0);
            r.downloadUrl = o.optString("url", "");
            r.apkSize = o.optLong("size", 0);
            r.apkSha256 = o.optString("sha256", "");
            // enabled=0 且不是暂停，就是过期
            if (o.has("enabled") && !o.optBoolean("enabled", true) && !r.expired) {
                r.expired = true;
            }
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

    private static String enc(String s) {
        try {
            return java.net.URLEncoder.encode(s == null ? "" : s, "UTF-8");
        } catch (Exception e) {
            return "";
        }
    }
}
