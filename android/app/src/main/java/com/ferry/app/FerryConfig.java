package com.ferry.app;

import android.content.Context;
import android.content.SharedPreferences;

/**
 * 本地配置。用 SharedPreferences —— 手机端没有 Windows 端那套「令牌不能丢」的
 * 极端要求（重装 App 才丢，而重装本来就是用户主动行为），不必再加备份层。
 */
public class FerryConfig {

    private static final String PREF = "ferry";
    private static final String K_PORTAL = "portalUrl";
    private static final String K_TOKEN = "token";
    private static final String K_CODE = "code";
    private static final String K_REMOTE_HOST = "remoteHost";
    private static final String K_REMOTE_PORT = "remotePort";
    private static final String K_EXPIRES = "expiresAt";

    /** 默认门户地址。和 Windows 客户端 Config.cs 的默认值保持一致。 */
    public static final String DEFAULT_PORTAL = "http://10.13.24.1:8080";
    /** 默认远端 SOCKS5 端口。ferry-socks5-server.py 同时提供 TCP 与 UDP。 */
    public static final int DEFAULT_REMOTE_PORT = 1080;

    public String portalUrl = DEFAULT_PORTAL;
    public String token = "";
    public String code = "";
    public String remoteHost = "10.13.24.1";
    public int remotePort = DEFAULT_REMOTE_PORT;
    public long expiresAt = 0;

    public static FerryConfig load(Context ctx) {
        SharedPreferences sp = ctx.getSharedPreferences(PREF, Context.MODE_PRIVATE);
        FerryConfig c = new FerryConfig();
        c.portalUrl = sp.getString(K_PORTAL, DEFAULT_PORTAL);
        c.token = sp.getString(K_TOKEN, "");
        c.code = sp.getString(K_CODE, "");
        c.remoteHost = sp.getString(K_REMOTE_HOST, "10.13.24.1");
        c.remotePort = sp.getInt(K_REMOTE_PORT, DEFAULT_REMOTE_PORT);
        c.expiresAt = sp.getLong(K_EXPIRES, 0);
        return c;
    }

    public void save(Context ctx) {
        ctx.getSharedPreferences(PREF, Context.MODE_PRIVATE).edit()
                .putString(K_PORTAL, portalUrl)
                .putString(K_TOKEN, token)
                .putString(K_CODE, code)
                .putString(K_REMOTE_HOST, remoteHost)
                .putInt(K_REMOTE_PORT, remotePort)
                .putLong(K_EXPIRES, expiresAt)
                .apply();
    }

    public boolean hasToken() {
        return token != null && !token.isEmpty();
    }

    /** 剩余天数；0 表示已过期或未激活。 */
    public int daysLeft() {
        if (expiresAt <= 0) return 0;
        long left = expiresAt * 1000L - System.currentTimeMillis();
        if (left <= 0) return 0;
        return (int) (left / 86400000L);
    }
}
