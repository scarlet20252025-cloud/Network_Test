package com.ferry.app;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Build;
import android.provider.Settings;

import java.util.UUID;

/**
 * 本地配置 + 设备身份 + 授权状态。
 *
 * 设备身份为什么要用 ANDROID_ID 而不是自己生成 UUID：
 *   2026-10-29 起授权改成「按设备」——一个账号可以绑多台设备。
 *   而「手机重装 App 后能自动恢复授权」的前提，是设备标识在重装后**不变**。
 *   自己生成的 UUID 存在 SharedPreferences 里，重装就没了；
 *   Settings.Secure.ANDROID_ID 是系统级、重装不变（同一次刷机内），正好合适。
 *   取不到（极少数 ROM）才退回 UUID，那种情况下自动恢复会失效，
 *   但重新输码激活仍然正常。
 */
public class FerryConfig {

    private static final String PREF = "ferry";
    private static final String K_PORTAL = "portalUrl";
    private static final String K_TOKEN = "token";
    private static final String K_CODE = "code";
    private static final String K_REMOTE_HOST = "remoteHost";
    private static final String K_REMOTE_PORT = "remotePort";
    private static final String K_EXPIRES = "expiresAt";
    private static final String K_DEVICE_ID = "deviceId";

    /** 默认门户地址。和 Windows 客户端 Config.cs 的默认值保持一致。 */
    public static final String DEFAULT_PORTAL = "http://10.13.24.1:8080";
    /** 默认远端 SOCKS5 端口。ferry-socks5-server.py 同时提供 TCP 与 UDP。 */
    public static final int DEFAULT_REMOTE_PORT = 1080;

    // ── 授权状态（只在内存里，不落盘）──
    //
    // 为什么要单独一个状态机而不是只看「有没有 token」：
    // 用户报过「界面显示未激活，但点开始借网又能用」。那是本地缓存和服务端
    // 不一致导致的。现在把状态显式建模，界面只说一种话，借网前也按它拦。
    public static final int LIC_UNKNOWN = 0;      // 还没校验过（刚装/刚启动）
    public static final int LIC_CHECKING = 1;     // 正在向服务端确认
    public static final int LIC_OK = 2;           // 有效
    public static final int LIC_EXPIRED = 3;      // 已过期
    public static final int LIC_INVALID = 4;      // token 无效（被撤销/换库）
    public static final int LIC_SUSPENDED = 5;    // 被管理员停用
    public static final int LIC_DEVICE_LIMIT = 6; // 设备数超限
    public static final int LIC_OFFLINE = 7;      // 连不上门户（不确定，但允许沿用缓存）

    public String portalUrl = DEFAULT_PORTAL;
    public String token = "";
    public String code = "";
    public String remoteHost = "10.13.24.1";
    public int remotePort = DEFAULT_REMOTE_PORT;
    /** 授权到期时间（unix 秒）。0 = 永久。 */
    public long expiresAt = 0;
    /** 设备标识。ANDROID_ID，重装不变。 */
    public String deviceId = "";

    // 内存态
    public int licState = LIC_UNKNOWN;
    public String licMessage = "";
    public int deviceCount = 0;
    public int deviceMax = 0;

    public static FerryConfig load(Context ctx) {
        SharedPreferences sp = ctx.getSharedPreferences(PREF, Context.MODE_PRIVATE);
        FerryConfig c = new FerryConfig();
        c.portalUrl = sp.getString(K_PORTAL, DEFAULT_PORTAL);
        c.token = sp.getString(K_TOKEN, "");
        c.code = sp.getString(K_CODE, "");
        c.remoteHost = sp.getString(K_REMOTE_HOST, "10.13.24.1");
        c.remotePort = sp.getInt(K_REMOTE_PORT, DEFAULT_REMOTE_PORT);
        c.expiresAt = sp.getLong(K_EXPIRES, 0);

        c.deviceId = sp.getString(K_DEVICE_ID, "");
        if (c.deviceId == null || c.deviceId.isEmpty()) {
            c.deviceId = resolveDeviceId(ctx);
            sp.edit().putString(K_DEVICE_ID, c.deviceId).apply();
        }
        return c;
    }

    /** 取设备标识：优先 ANDROID_ID（重装不变），拿不到才用随机 UUID。 */
    private static String resolveDeviceId(Context ctx) {
        try {
            String aid = Settings.Secure.getString(ctx.getContentResolver(),
                    Settings.Secure.ANDROID_ID);
            if (aid != null && aid.length() >= 8) return aid;
        } catch (Throwable ignored) {
            // 某些 ROM 会限制读取，不是错误，退回 UUID 即可
        }
        return UUID.randomUUID().toString();
    }

    /** 上报给服务端的设备描述，用来看「哪台设备」而不是靠 IP 猜。 */
    public String deviceModel() {
        String m = Build.MANUFACTURER + " " + Build.MODEL;
        return m.length() > 48 ? m.substring(0, 48) : m;
    }

    public void save(Context ctx) {
        ctx.getSharedPreferences(PREF, Context.MODE_PRIVATE).edit()
                .putString(K_PORTAL, portalUrl)
                .putString(K_TOKEN, token)
                .putString(K_CODE, code)
                .putString(K_REMOTE_HOST, remoteHost)
                .putInt(K_REMOTE_PORT, remotePort)
                .putLong(K_EXPIRES, expiresAt)
                .putString(K_DEVICE_ID, deviceId)
                .apply();
    }

    public boolean hasToken() {
        return token != null && !token.isEmpty();
    }

    /** 永久授权（expiresAt == 0 且有 token）。 */
    public boolean isPermanent() {
        return hasToken() && expiresAt == 0;
    }

    /**
     * 剩余天数。
     *
     * 返回 -1 表示「永久」，0 表示「已过期或未激活」。
     * 之所以要区分：服务端用 expiresAt=0 表示永久，如果这里也返回 0，
     * 永久授权的用户会看到「剩余 0 天」，像是过期了。
     */
    public int daysLeft() {
        if (!hasToken()) return 0;
        if (expiresAt <= 0) return -1;
        long left = expiresAt * 1000L - System.currentTimeMillis();
        if (left <= 0) return 0;
        // 不足一天也显示 1 天，别让用户看到「剩余 0 天」却还能用
        long days = left / 86400000L;
        return days <= 0 ? 1 : (int) days;
    }

    /** 本地就能判定的过期（不依赖网络）。 */
    public boolean locallyExpired() {
        if (!hasToken()) return false;
        if (expiresAt <= 0) return false;   // 永久
        return expiresAt * 1000L <= System.currentTimeMillis();
    }

    /** 现在到底能不能借网。只有明确不行才拦；网络异常时沿用缓存（LIC_OFFLINE）。 */
    public boolean canConnect() {
        if (!hasToken()) return false;
        if (locallyExpired()) return false;
        switch (licState) {
            case LIC_EXPIRED:
            case LIC_INVALID:
            case LIC_SUSPENDED:
            case LIC_DEVICE_LIMIT:
                return false;
            default:
                return true;
        }
    }

    /** 剩余时长的人话描述。 */
    public String leftText() {
        if (!hasToken()) return "未激活";
        if (expiresAt <= 0) return "永久有效";
        long left = expiresAt * 1000L - System.currentTimeMillis();
        if (left <= 0) return "已过期";
        long days = left / 86400000L;
        if (days >= 1) return "剩余 " + days + " 天";
        long hours = left / 3600000L;
        if (hours >= 1) return "剩余 " + hours + " 小时";
        return "剩余 " + Math.max(1, left / 60000L) + " 分钟";
    }
}
