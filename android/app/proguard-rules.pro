# JNI 方法名不能被混淆/删掉：
# hev-jni.c 在 JNI_OnLoad 里用 FindClass + RegisterNatives 按字面名字绑定。
# 这个类一旦被重命名或裁剪，.so 加载后找不到宿主类，直接崩。
-keep class hev.htproxy.TProxyService { *; }
-keepclasseswithmembernames class * {
    native <methods>;
}
