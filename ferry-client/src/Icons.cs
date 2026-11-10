using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ferry
{
    /// <summary>
    /// 界面图标。全部是 24×24 视框的线条几何，运行时按尺寸缩放。
    ///
    /// 为什么不用图片：图标要在深色侧栏 / 浅色内容区 / 选中态三种底色上
    /// 保持同一个形状，只换描边颜色。位图得准备三套，矢量只要改 Stroke。
    ///
    /// 为什么不用字体图标：Fluent / Segoe MDL2 的字形码在不同 Windows 版本
    /// 上会缺字，缺了就是方框，而且没法跟着 DPI 对齐。
    /// </summary>
    internal static class Icons
    {
        // 隧道：两台机器一上一下，中间一条通路（比「服务器」更能说明是转发）
        public const string Tunnel =
            "M4 5.5 H20 V10 H4 Z M4 14 H20 V18.5 H4 Z M8 7.75 H8.01 M8 16.25 H8.01";

        // 日志：文档 + 两条文字线
        public const string Log =
            "M6 3.5 H13.5 L18 8 V20.5 H6 Z M13.5 3.5 V8 H18 M9 12 H15 M9 15.5 H15";

        // 测速：仪表盘 + 指针
        public const string Speed =
            "M4 17.5 A8 8 0 0 1 20 17.5 M12 17.5 L16.2 12.3 M12 17.5 A0.9 0.9 0 1 0 12 17.4";

        // 设置：两条滑杆（比齿轮在小尺寸下清楚得多）
        public const string Settings =
            "M4 8.5 H20 M4 15.5 H20 M9.5 8.5 A2 2 0 1 0 9.6 8.5 M14.5 15.5 A2 2 0 1 0 14.6 15.5";

        // 关于：圆圈 + i
        public const string About =
            "M12 3.2 A8.8 8.8 0 1 0 12.1 3.2 M12 11 V16.5 M12 7.8 A0.75 0.75 0 1 0 12 7.9";

        // 刷新：圆弧 + 箭头
        public const string Refresh =
            "M19.5 12 A7.5 7.5 0 1 1 16.7 6.4 M19.5 4 V8 H15.5";

        // 关闭 / 最小化
        public const string Close  = "M6.5 6.5 L17.5 17.5 M17.5 6.5 L6.5 17.5";
        public const string Minus  = "M6.5 12 H17.5";

        // 清空日志：垃圾桶
        public const string Trash  = "M5 7 H19 M9.5 7 V4.8 H14.5 V7 M7.4 7 L8.4 20 H15.6 L16.6 7";

        // 一键测试：播放三角（填充）
        public const string Play   = "M7.5 4.8 L18.5 12 L7.5 19.2 Z";

        public static Path Make(string data, Brush stroke, double size)
        {
            return new Path
            {
                Data = Geometry.Parse(data),
                Stroke = stroke,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = null,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                SnapsToDevicePixels = true
            };
        }

        /// <summary>实心图标（播放三角用）。</summary>
        public static Path MakeFilled(string data, Brush fill, double size)
        {
            var p = Make(data, fill, size);
            p.Fill = fill;
            p.StrokeThickness = 0;
            return p;
        }
    }
}
