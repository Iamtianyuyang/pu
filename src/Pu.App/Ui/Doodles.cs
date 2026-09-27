using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Path = System.Windows.Shapes.Path;
using Shape = System.Windows.Shapes.Shape;
using System.Windows.Threading;

namespace Pu.App.Ui;

/// <summary>圆珠笔排线进度条（与网页 pu.js 的 hatch 同一画法）：手绘胶囊外框 + 斜排线，按进度裁剪。
/// IsIndeterminate：还没有进度时整条排线淡淡闪（转码刚起步、ffmpeg 还没报进度）。</summary>
public sealed class HatchProgress : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(HatchProgress),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(HatchProgress),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((HatchProgress)d).UpdatePulse()));

    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(
        nameof(Ink), typeof(Brush), typeof(HatchProgress),
        new FrameworkPropertyMetadata(Brushes.Navy, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty HatchOpacityProperty = DependencyProperty.Register(
        "HatchOpacity", typeof(double), typeof(HatchProgress),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0–1。</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsIndeterminate { get => (bool)GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }

    // 外框：400×20 单位里画的一个略歪的胶囊（与网页同一条路径），渲染时按实际尺寸缩放
    private static readonly Geometry Frame = Geometry.Parse(
        "M9 1.6Q200 .3 391 1.9Q399 2.6 398.5 10Q398 18.4 390 18.3Q200 19.7 10 18.1Q1.5 17.9 1.5 10Q1.6 1.9 9 1.6Z");

    public HatchProgress()
    {
        Height = 20;
        Loaded += (_, _) => UpdatePulse();
    }

    private void UpdatePulse()
    {
        if (IsIndeterminate && SystemParameters.ClientAreaAnimation)
            BeginAnimation(HatchOpacityProperty, new DoubleAnimation(0.18, 0.42, TimeSpan.FromSeconds(0.7))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        else
            BeginAnimation(HatchOpacityProperty, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var pen = new Pen(Ink, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();

        var frame = Frame.Clone();
        frame.Transform = new ScaleTransform(w / 400, h / 20);
        frame.Freeze();

        // 排线：每 7px 一根，长短角度按序号略微错开，像手涂的
        var lines = new StreamGeometry();
        using (var g = lines.Open())
        {
            var i = 0;
            for (var x = -6.0; x < w + 6; x += 7, i++)
            {
                var j = i * 37 % 5 * 0.35;
                g.BeginFigure(new Point(x + j, h - 3 - j * .4), false, false);
                g.LineTo(new Point(x + 9 - j, 3 + j * .3), true, false);
            }
        }
        lines.Freeze();

        var indet = IsIndeterminate;
        var fillWidth = indet ? w : w * Math.Clamp(Value, 0, 1);
        if (fillWidth > 0)
        {
            dc.PushClip(frame);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, fillWidth, h)));
            dc.PushOpacity(indet ? (double)GetValue(HatchOpacityProperty) : 1);
            dc.DrawGeometry(null, pen, lines);
            dc.Pop(); dc.Pop(); dc.Pop();
        }
        dc.DrawGeometry(null, pen, frame);
    }
}

/// <summary>噗噗的对话气泡：手绘不规则圆角 + 左下小尾巴（指向左边的噗噗）。
/// Say 传一组台词循环轮换（淡出换字），Hide 收起。</summary>
public sealed class SpeechBubble : Grid
{
    private readonly TextBlock _text;
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<string> _lines = [];
    private int _index;
    private string _key = "";

    public SpeechBubble()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(10, 0, 0, 0);

        _text = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 230 };
        _text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var body = new Border
        {
            // 四个角半径各不相同：手画的圆角没有那么规整
            CornerRadius = new CornerRadius(18, 21, 17, 20),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(12, 6, 13, 7),
            Child = _text,
        };
        body.SetResourceReference(Border.BorderBrushProperty, "InkBrush");
        body.SetResourceReference(Border.BackgroundProperty, "CardBrush");

        // 小尾巴：先用底色盖住气泡边框的一小段，再画两笔描边，看起来是一体的
        var tailFill = new Path
        {
            Data = Geometry.Parse("M12 0 C8 6 4 10 0 12 C6 12 12 10 16 6 Z"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-8, 0, 0, 5),
        };
        tailFill.SetResourceReference(Shape.FillProperty, "CardBrush");
        var tailLine = new Path
        {
            Data = Geometry.Parse("M12 0 C8 6 4 10 0 12 C6 12 12 10 16 6"),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-8, 0, 0, 5),
        };
        tailLine.SetResourceReference(Shape.StrokeProperty, "InkBrush");

        Children.Add(body);
        Children.Add(tailFill);
        Children.Add(tailLine);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Next();
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => { if (_lines.Count > 1 && Visibility == Visibility.Visible) _timer.Start(); };
    }

    /// <summary>说一组台词（同一组重复调用不重置，避免进度刷新把轮换打断）。</summary>
    public void Say(IReadOnlyList<string> lines, double seconds = 3)
    {
        Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var key = string.Join("|", lines);
        if (key == _key) return;
        _key = key;
        _lines = lines;
        _timer.Stop();
        if (lines.Count == 0) return;
        _index = Random.Shared.Next(lines.Count);
        _text.Text = lines[_index];
        _text.BeginAnimation(OpacityProperty, null);
        _text.Opacity = 1;
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        if (lines.Count > 1) _timer.Start();
    }

    public void Hide()
    {
        _timer.Stop();
        _key = "";
        Visibility = Visibility.Collapsed;
    }

    private void Next()
    {
        if (_lines.Count < 2) return;
        _index = (_index + 1) % _lines.Count;
        var next = _lines[_index];
        if (!SystemParameters.ClientAreaAnimation)
        {
            _text.Text = next;
            return;
        }
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fadeOut.Completed += (_, _) =>
        {
            _text.Text = next;
            _text.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
        };
        _text.BeginAnimation(OpacityProperty, fadeOut);
    }
}
