package com.ferry.app;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.ProgressDialog;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.widget.Toast;

import androidx.core.content.FileProvider;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.security.MessageDigest;

/**
 * Android 端「检查更新 → 下载 → 校验 → 安装」。
 *
 * 为什么 Android 端要单独做一套（不能照抄 Windows 的自动更新）：
 *   · Windows 那套是 exe 自己下载自己、改名、再启动新 exe —— 依赖
 *     「进程可以替换自己」。Android 上是 **APK 必须交给系统安装器**，
 *     App 自己无权覆盖自己。
 *   · Android 安装 APK 需要 `REQUEST_INSTALL_PACKAGES` 权限，且首次会
 *     弹「是否允许来自此来源的安装」—— 这是系统行为，绕不过去，
 *     所以流程必须是「用户点确认 → 系统安装器接管」。
 *   · Android 8.0 起 file:// 被 StrictMode 禁止，必须用 FileProvider
 *     给系统安装器一个 content:// URI，否则报 FileUriExposedException。
 *
 * 设计取舍：
 *   · **不后台静默下载**。用户点「检查更新」才下载，下载过程有进度条。
 *     流量是用户的，偷偷下 6 MB 不合适。
 *   · **校验 sha256**。手机弱网下下载截断很常见，装一个不完整的 APK
 *     会失败并留下垃圾文件。服务端给了 sha256 就一定要校。
 *   · 失败全部用 Toast/日志，不弹阻塞对话框 —— 更新是锦上添花，
 *     不能因为它让人用不了 App。
 */
public class Updater {

    private static final String TAG = "FerryUpdate";

    /** 下载目录：getExternalFilesDir 不需要存储权限，且系统安装器能读。 */
    private static File apkDir(Context ctx) {
        File d = ctx.getExternalFilesDir("update");
        if (d == null) d = new File(ctx.getFilesDir(), "update");
        if (!d.exists()) d.mkdirs();
        return d;
    }

    /**
     * 检查更新。有新版就弹对话框问用户要不要更新。
     *
     * @param silent true = 静默检查（启动时自动跑），没有新版不打扰用户；
     *               false = 用户主动点了「检查更新」，没新版也要给个反馈
     */
    public static void checkAndPrompt(final Activity act, final FerryConfig cfg,
                                      final boolean silent) {
        final Handler ui = new Handler(Looper.getMainLooper());
        new Thread(() -> {
            final FerryApi.Result r = FerryApi.checkUpdate(cfg.portalUrl, 8000);
            final int myCode = versionCode(act);
            final String myName = versionName(act);
            ui.post(() -> {
                if (r.networkError) {
                    if (!silent) toast(act, "连不上门户，检查更新失败");
                    Log.d(TAG, "check failed: " + r.error);
                    return;
                }
                if (!r.ok || r.latestCode <= 0) {
                    if (!silent) toast(act, "服务端还没有可用的安装包");
                    return;
                }
                if (r.latestCode <= myCode) {
                    if (!silent) {
                        toast(act, "已经是最新版本（" + myName + "）");
                    }
                    return;
                }
                // 有新版本
                String sizeTxt = r.apkSize > 0
                        ? String.format("（%.1f MB）", r.apkSize / 1048576.0) : "";
                new AlertDialog.Builder(act)
                        .setTitle("发现新版本 " + r.latest)
                        .setMessage("当前版本：" + myName + "\n"
                                + "最新版本：" + r.latest + sizeTxt + "\n\n"
                                + "更新会下载安装包并交给系统安装器。"
                                + "下载期间可以继续用 App。")
                        .setPositiveButton("立即更新", (d, w) -> downloadAndInstall(act, cfg, r))
                        .setNegativeButton("以后再说", null)
                        .show();
            });
        }, "ferry-update-check").start();
    }

    /** 下载 APK（带进度）→ 校验 sha256 → 交给系统安装器。 */
    private static void downloadAndInstall(final Activity act, final FerryConfig cfg,
                                           final FerryApi.Result info) {
        final ProgressDialog pd = new ProgressDialog(act);
        pd.setTitle("正在下载更新");
        pd.setMessage("0%");
        pd.setProgressStyle(ProgressDialog.STYLE_HORIZONTAL);
        pd.setCancelable(true);
        pd.setMax(100);
        pd.show();

        new Thread(() -> {
            final Handler ui = new Handler(Looper.getMainLooper());
            File out = new File(apkDir(act), "ferry-" + info.latest + ".apk");
            HttpURLConnection c = null;
            InputStream in = null;
            FileOutputStream fos = null;
            String err = null;
            try {
                String url = info.downloadUrl.startsWith("http")
                        ? info.downloadUrl
                        : trimSlash(cfg.portalUrl) + info.downloadUrl;
                c = (HttpURLConnection) new URL(url).openConnection();
                c.setConnectTimeout(10000);
                c.setReadTimeout(30000);
                c.setInstanceFollowRedirects(true);
                int code = c.getResponseCode();
                if (code != 200) throw new IllegalStateException("HTTP " + code);
                long total = c.getContentLength() > 0 ? c.getContentLength() : info.apkSize;
                in = c.getInputStream();
                fos = new FileOutputStream(out);
                byte[] buf = new byte[16384];
                long got = 0;
                int n;
                int lastPct = -1;
                while ((n = in.read(buf)) > 0) {
                    fos.write(buf, 0, n);
                    got += n;
                    if (total > 0) {
                        final int pct = (int) (got * 100 / total);
                        if (pct != lastPct) {
                            lastPct = pct;
                            ui.post(() -> pd.setMessage(pct + "%"));
                        }
                    }
                }
                fos.flush();
                fos.close();
                fos = null;
                in.close();
                in = null;

                // 校验：大小 + sha256
                if (info.apkSize > 0 && out.length() != info.apkSize) {
                    throw new IllegalStateException("下载不完整（" + out.length()
                            + " / " + info.apkSize + " 字节）");
                }
                if (info.apkSha256 != null && info.apkSha256.length() == 64) {
                    String actual = sha256(out);
                    if (!actual.equalsIgnoreCase(info.apkSha256)) {
                        throw new IllegalStateException("安装包校验失败（sha256 不匹配）");
                    }
                }
            } catch (Exception e) {
                err = e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage();
                Log.w(TAG, "download failed", e);
            } finally {
                try { if (fos != null) fos.close(); } catch (Exception ignored) {}
                try { if (in != null) in.close(); } catch (Exception ignored) {}
                if (c != null) c.disconnect();
            }

            final String ferr = err;
            final File fout = out;
            ui.post(() -> {
                try { pd.dismiss(); } catch (Exception ignored) {}
                if (ferr != null) {
                    try { fout.delete(); } catch (Exception ignored) {}
                    new AlertDialog.Builder(act)
                            .setTitle("更新下载失败")
                            .setMessage(ferr + "\n\n可以稍后再试，或让管理员把安装包发给你手动安装。")
                            .setPositiveButton("知道了", null)
                            .show();
                    return;
                }
                installApk(act, fout);
            });
        }, "ferry-update-download").start();
    }

    /** 把 APK 交给系统安装器。 */
    private static void installApk(Activity act, File apk) {
        try {
            Intent i = new Intent(Intent.ACTION_VIEW);
            Uri uri;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                // Android 7+ 必须用 content:// —— file:// 会抛 FileUriExposedException。
                // authority 用 <包名>.fileprovider，见 AndroidManifest 的 provider 声明
                // 与 res/xml/file_paths.xml。
                uri = FileProvider.getUriForFile(act, act.getPackageName() + ".fileprovider", apk);
            } else {
                uri = Uri.fromFile(apk);
            }
            i.setDataAndType(uri, "application/vnd.android.package-archive");
            i.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_ACTIVITY_NEW_TASK);
            act.startActivity(i);
        } catch (Exception e) {
            Log.w(TAG, "install failed", e);
            new AlertDialog.Builder(act)
                    .setTitle("无法启动安装")
                    .setMessage("安装包已下载到：\n" + apk.getAbsolutePath()
                            + "\n\n原因：" + e.getMessage()
                            + "\n\n可以在文件管理器里手动打开它，或检查是否允许「安装未知应用」。")
                    .setPositiveButton("知道了", null)
                    .show();
        }
    }

    // ── 工具 ──

    private static String trimSlash(String s) {
        if (s == null) return "";
        while (s.endsWith("/")) s = s.substring(0, s.length() - 1);
        return s;
    }

    private static void toast(Context ctx, String msg) {
        try { Toast.makeText(ctx, msg, Toast.LENGTH_SHORT).show(); } catch (Exception ignored) {}
    }

    /** 当前 versionCode。取不到返回 0（那会导致"永远有新版本"，比不更新更糟 —— 但取不到基本不可能）。 */
    public static int versionCode(Context ctx) {
        try {
            return ctx.getPackageManager().getPackageInfo(ctx.getPackageName(), 0).versionCode;
        } catch (Exception e) {
            return 0;
        }
    }

    public static String versionName(Context ctx) {
        try {
            return ctx.getPackageManager().getPackageInfo(ctx.getPackageName(), 0).versionName;
        } catch (Exception e) {
            return "";
        }
    }

    private static String sha256(File f) throws Exception {
        MessageDigest md = MessageDigest.getInstance("SHA-256");
        InputStream is = new java.io.FileInputStream(f);
        try {
            byte[] buf = new byte[65536];
            int n;
            while ((n = is.read(buf)) > 0) md.update(buf, 0, n);
        } finally {
            is.close();
        }
        byte[] d = md.digest();
        StringBuilder sb = new StringBuilder(d.length * 2);
        for (byte b : d) sb.append(String.format("%02x", b));
        return sb.toString();
    }
}
