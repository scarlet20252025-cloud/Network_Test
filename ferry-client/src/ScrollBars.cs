using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;

namespace Ferry
{
    /// <summary>
    /// 全局滚动条样式：细、无箭头、圆角悬浮滑块，轨道透明。
    ///
    /// 为什么必须换掉默认的：WPF 默认滚动条是 Aero 的浅色凹槽 + 上下箭头按钮，
    /// 深色主题里像贴了两条系统滚动条上去，和整套界面对不上。
    ///
    /// 做法是给 <see cref="ScrollBar"/> 装一个隐式样式（Application.Resources），
    /// 这样 TextBox 内部的 ScrollViewer、页面里的 ScrollViewer 都会自动套用。
    ///
    /// 用 XAML 字符串而不是 FrameworkElementFactory：Track.Thumb 是元素型依赖属性，
    /// 用工厂类赋不进去，只有 XAML 解析器会正确处理。
    /// </summary>
    internal static class ScrollBars
    {
        static bool _installed;
        static Style _style;

        /// <summary>
        /// 滚动条样式本体。除了装进 Application.Resources，还可以挂到某个控件的
        /// Resources 上 —— ScrollViewer / TextBox 模板内部的 ScrollBar 一定能找到，
        /// 比只依赖应用级隐式样式可靠（实测应用级那层有时不生效）。
        /// </summary>
        public static Style BarStyle
        {
            get
            {
                if (_style == null)
                {
                    try { _style = (Style)XamlReader.Parse(StyleXaml.Trim()); }
                    catch (System.Exception e)
                    {
                        _style = null;
                        try
                        {
                            System.IO.File.WriteAllText(
                                System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                    "ferry-scrollbar-xaml.txt"), e.ToString());
                        }
                        catch (System.Exception) { }
                    }
                }
                return _style;
            }
        }

        /// <summary>
        /// 把样式应用到某个控件（含它模板内部的滚动条）。
        ///
        /// 只往 Resources 里塞是不够的 —— 实测 ScrollViewer / TextBox 模板内部
        /// 创建的 ScrollBar 拿不到这层隐式样式，界面里还是系统默认的带箭头滚动条。
        /// 所以这里两手都做：资源照塞，另外在 Loaded 后直接遍历视觉树逐个赋 Style。
        /// </summary>
        public static void ApplyTo(FrameworkElement owner)
        {
            if (owner == null) return;
            var st = BarStyle;
            if (st == null) return;

            owner.Resources[typeof(ScrollBar)] = st;
            owner.Loaded += delegate { ForceApply(owner); };
        }

        /// <summary>遍历视觉树，把样式直接赋给每一个 ScrollBar。</summary>
        public static void ForceApply(DependencyObject root)
        {
            if (root == null) return;
            var st = BarStyle;
            if (st == null) return;

            var bar = root as ScrollBar;
            if (bar != null && !ReferenceEquals(bar.Style, st)) bar.Style = st;

            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
                ForceApply(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
        }

        public static void EnsureInstalled()
        {
            if (_installed) return;
            var app = Application.Current;
            if (app == null) return;
            _installed = true;

            try
            {
                var style = BarStyle;
                if (style == null) { _installed = false; return; }
                app.Resources[typeof(ScrollBar)] = style;
            }
            catch (System.Exception e)
            {
                // 样式装不上就退回系统默认 —— 不能让滚动条问题把界面拖崩。
                // 但要把原因落盘：静默失败过一次，结果以为「样式没生效」查了半天。
                _installed = false;
                try
                {
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ferry-scrollbar-xaml.txt"),
                        e.ToString());
                }
                catch (System.Exception) { }
            }
        }


        const string ThumbXaml =
            @"<Thumb Cursor='Hand' Background='#55FFFFFF'>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'>
                    <Border Background='{TemplateBinding Background}' CornerRadius='3'/>
                  </ControlTemplate>
                </Thumb.Template>
                <Thumb.Style>
                  <Style TargetType='Thumb'>
                    <Style.Triggers>
                      <Trigger Property='IsMouseOver' Value='True'>
                        <Setter Property='Background' Value='#7AFFFFFF'/>
                      </Trigger>
                      <Trigger Property='IsDragging' Value='True'>
                        <Setter Property='Background' Value='#A8FFFFFF'/>
                      </Trigger>
                    </Style.Triggers>
                  </Style>
                </Thumb.Style>
              </Thumb>";

        static readonly string StyleXaml = @"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='ScrollBar'>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='Width' Value='10'/>
  <Setter Property='MinWidth' Value='10'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Grid Background='Transparent'>
          <Track x:Name='PART_Track' IsDirectionReversed='True' Margin='2'>
            <Track.Thumb>" + ThumbXaml + @"</Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
    }
}
