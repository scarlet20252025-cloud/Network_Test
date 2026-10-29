package com.ferry.app.vpn;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Intent;
import android.net.VpnService;
import android.os.Build;
import android.os.ParcelFileDescriptor;
import android.util.Log;

import androidx.core.app.NotificationCompat;

import com.ferry.app.FerryConfig;
import com.ferry.app.MainActivity;
import com.ferry.app.R;

import java.io.File;
import java.io.FileOutputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;

import hev.htproxy.TProxyService;

/**
 * 渡口的 Android VPN 服务。
 *
 * 结构：
 *   VpnService（本类，Java） 建 TUN 网卡，把 fd 交给 native
 *     └─ libhev-socks5-tunnel.so（C）把 IP 包翻译成 SOCKS5 请求
 *          └─ 远端 ferry-socks5-server.py（已有，Python）
 *
 * 为什么不在 Java 里自己写 tun2socks：
 *   那需要一个完整的用户态 TCP/IP 栈（握手、拥塞控制、重传、分片重组、
 *   UDP 会话管理），几千行且极难做对。hev 用 lwIP + 自己的协程调度，
 *   已经在大量生产项目里验证过（AetherST、pingtunnel-client 等都在用）。
 */
public class FerryVpnService extends VpnService {

    private static final String TAG = "FerryVpn";

    public static final String ACTION_START = "com.ferry.app.START";
    public static final String ACTION_STOP = "com.ferry.app.STOP";

    /** 状态广播：界面靠它刷新，不用轮询。 */
    public static final String ACTION_STATE = "com.ferry.app.STATE";
    public static final String EXTRA_STATE = "state";
    public static final String EXTRA_MESSAGE = "message";

    public static final String STATE_OFF = "off";
    public static final String STATE_CONNECTING = "connecting";
    public static final String STATE_ON = "on";
    public static final String STATE_ERROR = "error";

    private static final String CHANNEL_ID = "ferry_vpn";
    private static final int NOTIF_ID = 1001;

    private ParcelFileDescriptor tunFd;
    private Thread worker;
    private volatile boolean running;
    private static String lastState = STATE_OFF;
    private static String lastMessage = "";

    public static String getLastState() {
        return lastState;
    }

    public static String getLastMessage() {
        return lastMessage;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        createChannel();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent == null ? null : intent.getAction();
        if (ACTION_STOP.equals(action)) {
            stopTunnel("用户停止");
            stopSelf();
            return START_NOT_STICKY;
        }
        // 默认按「开始」处理 —— 系统在 Always-on VPN 场景下会用 null intent 拉起
        if (running) {
            return START_STICKY;
        }
        startForeground(NOTIF_ID, buildNotification());
        startTunnel();
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        stopTunnel("服务销毁");
        super.onDestroy();
    }

    @Override
    public void onRevoke() {
        // 别的 VPN App 抢走了 VPN 权限，或者用户在系统设置里关掉了我们
        stopTunnel("VPN 授权被系统或其它应用收回");
        super.onRevoke();
    }

    // ── 隧道 ──

    private void startTunnel() {
        if (running) return;
        running = true;
        publish(STATE_CONNECTING, "正在建立隧道…");

        worker = new Thread(() -> {
            try {
                doStart();
            } catch (Throwable t) {
                Log.e(TAG, "start failed", t);
                publish(STATE_ERROR, "启动失败：" + friendly(t));
                running = false;
                stopSelf();
            }
        }, "ferry-vpn-start");
        worker.start();
    }

    private void doStart() throws Exception {
        FerryConfig cfg = FerryConfig.load(this);

        // ① 建 TUN
        Builder b = new Builder()
                .setSession("Ferry")
                .setMtu(TunConfig.MTU)
                .addAddress(TunConfig.TUN_ADDRESS_V4, TunConfig.TUN_PREFIX_V4)
                // 把 mapdns 的虚拟 DNS 配成系统 DNS —— 这样 App 发的 DNS 查询
                // 会被 TUN 抓到（198.18.0.2 属于 fake-ip 池，会进隧道）
                .addDnsServer(TunConfig.DNS_ADDRESS)
                // 0.0.0.0/0 = 全部流量进隧道。局域网段靠下面的路由排除。
                .addRoute("0.0.0.0", 0);

        // 排除局域网：不加这条，访问 10.x/192.168.x 的打印机、摄像头、
        // 远端主机自己，都会被塞进隧道绕一圈。
        for (String cidr : TunConfig.BYPASS_CIDRS) {
            String[] parts = cidr.split("/");
            b.addRoute(parts[0], Integer.parseInt(parts[1]));
        }
        // 本 App 自己不进隧道（否则代理请求又回到自己，成环）
        try {
            b.addDisallowedApplication(getPackageName());
        } catch (Exception ignored) {
            // 老系统上 addDisallowedApplication 可能抛，忽略即可
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            b.setMetered(false);
        }

        tunFd = b.establish();
        if (tunFd == null) {
            publish(STATE_ERROR, "建立 TUN 失败：可能是别的 VPN 应用正在运行");
            running = false;
            stopSelf();
            return;
        }

        // ② 写 hev 配置
        File confDir = new File(getFilesDir(), "hev");
        if (!confDir.exists()) confDir.mkdirs();
        File confFile = new File(confDir, "tunnel.yaml");
        File logFile = new File(confDir, "hev.log");
        String yaml = HevConfig.render(cfg.remoteHost, cfg.remotePort, logFile.getAbsolutePath());
        try (OutputStream os = new FileOutputStream(confFile)) {
            os.write(yaml.getBytes(StandardCharsets.UTF_8));
        }

        // ③ 起 native 隧道
        boolean ok;
        try {
            ok = TProxyService.TProxyStartService(confFile.getAbsolutePath(), tunFd.getFd());
        } catch (UnsatisfiedLinkError e) {
            publish(STATE_ERROR, "缺少 libhev-socks5-tunnel.so（构建时没编 native 库）");
            closeTun();
            running = false;
            stopSelf();
            return;
        }

        if (!ok) {
            publish(STATE_ERROR, "隧道启动失败，详见 " + logFile.getName());
            closeTun();
            running = false;
            stopSelf();
            return;
        }

        publish(STATE_ON, "已借网 · 出口 " + cfg.remoteHost + ":" + cfg.remotePort);
    }

    private void stopTunnel(String reason) {
        if (!running && tunFd == null) {
            publish(STATE_OFF, reason);
            return;
        }
        running = false;
        try {
            TProxyService.TProxyStopService();
        } catch (Throwable ignored) {
        }
        closeTun();
        stopForegroundCompat();
        publish(STATE_OFF, reason);
    }

    private void closeTun() {
        if (tunFd != null) {
            try {
                tunFd.close();
            } catch (Exception ignored) {
            }
            tunFd = null;
        }
    }

    private static String friendly(Throwable t) {
        String m = t.getMessage();
        if (m == null) m = t.getClass().getSimpleName();
        if (m.contains("Permission")) return "没有 VPN 授权，请在系统弹窗里点「确定」";
        return m;
    }

    // ── 通知 ──

    private void createChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return;
        NotificationManager nm = getSystemService(NotificationManager.class);
        if (nm == null) return;
        NotificationChannel ch = new NotificationChannel(
                CHANNEL_ID, getString(R.string.notif_channel_name), NotificationManager.IMPORTANCE_LOW);
        ch.setShowBadge(false);
        nm.createNotificationChannel(ch);
    }

    private Notification buildNotification() {
        Intent open = new Intent(this, MainActivity.class);
        int flags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            flags |= PendingIntent.FLAG_IMMUTABLE;
        }
        PendingIntent pi = PendingIntent.getActivity(this, 0, open, flags);

        return new NotificationCompat.Builder(this, CHANNEL_ID)
                .setContentTitle(getString(R.string.notif_title))
                .setContentText(getString(R.string.notif_text))
                .setSmallIcon(R.drawable.ic_launcher_foreground)
                .setOngoing(true)
                .setContentIntent(pi)
                .build();
    }

    private void stopForegroundCompat() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            stopForeground(STOP_FOREGROUND_REMOVE);
        } else {
            stopForeground(true);
        }
    }

    // ── 状态广播 ──

    private void publish(String state, String message) {
        lastState = state;
        lastMessage = message == null ? "" : message;
        Log.i(TAG, state + " : " + lastMessage);
        Intent i = new Intent(ACTION_STATE);
        i.setPackage(getPackageName());
        i.putExtra(EXTRA_STATE, lastState);
        i.putExtra(EXTRA_MESSAGE, lastMessage);
        sendBroadcast(i);
    }
}
