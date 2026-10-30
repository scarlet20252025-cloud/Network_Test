package com.ferry.app;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.net.VpnService;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.TextUtils;
import android.util.Log;
import android.widget.EditText;
import android.widget.TextView;

import androidx.annotation.Nullable;
import androidx.appcompat.app.AppCompatActivity;
import androidx.core.content.ContextCompat;

import com.ferry.app.vpn.FerryVpnService;
import com.google.android.material.button.MaterialButton;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

/**
 * 主界面。刻意做得极简：一个状态卡 + 一个激活卡 + 一块日志。
 *
 * 为什么不用 Compose：见 android/DESIGN.md 第三节 —— 依赖越少，
 * 远端构建越稳，而界面的复杂度根本不值一整套工具链。
 */
public class MainActivity extends AppCompatActivity {

    private static final int REQ_VPN = 100;

    private TextView txtState, txtDetail, txtLog;
    private EditText txtCode;
    private MaterialButton btnToggle, btnActivate, btnSelfCheck, btnChangeCode;
    /** 「检查更新」入口（2026-10-30 新增） */
    private MaterialButton btnUpdate;
    /** 激活卡的两种形态：未激活显示输入框，已激活显示摘要。 */
    private TextView txtActivateTitle, txtActivationInfo, txtActivationSub;
    private android.view.View rowInput, boxActivated;
    /** 用户点了「换一个激活码」就强制显示输入框，直到激活成功。 */
    private boolean forceShowInput = false;

    private FerryConfig cfg;

    /** 授权状态兜底轮询用的 Handler（见 scheduleLicenseWatch）。 */
    private Handler licWatch;
    /** 上次界面已知的服务状态，用来在 onResume 时发现「息屏期间错过了什么」。 */
    private String lastSeenState = FerryVpnService.STATE_OFF;
    private final StringBuilder logBuf = new StringBuilder();

    private final BroadcastReceiver stateReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            String state = intent.getStringExtra(FerryVpnService.EXTRA_STATE);
            String msg = intent.getStringExtra(FerryVpnService.EXTRA_MESSAGE);
            applyState(state, msg);
            if (msg != null && !msg.isEmpty()) log(msg);
            if (state != null) lastSeenState = state;
        }
    };

    @Override
    protected void onCreate(@Nullable Bundle b) {
        super.onCreate(b);
        setContentView(R.layout.activity_main);

        txtState = findViewById(R.id.txtState);
        txtDetail = findViewById(R.id.txtDetail);
        txtLog = findViewById(R.id.txtLog);
        txtCode = findViewById(R.id.txtCode);
        btnToggle = findViewById(R.id.btnToggle);
        btnActivate = findViewById(R.id.btnActivate);
        btnSelfCheck = findViewById(R.id.btnSelfCheck);
        btnUpdate = findViewById(R.id.btnUpdate);

        txtActivateTitle = findViewById(R.id.txtActivateTitle);
        rowInput = findViewById(R.id.rowInput);
        boxActivated = findViewById(R.id.boxActivated);
        txtActivationInfo = findViewById(R.id.txtActivationInfo);
        txtActivationSub = findViewById(R.id.txtActivationSub);
        btnChangeCode = findViewById(R.id.btnChangeCode);

        cfg = FerryConfig.load(this);

        btnToggle.setOnClickListener(v -> onToggle());
        btnActivate.setOnClickListener(v -> onActivate());
        btnSelfCheck.setOnClickListener(v -> runSelfCheck());
        // 检查更新：silent=false —— 这是用户主动点的，没新版也要给个提示，
        // 否则「点了没反应」会让人以为按钮坏了。
        btnUpdate.setOnClickListener(v -> {
            log(getString(R.string.update_checking));
            Updater.checkAndPrompt(this, cfg, false);
        });

        // 「换一个激活码」：已激活时把输入框重新露出来。
        // 没有这个入口，隐藏输入框之后用户就换不了账号了 —— 会变成死路。
        btnChangeCode.setOnClickListener(v -> {
            forceShowInput = true;
            txtCode.setText("");
            refreshActivationCard();
            txtCode.requestFocus();
            log("请输入新的激活码");
        });

        if (cfg.hasToken()) {
            // 先用本地缓存渲染，别等网络 —— 否则联网慢的时候会闪一下「未激活」
            txtCode.setText(cfg.code);
            cfg.licState = FerryConfig.LIC_CHECKING;
            log("已激活，" + cfg.leftText());
        }
        // 顺序同 onResume：先铺授权详情，再按服务状态覆盖主标题。
        // 反过来写的话 applyState 刚设好的「授权已到期，隧道已断开…」
        // 会被 refreshDetail 立刻盖成「授权已过期」—— 用户看不到原因。
        refreshDetail();
        String initState = FerryVpnService.getLastState();
        applyState(initState, FerryVpnService.STATE_ON.equals(initState)
                ? null : FerryVpnService.getLastMessage());
        lastSeenState = initState;

        // 授权状态监控：启动即核对一次，之后跟着借网启停再核对，
        // 另外每 5 分钟兜底一次（用户挂着不关的时候，过期了要能发现）。
        syncLicense();
        scheduleLicenseWatch();

        // 启动时静默检查一次更新（silent=true：没新版完全不打扰）。
        //
        // 为什么延迟 3 秒：启动瞬间要拉授权、建界面，再抢网络没意义；
        // 而且用户刚打开 App 就弹「发现新版本」很突兀。
        // 检查失败一律静默（校园网抖动很常见，不能因此打扰用户）。
        btnUpdate.postDelayed(() -> Updater.checkAndPrompt(this, cfg, true), 3000);

        // 自动化测试入口：am start ... --ez autostart true
        //
        // 为什么要留这个：真机/模拟器上没法自动点系统 VPN 授权框之后的「开始」按钮，
        // 而端到端验证必须真的把隧道拉起来。加一个显式意图参数最省事，
        // 也不会被误触发（普通启动不带这个 extra）。
        handleAutoIntent(getIntent());
    }

    /**
     * 处理 autostart / selfcheck 两个自动化意图。
     *
     * ⚠ 必须同时挂在 onCreate 和 onNewIntent 上。2026-10-29 真机实测踩到：
     * 因为 launchMode 是 singleTask，Activity 已经在前台时再 `am start --ez autostart true`
     * 只会走 onNewIntent，onCreate 不再执行 —— 于是「命令发出去了、什么都没发生」，
     * 而界面上看不出任何区别。这类"静默不生效"最难查。
     */
    private void handleAutoIntent(Intent it) {
        if (it == null) return;
        if (it.getBooleanExtra("autostart", false)) {
            log("收到 autostart，自动开始借网");
            btnToggle.postDelayed(this::startVpn, 800);
        }
        if (it.getBooleanExtra("selfcheck", false)) {
            log("收到 selfcheck，自动自检");
            btnSelfCheck.postDelayed(this::runSelfCheck, 1200);
        }
        // 测试用：模拟「界面判完之后、隧道建好之前刚好跨过到期时刻」。
        //
        // 为什么需要它：doStart() 里那道「起隧道前再判一次授权」的前门，
        // 正常点按根本走不到 —— 界面在 startVpn() 就拦下了。它防的是
        // 系统 Always-on VPN 用 null intent 拉起、以及这个时间窗竞态。
        // 这两条在真机上都复现不了：服务 exported=false，`am startservice`
        // 直接被拒；Always-on 在 vivo 上设了 secure setting 也不拉起
        // （实测 always_on_vpn_app 写入成功但进程没起）。
        //
        // ⚠ 必须**改盘再发**。早先试过「外面 run-as 改磁盘 + forcestart」，
        // 结果隧道照建 —— 因为 SharedPreferences 在同一进程里有内存缓存，
        // FerryConfig.load() 读到的是缓存，不是磁盘。所以这里调 cfg.save()
        // 让缓存和磁盘一起变，才真正还原「服务读盘时已过期」这个条件。
        if (it.getBooleanExtra("forceexpiredstart", false)) {
            log("收到 forceexpiredstart，模拟授权在起隧道前一刻过期");
            btnToggle.postDelayed(() -> {
                cfg.expiresAt = System.currentTimeMillis() / 1000L - 60;
                cfg.save(this);
                log("已把本地 expiresAt 置为过去，直发 ACTION_START（不走 startVpn 的拦截）");
                launchService();
            }, 800);
        }
        // 诊断用：在 App 自己的进程里试读 /proc/net/dev。
        //
        // 为什么必须「在 App 内读」：交接文档 P2 建议自检 ④ 改成
        // 「读 /proc/net/dev 的 tun0 字节增量」，并注明「不需要 root，Android 10+ 可读」。
        // 但用 `adb shell run-as <pkg> cat /proc/net/dev` 实测是 Permission denied ——
        // 然而 run-as 跑在 **runas_app** 域，App 本体跑在 **untrusted_app** 域，
        // 两者是**不同的 SELinux 域**，run-as 的结果不能代表 App 的真实权限。
        // 只能让 App 自己读一次，把结果写到 files/ 下再取回来看。
        if (it.getBooleanExtra("probeifs", false)) {
            log("收到 probeifs，在 App 内试读 /proc/net/dev");
            new Thread(() -> {
                StringBuilder sb = new StringBuilder();
                sb.append("uid=").append(android.os.Process.myUid()).append('\n');
                sb.append("selinux=").append(readFile("/proc/self/attr/current")).append('\n');
                String dev = readFile("/proc/net/dev");
                if (dev.startsWith("ERR:")) {
                    sb.append("proc_net_dev=").append(dev).append('\n');
                } else {
                    sb.append("proc_net_dev=OK\n");
                    for (String line : dev.split("\n")) {
                        String t = line.trim();
                        if (t.startsWith("tun") || t.startsWith("wlan")) {
                            sb.append("  ").append(t).append('\n');
                        }
                    }
                }
                // hev 自己报的计数（对照组：这个肯定能拿到）
                try {
                    long[] st = hev.htproxy.TProxyService.TProxyGetStats();
                    if (st != null && st.length >= 4) {
                        sb.append("hev_stats=txPkts=").append(st[0])
                          .append(" txBytes=").append(st[1])
                          .append(" rxPkts=").append(st[2])
                          .append(" rxBytes=").append(st[3]).append('\n');
                    } else {
                        sb.append("hev_stats=NULL\n");
                    }
                } catch (Throwable t) {
                    sb.append("hev_stats=ERR:").append(t.getClass().getSimpleName()).append('\n');
                }
                try {
                    java.io.FileOutputStream fos = openFileOutput("probeifs.txt", MODE_PRIVATE);
                    fos.write(sb.toString().getBytes("UTF-8"));
                    fos.close();
                } catch (Exception e) {
                    Log.w("FerryUi", "写 probeifs.txt 失败", e);
                }
            }, "ferry-probeifs").start();
        }
    }

    /** 读一个小文本文件；失败返回 "ERR:<异常名>: <消息>"，不抛。 */
    private static String readFile(String path) {
        java.io.BufferedReader br = null;
        try {
            br = new java.io.BufferedReader(new java.io.FileReader(path));
            StringBuilder sb = new StringBuilder();
            String line;
            while ((line = br.readLine()) != null) sb.append(line).append('\n');
            return sb.toString();
        } catch (Exception e) {
            return "ERR:" + e.getClass().getSimpleName() + ": " + e.getMessage();
        } finally {
            if (br != null) try { br.close(); } catch (Exception ignored) {}
        }
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handleAutoIntent(intent);
    }

    /**
     * 授权状态兜底轮询。
     *
     * 为什么需要：用户可能挂着 App 很久不关（甚至息屏），而授权到期是
     * 服务端的时间，本地只能靠缓存的 expiresAt 推断。每 5 分钟核对一次，
     * 到期时能主动告诉用户，而不是等他点了「开始借网」才发现连不上。
     *
     * **到期必须连隧道一起停**（2026-10-30 修的 bug）：
     * 这里原来只改界面文字、不碰隧道，而 FerryVpnService 也不读授权，
     * 于是「界面写着授权已过期、隧道照跑」。当天实测：授权 13:17:49 到期，
     * 13:54 服务端还在收这台手机的流量，白用 36 分钟。
     * 现在两处都加了：这里的 licWatch 停隧道是「即时」的那一路，
     * 服务内自己的复核（FerryVpnService.licWatchTick）是界面被冻结时的兜底。
     *
     * 用 Handler 而不是 WorkManager：这个检查只在界面活着的时候有意义，
     * 进程被杀掉之后本来就该靠下次启动时的 syncLicense()。
     */
    private void scheduleLicenseWatch() {
        if (licWatch == null) {
            licWatch = new Handler(Looper.getMainLooper());
        }
        licWatch.removeCallbacksAndMessages(null);
        licWatch.postDelayed(new Runnable() {
            @Override
            public void run() {
                if (isFinishing() || isDestroyed()) return;
                // 本地已判过期就不用再问服务端了，直接标记
                if (cfg.locallyExpired()) {
                    if (cfg.licState != FerryConfig.LIC_EXPIRED) {
                        cfg.licState = FerryConfig.LIC_EXPIRED;
                        cfg.licMessage = "授权已过期";
                        log("授权已过期，需要续期");
                        refreshDetail();
                    }
                    // 关键：授权没了就不能再借网。隧道如果还开着就停掉。
                    if (isTunnelUp()) {
                        log("授权已过期，已为你断开隧道");
                        stopTunnelWithReason("授权已到期，隧道已断开；续期后重新点「开始借网」");
                    }
                } else {
                    syncLicense();
                }
                licWatch.postDelayed(this, 5 * 60 * 1000L);
            }
        }, 5 * 60 * 1000L);
    }

    private boolean isTunnelUp() {
        String state = FerryVpnService.getLastState();
        return FerryVpnService.STATE_ON.equals(state)
                || FerryVpnService.STATE_CONNECTING.equals(state);
    }

    /** 停隧道并说明原因。原因会显示在状态栏，别让人以为是自己点错了。 */
    private void stopTunnelWithReason(String reason) {
        Intent i = new Intent(this, FerryVpnService.class)
                .setAction(FerryVpnService.ACTION_STOP)
                .putExtra(FerryVpnService.EXTRA_REASON, reason);
        startService(i);
    }

    @Override
    protected void onDestroy() {
        if (licWatch != null) licWatch.removeCallbacksAndMessages(null);
        super.onDestroy();
    }

    /** 自检：五步逐层验证隧道，结果写进日志（也回显在状态栏）。 */
    private void runSelfCheck() {
        btnSelfCheck.setEnabled(false);
        log(getString(R.string.selfcheck_running));
        // 传 0：Android 侧没有本地代理端口（hev 直接连远端 SOCKS5），
        // 且 App 自己被 addDisallowedApplication 排除在 VPN 路由外，
        // 所以本进程的 HTTP 请求注定走物理网卡。
        // 若物理网卡没网（校园网禁网时段），这一项必然失败 —— 是假阴性。
        // SelfCheck 会据此把这项标成「仅供参考」，不计入失败。
        SelfCheck.run(0, (items, summary) -> runOnUiThread(() -> {
            btnSelfCheck.setEnabled(true);
            for (SelfCheck.Item i : items) log("  " + i);
            log("自检结果：" + summary);
            txtDetail.setText(summary);
            // 落一份文件：adb 抓 logcat 有时序丢行的问题，
            // 自检结果单独写文件更可靠（也方便用户直接发给我）。
            try {
                StringBuilder sb = new StringBuilder();
                sb.append(new SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(new Date()))
                  .append('\n');
                for (SelfCheck.Item i : items) sb.append(i).append('\n');
                sb.append(summary).append('\n');
                java.io.FileOutputStream fos =
                        openFileOutput("selfcheck.txt", MODE_PRIVATE);
                fos.write(sb.toString().getBytes("UTF-8"));
                fos.close();
            } catch (Exception e) {
                Log.w("FerryUi", "写 selfcheck.txt 失败", e);
            }
        }));
    }

    @Override
    protected void onResume() {
        super.onResume();
        IntentFilter f = new IntentFilter(FerryVpnService.ACTION_STATE);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(stateReceiver, f, Context.RECEIVER_NOT_EXPORTED);
        } else {
            registerReceiver(stateReceiver, f);
        }
        // 注册完必须**回读一次**服务状态。
        //
        // 为什么：广播是「注册期间才收得到」的一次性事件，onPause 里已经注销了。
        // 息屏（或切到别的 App）期间隧道如果停了 —— 最典型的是授权到期后
        // FerryVpnService.licWatchTick 自己断开 —— 那条 STATE_OFF 广播就没人收。
        // 点亮屏幕回来时 onResume 只重新注册，界面还停在息屏前的「已借网」，
        // 按钮还是「停止并恢复直连」，而 tun0 其实早就没了。
        // 2026-10-30 真机实测：14:26:59 隧道已断，14:27 亮屏界面仍显示「已借网」。
        // 顺序：先 refreshDetail 铺授权详情（出口/剩余/设备），再按服务状态刷主标题；
        // 只有「停止/错误」这类原因才盖上去 —— 「已借网 · 出口…」那句比授权详情
        // 信息少，盖上去反而丢东西。
        refreshDetail();
        String st = FerryVpnService.getLastState();
        String msg = FerryVpnService.getLastMessage();
        applyState(st, FerryVpnService.STATE_ON.equals(st) ? null : msg);
        // 把「回来时才发现的状态变化」补进日志。
        //
        // 广播丢了，界面日志就断在息屏前最后一条「已借网」，用户/排查的人
        // 根本看不出是什么时候、因为什么断的。这里对比一次就补上。
        if (!st.equals(lastSeenState)) {
            if (msg != null && !msg.isEmpty()) log(msg);
            lastSeenState = st;
        }
    }

    @Override
    protected void onPause() {
        super.onPause();
        try {
            unregisterReceiver(stateReceiver);
        } catch (Exception ignored) {
        }
    }

    // ── 交互 ──

    private void onToggle() {
        String state = FerryVpnService.getLastState();
        if (FerryVpnService.STATE_ON.equals(state) || FerryVpnService.STATE_CONNECTING.equals(state)) {
            Intent i = new Intent(this, FerryVpnService.class).setAction(FerryVpnService.ACTION_STOP);
            startService(i);
            return;
        }
        startVpn();
    }

    private void startVpn() {
        if (!cfg.hasToken()) {
            log("还没激活，先在上面填激活码");
            return;
        }
        // 借网前先按本地状态拦一次。
        //
        // 用户报过「界面显示未激活，但点开始借网又能用」—— 那是本地缓存和服务端
        // 不一致。现在只要本地明确知道「过期/失效/停用/设备超限」就不放行，
        // 并且说清是哪种，别让人猜。
        if (cfg.locallyExpired()) {
            cfg.licState = FerryConfig.LIC_EXPIRED;
            refreshDetail();
            log("授权已过期（本地时间 " + cfg.leftText() + "），请续期后再试");
            return;
        }
        if (!cfg.canConnect()) {
            refreshDetail();
            log("现在不能借网：" + cfg.licMessage);
            return;
        }
        // prepare() 会弹系统的 VPN 授权框。第一次必然弹，之后系统记住。
        Intent prep = VpnService.prepare(this);
        if (prep != null) {
            // 用户之前拒绝过，需要再走一次授权流程
            startActivityForResult(prep, REQ_VPN);
            return;
        }
        launchService();
    }

    private void launchService() {
        Intent i = new Intent(this, FerryVpnService.class).setAction(FerryVpnService.ACTION_START);
        ContextCompat.startForegroundService(this, i);
    }

    @Override
    protected void onActivityResult(int req, int res, @Nullable Intent data) {
        super.onActivityResult(req, res, data);
        if (req == REQ_VPN) {
            if (res == RESULT_OK) {
                launchService();
            } else {
                log(getString(R.string.vpn_permission_hint));
            }
        }
    }

    private void onActivate() {
        String code = txtCode.getText().toString().trim().toUpperCase();
        if (TextUtils.isEmpty(code)) {
            log("请填激活码");
            return;
        }
        btnActivate.setEnabled(false);
        log("正在激活…");
        new Thread(() -> {
            FerryApi.Result r = FerryApi.activate(cfg.portalUrl, code, cfg.deviceId,
                    cfg.deviceModel(), appVersion(), 10000);
            runOnUiThread(() -> {
                btnActivate.setEnabled(true);
                if (r.ok) {
                    cfg.code = code;
                    if (!r.token.isEmpty()) cfg.token = r.token;
                    if (!r.remoteHost.isEmpty()) cfg.remoteHost = r.remoteHost;
                    if (r.remotePort > 0) cfg.remotePort = r.remotePort;
                    // expiresAt 允许为 0（= 永久），所以这里直接赋值，不要加 >0 判断
                    cfg.expiresAt = r.expiresAt;
                    cfg.licState = FerryConfig.LIC_OK;
                    cfg.licMessage = "";
                    cfg.deviceCount = r.deviceCount;
                    cfg.deviceMax = r.deviceMax;
                    cfg.save(MainActivity.this);
                    // 激活成功后把输入框收起来（用户要求），除非他再点「换一个」
                    forceShowInput = false;
                    log("激活成功，" + cfg.leftText());
                    if (r.deviceMax > 0 && r.deviceCount > 0) {
                        log("已绑定 " + r.deviceCount + "/" + r.deviceMax + " 台设备");
                    }
                    if (!r.deviceMessage.isEmpty()) log(r.deviceMessage);
                    refreshDetail();
                } else {
                    // 设备超限是一个特殊分支：密钥已经被核销掉了，
                    // 再点一次会撞上「密钥已使用」。必须说清楚，别让用户白试。
                    if (r.deviceLimit) {
                        log("激活失败：设备数已满。" + r.error);
                        if (r.codeConsumed) {
                            log("注意：这个激活码已经被核销，重复提交会提示「已使用」。"
                                    + "请在其它设备上退出，或找管理员加设备。");
                        }
                    } else {
                        log("激活失败：" + r.error);
                    }
                }
            });
        }, "ferry-activate").start();
    }

    /** 应用版本号，上报给服务端看「哪台设备跑的哪个版本」。 */
    private String appVersion() {
        try {
            return getPackageManager().getPackageInfo(getPackageName(), 0).versionName;
        } catch (Exception e) {
            return "";
        }
    }

    /**
     * 授权状态同步：本地乐观显示 + 后台向服务端核对。
     *
     * 为什么要「乐观」：
     *   用户报过「明明激活了，打开却显示未激活」。原因是启动瞬间还没联网校验，
     *   界面就把状态写成未激活。现在启动先用本地缓存渲染（有 token 就按
     *   缓存里的到期时间显示「已激活 / 剩余 N 天」），等校验结果回来再精确更新。
     *
     * 三种结果的处理：
     *   · 服务端确认有效 → LIC_OK
     *   · 连不上门户      → LIC_OFFLINE，**不改变**可用性（沿用缓存），
     *                       否则校园网抖动一下就把人踢下线
     *   · 服务端明确否定  → 按 expired/suspended/device_limit 分别标记，拦住借网
     */
    private void syncLicense() {
        if (!cfg.hasToken()) {
            // 没有 token：可能是重装过。尝试凭设备标识自动恢复。
            tryRestore();
            return;
        }
        cfg.licState = FerryConfig.LIC_CHECKING;
        refreshDetail();
        new Thread(() -> {
            FerryApi.Result r = FerryApi.checkLicense(cfg.portalUrl, cfg.token, cfg.deviceId,
                    cfg.deviceModel(), appVersion(), 8000);
            runOnUiThread(() -> applyLicenseResult(r, false));
        }, "ferry-license").start();
    }

    /** 没有本地 token 时，凭 ANDROID_ID 问服务端「这台设备登记过吗」。 */
    private void tryRestore() {
        new Thread(() -> {
            FerryApi.Result r = FerryApi.restoreDevice(cfg.portalUrl, cfg.deviceId,
                    cfg.deviceModel(), appVersion(), 6000);
            runOnUiThread(() -> {
                if (r.ok && !r.token.isEmpty()) {
                    cfg.token = r.token;
                    cfg.expiresAt = r.expiresAt;
                    if (!r.remoteHost.isEmpty()) cfg.remoteHost = r.remoteHost;
                    if (r.remotePort > 0) cfg.remotePort = r.remotePort;
                    cfg.licState = FerryConfig.LIC_OK;
                    cfg.deviceCount = r.deviceCount;
                    cfg.deviceMax = r.deviceMax;
                    cfg.save(MainActivity.this);
                    log("这台设备之前激活过，已自动恢复授权（" + cfg.leftText() + "）");
                    refreshDetail();
                } else if (r.deviceUnknown) {
                    // 正常情况：新设备，什么都不用说，等用户填码
                    cfg.licState = FerryConfig.LIC_UNKNOWN;
                    refreshDetail();
                } else if (r.networkError) {
                    cfg.licState = FerryConfig.LIC_OFFLINE;
                    refreshDetail();
                } else {
                    log("自动恢复授权失败：" + r.error);
                    cfg.licState = FerryConfig.LIC_UNKNOWN;
                    refreshDetail();
                }
            });
        }, "ferry-restore").start();
    }

    private void applyLicenseResult(FerryApi.Result r, boolean quiet) {
        if (r.networkError) {
            // 连不上门户不等于授权无效 —— 校园网本来就可能抖动。
            // 这里只记状态，不动可用性。
            cfg.licState = FerryConfig.LIC_OFFLINE;
            cfg.licMessage = "暂时连不上门户，继续用上次的授权信息";
            if (!quiet) log("授权检查：连不上门户，沿用本地缓存");
            refreshDetail();
            return;
        }
        if (r.ok) {
            cfg.expiresAt = r.expiresAt;
            if (r.remotePort > 0) cfg.remotePort = r.remotePort;
            if (!r.remoteHost.isEmpty()) cfg.remoteHost = r.remoteHost;
            cfg.deviceCount = r.deviceCount;
            cfg.deviceMax = r.deviceMax;
            cfg.licMessage = "";
            if (r.suspended) {
                cfg.licState = FerryConfig.LIC_SUSPENDED;
                cfg.licMessage = "授权已被管理员停用";
            } else if (r.expired) {
                cfg.licState = FerryConfig.LIC_EXPIRED;
                cfg.licMessage = "授权已过期";
            } else {
                cfg.licState = FerryConfig.LIC_OK;
            }
            cfg.save(this);
            if (!quiet && cfg.licState != FerryConfig.LIC_OK) log(cfg.licMessage);
            refreshDetail();
            return;
        }
        // 服务端明确拒绝
        if (r.deviceLimit) {
            cfg.licState = FerryConfig.LIC_DEVICE_LIMIT;
            cfg.licMessage = r.error;
        } else {
            cfg.licState = FerryConfig.LIC_INVALID;
            cfg.licMessage = r.error.isEmpty() ? "授权无效" : r.error;
        }
        log("授权检查：" + cfg.licMessage);
        refreshDetail();
    }

    // ── 界面 ──

    private void applyState(String state, String msg) {
        if (state == null) state = FerryVpnService.STATE_OFF;
        switch (state) {
            case FerryVpnService.STATE_ON:
                txtState.setText(R.string.state_on);
                // 「已借网」用绿色，和 Windows 端对齐（那边大状态「正在走渡口」
                // 也是 Palette.GoodB 薄荷绿）。2026-10-30 用户明确要求：
                // 「已借网 的文字状态提示要跟电脑端一样变成绿色，而不是保持一个颜色」。
                setStateColor(R.color.ferry_good);
                btnToggle.setText(R.string.btn_stop);
                break;
            case FerryVpnService.STATE_CONNECTING:
                txtState.setText(R.string.state_connecting);
                // 正在建立：用暖黄，表示「还没好，等一下」。
                // 不给绿色 —— 绿色应该只属于「真的通了」，否则用户会
                // 把"正在连"和"已连上"看混（Windows 端也是这么分的）。
                setStateColor(R.color.ferry_warn);
                btnToggle.setText(R.string.btn_stop);
                break;
            case FerryVpnService.STATE_ERROR:
                txtState.setText(R.string.state_error);
                setStateColor(R.color.ferry_bad);
                btnToggle.setText(R.string.btn_start);
                break;
            default:
                txtState.setText(R.string.state_off);
                // 未连接回到正常文字色（不是灰色）—— 它是常态，不是错误。
                setStateColor(R.color.ferry_text);
                btnToggle.setText(R.string.btn_start);
                break;
        }
        if (msg != null && !msg.isEmpty()) txtDetail.setText(msg);
    }

    /** 设置大状态文字的颜色。取不到颜色资源时保持原样，不抛。 */
    private void setStateColor(int colorRes) {
        try {
            txtState.setTextColor(androidx.core.content.ContextCompat.getColor(this, colorRes));
        } catch (Exception ignored) {
        }
    }

    /**
     * 激活卡的两种形态。
     *
     * 用户要求：激活成功后应该隐藏输入框。同时也要能退回来 ——
     * 授权失效、或者用户主动点「换一个激活码」时，输入框必须重新出现，
     * 否则就是死路。
     *
     * 判据用 canConnect() 而不是「有没有 token」：token 还在但已过期时，
     * 继续显示「已激活」是骗人的，那时候就该让用户看到输入框去续期。
     */
    private void refreshActivationCard() {
        boolean activated = cfg.hasToken() && cfg.canConnect() && !forceShowInput;
        if (activated) {
            txtActivateTitle.setText(R.string.label_activated);
            rowInput.setVisibility(android.view.View.GONE);
            boxActivated.setVisibility(android.view.View.VISIBLE);

            // 对齐 PC 端那一行：「剩余 27 天 · 2026-11-25 到期」
            txtActivationInfo.setText("已激活 · " + cfg.licenseSummary());

            StringBuilder sub = new StringBuilder();
            sub.append(cfg.deviceModel());
            if (cfg.deviceMax > 0) {
                sub.append(" · 设备 ").append(Math.max(1, cfg.deviceCount))
                   .append("/").append(cfg.deviceMax);
            }
            txtActivationSub.setText(sub.toString());
        } else {
            txtActivateTitle.setText(R.string.label_code);
            rowInput.setVisibility(android.view.View.VISIBLE);
            boxActivated.setVisibility(android.view.View.GONE);
        }
    }

    /**
     * 状态卡的副标题。这里必须只有「一种说法」——
     * 之前出过「显示未激活，但点开始借网又能用」的矛盾，根因就是
     * 状态文字看 token、借网判定看别的东西。现在两边都读同一个 cfg.licState。
     */
    private void refreshDetail() {
        refreshActivationCard();
        if (!cfg.hasToken()) {
            txtDetail.setText(cfg.licState == FerryConfig.LIC_OFFLINE
                    ? "连不上门户（还没激活）" : "尚未激活");
            return;
        }
        String left = cfg.leftText();
        String dev = "";
        if (cfg.deviceMax > 0 && cfg.deviceCount > 0) {
            dev = " · 设备 " + cfg.deviceCount + "/" + cfg.deviceMax;
        }
        switch (cfg.licState) {
            case FerryConfig.LIC_CHECKING:
                txtDetail.setText("出口 " + cfg.remoteHost + ":" + cfg.remotePort
                        + " · " + left + " · 正在核对授权…");
                break;
            case FerryConfig.LIC_OFFLINE:
                txtDetail.setText("出口 " + cfg.remoteHost + ":" + cfg.remotePort
                        + " · " + left + " · 暂时连不上门户" + dev);
                break;
            case FerryConfig.LIC_EXPIRED:
            case FerryConfig.LIC_INVALID:
            case FerryConfig.LIC_SUSPENDED:
            case FerryConfig.LIC_DEVICE_LIMIT:
                txtDetail.setText(cfg.licMessage.isEmpty() ? "授权不可用" : cfg.licMessage);
                break;
            default:
                txtDetail.setText("出口 " + cfg.remoteHost + ":" + cfg.remotePort
                        + " · " + left + dev);
                break;
        }
    }

    private void log(String s) {
        String ts = new SimpleDateFormat("HH:mm:ss", Locale.US).format(new Date());
        // 同时进 logcat：用户报「连不上」时，让他用 `adb logcat -s FerryUi` 抓一段
        // 比让他复述界面文字准确得多。自检结果也走这条路。
        Log.i("FerryUi", s);
        logBuf.append(ts).append("  ").append(s).append('\n');
        // 只留最后 4000 字，防止长了以后界面卡
        if (logBuf.length() > 4000) {
            logBuf.delete(0, logBuf.length() - 4000);
        }
        txtLog.setText(logBuf.toString());
    }
}
