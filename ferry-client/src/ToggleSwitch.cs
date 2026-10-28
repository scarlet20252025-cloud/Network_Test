using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Ferry
{
    /// <summary>
    /// 两段式拨动开关（Windows 11「设置」里那种）。
    ///
    /// 为什么自己画：WPF 没有内置 ToggleSwitch，只有 CheckBox。CheckBox 的勾选框
    /// 是「打勾」不是「拨动」，和系统设置页的观感差很远 —— 用户明确要求「两段式
    /// 那种拨动效果（Windows 也是这种）」。
    ///
    /// 结构：一个圆角轨道（Border）+ 一个圆形滑块（Ellipse），滑块靠左=关、靠右=开。
    /// 点击整个控件切换。滑块位置用 ThicknessAnimation 走 120ms，只有**用户点击**
    /// 才播动画；程序化设置（初始化 / 状态回填）直接落位，避免离屏渲染截到中间态。
    ///
    /// 尺寸：轨道 44x24，滑块直径 18，内边距 3。这是 Win11 视觉上的比例，
    /// 在 100%/125%/150% DPI 下都是 DIP，不用特殊处理。
    /// </summary>
    class ToggleSwitch : Grid
    {
        const double TrackW = 44;
        const double TrackH = 24;
        const double KnobD = 18;
        const double Pad = 3;

        readonly Border _track;
        readonly Ellipse _knob;

        bool _isOn;
        bool _suppress;

        /// <summary>用户拨动开关时触发（程序化设置 IsOn 不触发）。</summary>
        public event EventHandler Toggled;

        public ToggleSwitch()
        {
            Width = TrackW;
            Height = TrackH;
            Background = Brushes.Transparent;      // 让整个矩形区域都能点
            Cursor = Cursors.Hand;
            VerticalAlignment = VerticalAlignment.Center;

            _track = new Border
            {
                Width = TrackW,
                Height = TrackH,
                CornerRadius = new CornerRadius(TrackH / 2),
                Background = Palette.Line2B
            };
            Children.Add(_track);

            _knob = new Ellipse
            {
                Width = KnobD,
                Height = KnobD,
                Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(Pad, 0, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 3,
                    ShadowDepth = 0.6,
                    Opacity = 0.28
                }
            };
            Children.Add(_knob);

            MouseLeftButtonUp += delegate { Toggle(); };
            ApplyVisual(false);
        }

        public bool IsOn
        {
            get { return _isOn; }
            set
            {
                if (_isOn == value) return;
                _isOn = value;
                ApplyVisual(true);
            }
        }

        /// <summary>程序化设置（不触发 Toggled），用于把运行时状态回填到界面。</summary>
        public void SetSilently(bool on)
        {
            _suppress = true;
            try
            {
                if (_isOn != on) { _isOn = on; ApplyVisual(false); }
            }
            finally { _suppress = false; }
        }

        void Toggle()
        {
            _isOn = !_isOn;
            ApplyVisual(true);
            var h = Toggled;
            if (!_suppress && h != null) h(this, EventArgs.Empty);
        }

        void ApplyVisual(bool animate)
        {
            var targetLeft = _isOn ? TrackW - KnobD - Pad : Pad;
            _track.Background = _isOn ? Palette.AccentB : Palette.Line2B;

            var to = new Thickness(targetLeft, 0, 0, 0);
            if (animate)
            {
                var a = new ThicknessAnimation(to, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                _knob.BeginAnimation(MarginProperty, a);
            }
            else
            {
                _knob.BeginAnimation(MarginProperty, null);   // 清掉可能残留的动画
                _knob.Margin = to;
            }
        }
    }
}
