using System;
using System.Windows;
using System.Windows.Media;

namespace Ferry
{
    /// <summary>
    /// 配色与尺寸。1.2.0 起整体换成「东风谷早苗」主题：
    ///
    ///   发色 #859980（采样自立绘）→ 主色系：整窗深鼠尾草绿
    ///   瞳色 #7595C4（采样自立绘）→ 次强调：链接、境外、测速
    ///   裙色 #6F7CA8             → 更深一档的蓝，压在蓝色文字上做层次
    ///
    /// 关键决策：**整窗一个底色**，不再用「浅灰底 + 白卡片」。
    /// 白面板压在深色主题上像贴上去的 PPT，和立绘也完全不搭；
    /// 这里改成卡片是「比背景亮一档的同色系面板 + 一圈更亮的描边」，
    /// 层次靠明度拉开，不靠白色。
    ///
    /// ⚠ 画刷一律 static readonly 且 Freeze，绝不能写成 get 属性 ——
    ///   RefreshUi 每秒跑一次，每次 new 画刷会让 WPF 判定依赖属性变化，
    ///   触发全窗口重排。1.0.47 曾因此 CPU 稳定 80%。
    /// </summary>
    internal static class Palette
    {
        // ── 主题面（自下而上三层明度）──
        public static readonly Color Bg      = Color.FromRgb(47, 68, 54);    // #2F4436 窗口底
        public static readonly Color Card    = Color.FromRgb(61, 87, 71);    // #3D5747 面板
        public static readonly Color Card2   = Color.FromRgb(68, 96, 79);    // #44604F 面板内嵌
        public static readonly Color Line    = Color.FromRgb(74, 101, 83);   // #4A6553 面板描边
        public static readonly Color Line2   = Color.FromRgb(88, 116, 95);   // #58745F 更明显描边
        public static readonly Color Text    = Color.FromRgb(237, 243, 234); // #EDF3EA
        public static readonly Color Muted   = Color.FromRgb(169, 188, 173); // #A9BCAD

        // ── 侧栏（比窗口底再暗一档）──
        public static readonly Color SideBg     = Color.FromRgb(40, 59, 47);  // #283B2F
        public static readonly Color SideBg2    = Color.FromRgb(51, 73, 58);  // #33493A 悬停
        public static readonly Color SideActive = Color.FromRgb(62, 90, 72);  // #3E5A48 选中
        public static readonly Color SideText   = Color.FromRgb(232, 240, 228);
        public static readonly Color SideMuted  = Color.FromRgb(159, 179, 164);
        public static readonly Color SideLine   = Color.FromRgb(60, 83, 67);
        public static readonly Color SideDim    = Color.FromRgb(130, 153, 136);

        // ── 语义色（深底上偏亮，保证对比度）──
        public static readonly Color Accent   = Color.FromRgb(143, 190, 149); // #8FBE95
        public static readonly Color AccentD  = Color.FromRgb(110, 154, 120); // #6E9A78
        public static readonly Color OnAccent = Color.FromRgb(28, 45, 33);    // 主按钮上的深色字
        public static readonly Color Blue     = Color.FromRgb(155, 184, 224); // #9BB8E0
        public static readonly Color BlueD    = Color.FromRgb(127, 162, 212); // #7FA2D4
        public static readonly Color Good     = Color.FromRgb(143, 190, 149);
        public static readonly Color Warn     = Color.FromRgb(224, 176, 112); // #E0B070
        public static readonly Color Bad      = Color.FromRgb(224, 138, 130); // #E08A82
        public static readonly Color Idle     = Color.FromRgb(126, 145, 132); // #7E9184

        public static readonly Brush BgB     = Freeze(new SolidColorBrush(Bg));
        public static readonly Brush CardB   = Freeze(new SolidColorBrush(Card));
        public static readonly Brush Card2B  = Freeze(new SolidColorBrush(Card2));
        public static readonly Brush LineB   = Freeze(new SolidColorBrush(Line));
        public static readonly Brush Line2B  = Freeze(new SolidColorBrush(Line2));
        public static readonly Brush TextB   = Freeze(new SolidColorBrush(Text));
        public static readonly Brush MutedB  = Freeze(new SolidColorBrush(Muted));

        public static readonly Brush SideBgB     = Freeze(new SolidColorBrush(SideBg));
        public static readonly Brush SideBg2B    = Freeze(new SolidColorBrush(SideBg2));
        public static readonly Brush SideActiveB = Freeze(new SolidColorBrush(SideActive));
        public static readonly Brush SideTextB   = Freeze(new SolidColorBrush(SideText));
        public static readonly Brush SideMutedB  = Freeze(new SolidColorBrush(SideMuted));
        public static readonly Brush SideLineB   = Freeze(new SolidColorBrush(SideLine));
        public static readonly Brush SideDimB    = Freeze(new SolidColorBrush(SideDim));

        public static readonly Brush AccentB   = Freeze(new SolidColorBrush(Accent));
        public static readonly Brush AccentDB  = Freeze(new SolidColorBrush(AccentD));
        public static readonly Brush OnAccentB = Freeze(new SolidColorBrush(OnAccent));
        public static readonly Brush BlueB     = Freeze(new SolidColorBrush(Blue));
        public static readonly Brush BlueDB    = Freeze(new SolidColorBrush(BlueD));
        public static readonly Brush GoodB     = Freeze(new SolidColorBrush(Good));
        public static readonly Brush WarnB     = Freeze(new SolidColorBrush(Warn));
        public static readonly Brush BadB      = Freeze(new SolidColorBrush(Bad));
        public static readonly Brush IdleB     = Freeze(new SolidColorBrush(Idle));

        static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        // ── 渐变（1.2.1）──
        //
        // 整窗纯色在 1000×660 上会显得很平：一大片同色，卡片再亮也拉不出空间感。
        // 这里叠两层很轻的渐变：
        //   1) 对角线性渐变：左上偏亮、右下压深，给「光从左上进来」的方向；
        //   2) 右上角一团极淡的径向光斑，模拟环境光，不让右上角死黑。
        // 幅度都控制在 6% 以内 —— 再多就会显脏，深色主题尤其明显。
        // 主渐变只走纵向：横向的对角线会被右上角那团光抵消，
        // 结果四角采样出来左上和右上一样亮，看着像没渐变。
        // 纵向明暗 + 两个对角色光，方向才清楚。
        public static readonly Brush ShellBgB = FreezeGrad(new LinearGradientBrush(
            Color.FromRgb(67, 97, 77),      // #43614D 顶部
            Color.FromRgb(34, 53, 40),      // #223528 底部
            new Point(0, 0), new Point(0, 1)));

        public static readonly Brush GlowB = FreezeGrad(new RadialGradientBrush
        {
            Center = new Point(0.74, 0.0),
            GradientOrigin = new Point(0.74, 0.0),
            RadiusX = 0.72,
            RadiusY = 0.62,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(46, 168, 198, 160), 0.0),
                new GradientStop(Color.FromArgb(0, 168, 198, 160), 0.75)
            }
        });

        // 左下角一团瞳蓝冷光。整窗只有绿会有「一块布」的感觉，
        // 掺一点立绘里的蓝（#7FA2D4）立刻有冷暖关系；透明度压到 10% 以下，
        // 再多就会像屏幕漏光。
        public static readonly Brush GlowCoolB = FreezeGrad(new RadialGradientBrush
        {
            // 中心放在内容区左下（0.32 已经越过 208px 的侧栏），
            // 放 0.03 会被侧栏整个盖住，等于没加。
            Center = new Point(0.32, 1.04),
            GradientOrigin = new Point(0.32, 1.04),
            RadiusX = 0.6,
            RadiusY = 0.58,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(36, 127, 162, 212), 0.0),
                new GradientStop(Color.FromArgb(0, 127, 162, 212), 0.8)
            }
        });

        public static readonly Brush SideGradB = FreezeGrad(new LinearGradientBrush(
            Color.FromArgb(222, 36, 54, 43),   // 半透明，让窗口渐变透上来一点
            Color.FromArgb(238, 27, 41, 32),
            new Point(0, 0), new Point(0, 1)));

        public static readonly Brush CardGradB = FreezeGrad(new LinearGradientBrush(
            Color.FromRgb(66, 94, 76),      // #425E4C
            Color.FromRgb(57, 82, 67),      // #395243
            new Point(0, 0), new Point(0, 1)));

        static Brush FreezeGrad(GradientBrush b) { b.Freeze(); return b; }

        /// <summary>
        /// 字号。WPF 用 DIP（96 DPI 下的逻辑像素），系统缩放由 WPF 自己换算。
        /// </summary>
        public const double FsH1    = 21;
        public const double FsTitle = 17;
        public const double FsBig   = 25;
        public const double FsBody  = 13;
        public const double FsSmall = 12;
        public const double FsTiny  = 11;
        public const double FsMono  = 12;

        // ── 版式尺寸：集中在这里，改一处全界面跟着走 ──
        public const double WinW       = 1000;
        public const double WinH       = 660;
        public const double MinW       = 900;
        public const double MinH       = 580;
        public const double SideW      = 208;
        public const double ContentPad = 28;

        public const string FontUI   = "Microsoft YaHei UI, Microsoft YaHei, Segoe UI";
        public const string FontMono = "Consolas, Cascadia Mono, Courier New";
    }
}
