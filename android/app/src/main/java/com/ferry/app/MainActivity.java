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
    private MaterialButton btnToggle, btnActivate, btnSelfCheck;

    private FerryConfig cfg;

    /** 授权状态兜底轮询用的 Handler（见 scheduleLicenseWatch）。 */
    private Handler licWatch;
    private final StringBuilder logBuf = new StringBuilder();

    private final BroadcastReceiver stateReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            String state = intent.getStringExtra(FerryVpnService.EXTRA_STATE);
            String msg = intent.getStringExtra(FerryVpnService.EXTRA_MESSAGE);
            applyState(state, msg);
            if (msg != null && !msg.isEmpty()) log(msg);
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

        cfg = FerryConfig.load(this);

        btnToggle.setOnClickListener(v -> onToggle());
        btnActivate.setOnClickListener(v -> onActivate());
        btnSelfCheck.setOnClickListener(v -> runSelfCheck());

        if (cfg.hasToken()) {
            // 先用本地缓存渲染，别等网络 —— 否则联网慢的时候会闪一下「未激活」
            txtCode.setText(cfg.code);
            cfg.licState = FerryConfig.LIC_CHECKING;
            log("已激活，" + cfg.leftText());
        }
        applyState(FerryVpnService.getLastState(), FerryVpnService.getLastMessage());
        refreshDetail();

        // 授权状态监控：启动即核对一次，之后跟着借网启停再核对，
        // 另外每 5 分钟兜底一次（用户挂着不关的时候，过期了要能发现）。
        syncLicense();
        scheduleLicenseWatch();

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
                } else {
                    syncLicense();
                }
                licWatch.postDelayed(this, 5 * 60 * 1000L);
            }
        }, 5 * 60 * 1000L);
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
        SelfCheck.run((items, summary) -> runOnUiThread(() -> {
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
                btnToggle.setText(R.string.btn_stop);
                break;
            case FerryVpnService.STATE_CONNECTING:
                txtState.setText(R.string.state_connecting);
                btnToggle.setText(R.string.btn_stop);
                break;
            case FerryVpnService.STATE_ERROR:
                txtState.setText(R.string.state_error);
                btnToggle.setText(R.string.btn_start);
                break;
            default:
                txtState.setText(R.string.state_off);
                btnToggle.setText(R.string.btn_start);
                break;
        }
        if (msg != null && !msg.isEmpty()) txtDetail.setText(msg);
    }

    /**
     * 状态卡的副标题。这里必须只有「一种说法」——
     * 之前出过「显示未激活，但点开始借网又能用」的矛盾，根因就是
     * 状态文字看 token、借网判定看别的东西。现在两边都读同一个 cfg.licState。
     */
    private void refreshDetail() {
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
