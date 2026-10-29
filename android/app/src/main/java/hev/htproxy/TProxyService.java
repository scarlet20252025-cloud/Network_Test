package hev.htproxy;

/**
 * hev-socks5-tunnel 的 JNI 绑定。
 *
 * ⚠ 这个类的**包名和类名都不能改**。
 *
 * 为什么：libhev-socks5-tunnel.so 在 JNI_OnLoad 里做的是
 *   FindClass("hev/htproxy/TProxyService") + RegisterNatives(...)
 * 名字是编译进 .so 的字面常量（见上游 src/hev-jni.c 的 PKGNAME/CLSNAME 宏）。
 * 改类名 → FindClass 返回 null → RegisterNatives 失败 → 载入 .so 就崩，
 * 而且崩在 native 层，Java 侧只看到一个 UnsatisfiedLinkError，极难定位。
 *
 * 要用别的包名也可以，但必须用 Application.mk 的 PKGNAME/CLSNAME 重新编译 .so，
 * 不能只改 Java。
 */
public final class TProxyService {
    private TProxyService() {
    }

    static {
        System.loadLibrary("hev-socks5-tunnel");
    }

    /** 启动隧道。configPath 是 YAML 配置文件路径，tunFd 是 VpnService 给的文件描述符。 */
    public static native boolean TProxyStartService(String configPath, int tunFd);

    public static native boolean TProxyStopService();

    public static native boolean TProxyIsRunning();

    /** [txPackets, txBytes, rxPackets, rxBytes] */
    public static native long[] TProxyGetStats();
}
