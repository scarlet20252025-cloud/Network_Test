using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ferry
{
    /// <summary>
    /// WPF 没有 NumericUpDown —— 用 TextBox 限制输入凑一个。
    /// 比引 WinForms 的 NumericUpDown 干净（那个在 WPF 里要放 WindowsFormsHost，
    /// DPI 缩放又是一套麻烦）。
    /// </summary>
    internal class NumericUpDownAdapter
    {
        public TextBox Box { get; private set; }
        readonly int _min, _max;

        public NumericUpDownAdapter(int value, int min, int max, int step, double width)
        {
            _min = min;
            _max = max;

            Box = new TextBox
            {
                Text = value.ToString(),
                Width = width,
                FontFamily = new FontFamily(Palette.FontMono),
                FontSize = Palette.FsBody,
                Padding = new Thickness(8, 5, 8, 5),
                BorderBrush = Palette.Line2B,
                BorderThickness = new Thickness(1),
                Background = Palette.Card2B,
                Foreground = Palette.TextB,
                CaretBrush = Palette.TextB,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            Box.PreviewTextInput += delegate (object s, System.Windows.Input.TextCompositionEventArgs e)
            {
                int dummy;
                e.Handled = !int.TryParse(e.Text, out dummy);
            };
            Box.LostFocus += delegate { Normalize(); };
        }

        public int Value
        {
            get
            {
                int v;
                if (!int.TryParse(Box.Text, out v)) v = _min;
                if (v < _min) v = _min;
                if (v > _max) v = _max;
                return v;
            }
            set { Box.Text = Math.Max(_min, Math.Min(_max, value)).ToString(); }
        }

        void Normalize() { Value = Value; }
    }
}
