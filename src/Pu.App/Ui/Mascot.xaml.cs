using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pu.App.Ui;

public enum MascotFace { Idle, Busy, Ready, Error, Empty }

/// <summary>噗噗吉祥物（路径由 tools/mascot/build.py 生成进 Mascot.xaml，与网页 mascot.svg 同源）。
/// Face 切换表情；每个表情只有一个小动作，与网页 pu.css 的动画一一对应：
/// 等待眨眼、转码冒气+身体一鼓一鼓、就绪闪光、出错淌汗、空文件夹问号晃。
/// 系统关了「显示动画」（ClientAreaAnimation）或 Animate=false（标题栏小图标）时全部静止。</summary>
public sealed partial class Mascot : UserControl
{
    public static readonly DependencyProperty FaceProperty = DependencyProperty.Register(
        nameof(Face), typeof(MascotFace), typeof(Mascot),
        new PropertyMetadata(MascotFace.Idle, (d, _) => ((Mascot)d).Apply()));

    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate), typeof(bool), typeof(Mascot),
        new PropertyMetadata(true, (d, _) => ((Mascot)d).Apply()));

    public MascotFace Face
    {
        get => (MascotFace)GetValue(FaceProperty);
        set => SetValue(FaceProperty, value);
    }

    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    // 正在跑的动画（换表情时逐个停掉，属性回到基础值）
    private readonly List<(Animatable Target, DependencyProperty Property)> _running = [];

    public Mascot()
    {
        InitializeComponent();
        Loaded += (_, _) => Apply();
        Unloaded += (_, _) => StopAll();
    }

    private void Apply()
    {
        FaceIdle.Visibility = Face == MascotFace.Idle ? Visibility.Visible : Visibility.Collapsed;
        FaceBusy.Visibility = Face == MascotFace.Busy ? Visibility.Visible : Visibility.Collapsed;
        FaceReady.Visibility = Face == MascotFace.Ready ? Visibility.Visible : Visibility.Collapsed;
        FaceError.Visibility = Face == MascotFace.Error ? Visibility.Visible : Visibility.Collapsed;
        FaceEmpty.Visibility = Face == MascotFace.Empty ? Visibility.Visible : Visibility.Collapsed;

        StopAll();
        if (!Animate || !IsLoaded || !SystemParameters.ClientAreaAnimation) return;
        switch (Face)
        {
            case MascotFace.Idle:
                // 每 4.2 秒眨一次：绝大部分时间睁着，快速闭合再睁开
                Run(Scale(IdleEyes), ScaleTransform.ScaleYProperty, Blink());
                break;
            case MascotFace.Busy:
                Run(Squash, ScaleTransform.ScaleXProperty, Pulse(1, 1.035, 0.55));
                Run(Squash, ScaleTransform.ScaleYProperty, Pulse(1, 0.965, 0.55));
                Run(Translate(BusySteam), TranslateTransform.YProperty, Pulse(0, -3, 0.55));
                break;
            case MascotFace.Ready:
                var delay = 0.0;
                foreach (var spark in new[] { Spark1, Spark2, Spark3 })
                {
                    Run(Scale(spark), ScaleTransform.ScaleXProperty, Pulse(1, 0.55, 0.9, delay));
                    Run(Scale(spark), ScaleTransform.ScaleYProperty, Pulse(1, 0.55, 0.9, delay));
                    Run(Rotate(spark), RotateTransform.AngleProperty, Pulse(0, 20, 0.9, delay));
                    delay += 0.45;
                }
                break;
            case MascotFace.Error:
                // 汗珠往下淌：出现 → 下滑 → 淡出，循环
                Run(Translate(Sweat), TranslateTransform.YProperty,
                    new DoubleAnimation(-2, 7, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever });
                Sweat.BeginAnimation(OpacityProperty, SweatFade());
                break;
            case MascotFace.Empty:
                Run(Rotate(Question), RotateTransform.AngleProperty, Pulse(0, -8, 1.0));
                Run(Translate(Question), TranslateTransform.YProperty, Pulse(0, -2, 1.0));
                break;
        }
    }

    private void Run(Animatable target, DependencyProperty property, AnimationTimeline animation)
    {
        target.BeginAnimation(property, animation);
        _running.Add((target, property));
    }

    private void StopAll()
    {
        foreach (var (target, property) in _running) target.BeginAnimation(property, null);
        _running.Clear();
        Sweat.BeginAnimation(OpacityProperty, null);
    }

    private static DoubleAnimation Pulse(double from, double to, double halfSeconds, double delay = 0) => new(from, to, TimeSpan.FromSeconds(halfSeconds))
    {
        AutoReverse = true,
        RepeatBehavior = RepeatBehavior.Forever,
        BeginTime = TimeSpan.FromSeconds(delay),
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
    };

    private static DoubleAnimationUsingKeyFrames Blink()
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(4.2), RepeatBehavior = RepeatBehavior.Forever };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.95))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0.12, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.05))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.2))));
        return a;
    }

    private static DoubleAnimationUsingKeyFrames SweatFade()
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.4), RepeatBehavior = RepeatBehavior.Forever };
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.6))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.4))));
        return a;
    }

    // build.py 给每个带动画的组生成 TransformGroup[Scale, Rotate, Translate]，中心点已写好
    private static TransformGroup Group(UIElement e) => (TransformGroup)e.RenderTransform;
    private static ScaleTransform Scale(UIElement e) => (ScaleTransform)Group(e).Children[0];
    private static RotateTransform Rotate(UIElement e) => (RotateTransform)Group(e).Children[1];
    private static TranslateTransform Translate(UIElement e) => (TranslateTransform)Group(e).Children[2];
}
