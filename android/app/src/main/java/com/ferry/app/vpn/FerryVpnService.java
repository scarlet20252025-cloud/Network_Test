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

    /** 停止原因，跟着 ACTION_STOP 一起传。不传就是「用户停止」。 */
    public static final String EXTRA_REASON = "reason";

    /**
     * 授权检查周期（毫秒）。
     *
     * 必须由**服务自己**查，不能只靠 MainActivity 的 licWatch —— 那条路
     * 只在界面活着时跑。2026-10-30 实测：授权 13:17:49 到期，界面早被切到
     * 后台，13:54 隧道还在跑（服务端仍收到这台手机 141 条记录），
     * 等于白送了 36 分钟。
     *
     * 30 秒一次是有意的折中：SharedPreferences 是纯内存读，代价可忽略；
     * 而临近到期时会精确调度到到期那一刻（见 licWatchTick），不会白等这 30 秒。
     */
    private static final long LIC_CHECK_INTERVAL_MS = 30_000L;

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

    /** 授权复核定时器。跑在主线程，只做一次 SharedPreferences 读，不做 IO。 */
    private final android.os.Handler licHandler =
            new android.os.Handler(android.os.Looper.getMainLooper());
    private final Runnable licWatchTick = new Runnable() {
        @Override
        public void run() {
            if (!running) return;
            FerryConfig cfg = FerryConfig.load(FerryVpnService.this);
            if (cfg.locallyExpired()) {
                Log.i(TAG, "license expired while tunnel up, stopping");
                stopTunnel("授权已到期，隧道已断开；续期后重新点「开始借网」");
                stopSelf();
                return;
            }
            scheduleLicWatch(cfg);
        }
    };

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
            // 界面可以带原因过来（例如「授权过期」），这样状态栏说的
            // 就是真实原因，而不是一律「用户停止」。
            String reason = intent.getStringExtra(EXTRA_REASON);
            stopTunnel(reason == null || reason.isEmpty() ? "用户停止" : reason);
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

        // ⓪ 起隧道前先自己判一次授权。
        //
        // 界面在点「开始借网」时也会拦（MainActivity.startVpn），但那一路
        // 拦不住两种绕过方式：系统 Always-on VPN 用 null intent 直接拉起本服务，
        // 以及界面判完之后、隧道建好之前刚好跨过到期时刻。
        // 这里再判一次是最后一道闸，代价只是一次 SharedPreferences 读。
        if (cfg.locallyExpired()) {
            publish(STATE_ERROR, "授权已到期，不能借网；请续期后重试");
            running = false;
            stopSelf();
            return;
        }

        // ① 先确认远端 SOCKS5 真的能连上。
        //
        // **为什么必须先查（2026-10-30 实测）**：
        // 端口配错（remotePort=1099，而服务端只监听 1080）时，
        // TUN 建得起来、路由也全对、hev 也启动成功 —— 界面上「通道都通了」，
        // 但每个连接都是 io timeout，用户感觉「能连上但上不了网」。
        // hev.log 里刷的是一模一样的四行无限循环：
        //     io timeout
        //     socks5 client connect
        //     socks5 session connect
        //     socks5 client tcp -> [xxx]:443
        // 因为 hev 不知道上游连不上，只会一直接下去。
        //
        // 在这里先做一次 TCP 连通性探测，连不上就**明确报错并停止**，
        // 不建 TUN —— 比让用户面对「通道都通却上不了网」好得多。
        {
            String probeErr = probeUpstream(cfg.remoteHost, cfg.remotePort, 4000);
            if (probeErr != null) {
                publish(STATE_ERROR, probeErr);
                running = false;
                stopSelf();
                return;
            }
        }

        // ② 建 TUN
        Builder b = new Builder()
                .setSession("Ferry")
                .setMtu(TunConfig.MTU)
                .addAddress(TunConfig.TUN_ADDRESS_V4, TunConfig.TUN_PREFIX_V4)
                // 把 mapdns 的虚拟 DNS 配成系统 DNS —— 这样 App 发的 DNS 查询
                // 会落到 198.18.0.2，属于会进隧道的段，由 hev 在本地应答成 fake-ip。
                .addDnsServer(TunConfig.DNS_ADDRESS);

        // ② 只把「公网单播」导进隧道（**不是** 0.0.0.0/0）
        //
        // 原来的写法是 addRoute("0.0.0.0",0) 再 addRoute("10.0.0.0",8)，
        // 想「排除局域网」—— 完全相反：addRoute 是「导进隧道」，
        // 越具体优先级越高，等于把局域网也塞进隧道，然后被送到远端绕一圈，
        // 访问打印机/摄像头/路由器后台全部超时。VpnService 没有「排除」API。
        // 详见 TunConfig 里的长注释。
        for (String[] r : TunConfig.routesV4()) {
            b.addRoute(r[0], Integer.parseInt(r[1]));
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
        scheduleLicWatch(cfg);
    }

    /**
     * 排下一次授权复核。
     *
     * 临近到期时**精确**排到到期那一刻，而不是傻等 30 秒 ——
     * 用户签的是一天，不该因为我们自己的检查粒度多借出 30 秒。
     */
    private void scheduleLicWatch(FerryConfig cfg) {
        licHandler.removeCallbacks(licWatchTick);
        long delay = LIC_CHECK_INTERVAL_MS;
        if (cfg != null && cfg.expiresAt > 0) {
            long untilExpiry = cfg.expiresAt * 1000L - System.currentTimeMillis();
            if (untilExpiry > 0 && untilExpiry < delay) delay = untilExpiry;
        }
        licHandler.postDelayed(licWatchTick, Math.max(0, delay));
    }

    private void stopTunnel(String reason) {
        licHandler.removeCallbacks(licWatchTick);
        if (!running && tunFd == null) {
            // 已经停了就别再广播一次。
            //
            // 这里原来无条件 publish(STATE_OFF, reason)，结果每次真正停止时
            // 会广播两遍：一次带真实原因（"授权已到期…" / "用户停止"），
            // 紧接着 stopSelf() → onDestroy() → stopTunnel("服务销毁")
            // 再把文案盖成「服务销毁」。用户永远看不到真正的原因。
            // 2026-10-30 加「授权到期」文案时才发现 —— 之前所有停止原因都是白写的。
            if (!STATE_OFF.equals(lastState)) {
                publish(STATE_OFF, reason);
            }
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

    /**
     * 探测远端 SOCKS5 端口能不能连上。
     *
     * @return null = 通；否则返回给用户看的错误文案
     *
     * 只做 TCP 三次握手，不发 SOCKS5 协商 —— 我们要判断的是「端口有没有人听」，
     * 不是「服务正不正常」。端口不通时立刻返回，不必等满超时。
     *
     * 必须在**后台线程**调用（doStart 已经在 worker 线程里，没问题）。
     */
    private String probeUpstream(String host, int port, int timeoutMs) {
        java.net.Socket s = null;
        try {
            s = new java.net.Socket();
            s.connect(new java.net.InetSocketAddress(host, port), timeoutMs);
            return null;
        } catch (java.net.SocketTimeoutException e) {
            return "连不上出口主机 " + host + ":" + port + "（超时）。\n\n"
                    + "可能原因：\n"
                    + "  · 端口填错了（服务端实际监听的是 1080）\n"
                    + "  · 不在校园网 / 出口主机没开\n"
                    + "  · 校园网把该端口封了";
        } catch (java.net.ConnectException e) {
            return "连不上出口主机 " + host + ":" + port + "（连接被拒绝）。\n\n"
                    + "端口上没有服务在监听 —— 检查「设置」里的端口，"
                    + "服务端默认是 1080。";
        } catch (Exception e) {
            return "连不上出口主机 " + host + ":" + port + "：" + e.getMessage();
        } finally {
            if (s != null) {
                try {
                    s.close();
                } catch (Exception ignored) {
                }
            }
        }
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
