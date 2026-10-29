using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Ferry
{
    /// <summary>
    /// 配色与样式。从 WinForms 版搬过来，但只保留「颜色 + 数值」，
    /// 不再有 RoundControl / MeasureHeight 这类手工布局辅助 ——
    /// WPF 的 Border / TextBlock 自带圆角和自动尺寸。
    /// </summary>
    internal static class Palette
    {
        public static readonly Color Bg      = Color.FromRgb(242, 245, 246);
        public static readonly Color Card    = Colors.White;
        public static readonly Color Card2   = Color.FromRgb(250, 251, 252);
        public static readonly Color Line    = Color.FromRgb(228, 233, 235);
        public static readonly Color Line2   = Color.FromRgb(214, 221, 226);
        public static readonly Color Text    = Color.FromRgb(44, 62, 80);
        public static readonly Color Muted   = Color.FromRgb(140, 150, 160);
        public static readonly Color Accent  = Color.FromRgb(32, 178, 170);
        public static readonly Color AccentD = Color.FromRgb(22, 150, 143);
        public static readonly Color Good    = Color.FromRgb(32, 178, 170);
        public static readonly Color Warn    = Color.FromRgb(230, 126, 34);
        public static readonly Color Bad     = Color.FromRgb(231, 76, 60);
        public static readonly Color Idle    = Color.FromRgb(149, 165, 166);

        // ⚠ 这些 Brush **必须只创建一次然后复用**，不能写成
        //     public static Brush TextB { get { return Freeze(new SolidColorBrush(Text)); } }
        //
        // 为什么：RefreshUi() 每秒跑一次，里面会给二十来个控件的 Foreground 赋值。
        // 原来的 get 每次 new 一个新的 SolidColorBrush ——
        //   · WPF 的 Foreground 是依赖属性，赋一个**不同的对象**（哪怕颜色一样）
        //     就会判定值变了 → 触发属性失效 → 重新 measure/arrange/重绘整块区域；
        //   · 每秒二十几个新画刷，5 分钟一万多个，全是垃圾。
        // 结果就是：界面在「直连」这种完全空闲的状态下 CPU 稳定 80%+，
        // 点什么都半天没反应（UI 线程被重排占满）。实测 1.0.47 在直连状态下
        // 5 秒消耗 4.09 秒 CPU。
        //
        // 改成静态只读字段后，同一个 Brush 实例反复赋给同一个属性 ——
        // WPF 用引用相等判等，值没变就完全不产生工作。
        public static readonly Brush BgB     = Freeze(new SolidColorBrush(Bg));
        public static readonly Brush CardB   = Freeze(new SolidColorBrush(Card));
        public static readonly Brush Card2B  = Freeze(new SolidColorBrush(Card2));
        public static readonly Brush LineB   = Freeze(new SolidColorBrush(Line));
        public static readonly Brush Line2B  = Freeze(new SolidColorBrush(Line2));
        public static readonly Brush TextB   = Freeze(new SolidColorBrush(Text));
        public static readonly Brush MutedB  = Freeze(new SolidColorBrush(Muted));
        public static readonly Brush AccentB = Freeze(new SolidColorBrush(Accent));
        public static readonly Brush GoodB   = Freeze(new SolidColorBrush(Good));
        public static readonly Brush WarnB   = Freeze(new SolidColorBrush(Warn));
        public static readonly Brush BadB    = Freeze(new SolidColorBrush(Bad));
        public static readonly Brush IdleB   = Freeze(new SolidColorBrush(Idle));

        static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        /// <summary>
        /// 字号。WPF 用 DIP（96 DPI 下的逻辑像素），
        /// 系统缩放由 WPF 自己按 DPI 换算 —— 不需要我们做任何事。
        /// </summary>
        public const double FsTitle = 17;
        public const double FsBig   = 26;
        public const double FsBody  = 13;
        public const double FsSmall = 11;
        public const double FsMono  = 11;

        public const string FontUI   = "Microsoft YaHei UI, Microsoft YaHei, Segoe UI";
        public const string FontMono = "Consolas, Cascadia Mono, Courier New";
    }
}
