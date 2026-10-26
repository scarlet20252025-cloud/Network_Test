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

        public static Brush BgB      { get { return Freeze(new SolidColorBrush(Bg)); } }
        public static Brush CardB    { get { return Freeze(new SolidColorBrush(Card)); } }
        public static Brush Card2B   { get { return Freeze(new SolidColorBrush(Card2)); } }
        public static Brush LineB    { get { return Freeze(new SolidColorBrush(Line)); } }
        public static Brush Line2B   { get { return Freeze(new SolidColorBrush(Line2)); } }
        public static Brush TextB    { get { return Freeze(new SolidColorBrush(Text)); } }
        public static Brush MutedB   { get { return Freeze(new SolidColorBrush(Muted)); } }
        public static Brush AccentB  { get { return Freeze(new SolidColorBrush(Accent)); } }
        public static Brush GoodB    { get { return Freeze(new SolidColorBrush(Good)); } }
        public static Brush WarnB    { get { return Freeze(new SolidColorBrush(Warn)); } }
        public static Brush BadB     { get { return Freeze(new SolidColorBrush(Bad)); } }
        public static Brush IdleB    { get { return Freeze(new SolidColorBrush(Idle)); } }

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
