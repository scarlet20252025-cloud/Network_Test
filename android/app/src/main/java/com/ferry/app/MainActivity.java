package com.ferry.app;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.net.VpnService;
import android.os.Build;
import android.os.Bundle;
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
            txtCode.setText(cfg.code);
            log("已激活，剩余 " + cfg.daysLeft() + " 天");
        }
        applyState(FerryVpnService.getLastState(), FerryVpnService.getLastMessage());
        refreshDetail();

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
        String code = txtCode.getText().toString().trim();
        if (TextUtils.isEmpty(code)) {
            log("请填激活码");
            return;
        }
        btnActivate.setEnabled(false);
        log("正在激活…");
        new Thread(() -> {
            FerryApi.Result r = FerryApi.activate(cfg.portalUrl, code, 10000);
            runOnUiThread(() -> {
                btnActivate.setEnabled(true);
                if (r.ok) {
                    cfg.code = code;
                    if (!r.token.isEmpty()) cfg.token = r.token;
                    if (!r.remoteHost.isEmpty()) cfg.remoteHost = r.remoteHost;
                    if (r.remotePort > 0) cfg.remotePort = r.remotePort;
                    if (r.expiresAt > 0) cfg.expiresAt = r.expiresAt;
                    cfg.save(MainActivity.this);
                    log("激活成功，剩余 " + cfg.daysLeft() + " 天");
                    refreshDetail();
                } else {
                    log("激活失败：" + r.error);
                }
            });
        }, "ferry-activate").start();
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

    private void refreshDetail() {
        if (cfg.hasToken()) {
            txtDetail.setText("出口 " + cfg.remoteHost + ":" + cfg.remotePort
                    + " · 剩余 " + cfg.daysLeft() + " 天");
        } else {
            txtDetail.setText("尚未激活");
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
