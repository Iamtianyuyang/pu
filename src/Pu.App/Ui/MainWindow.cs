using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Pu.Core.Common;
using Pu.Core.Serving;
using QRCoder;

namespace Pu.App.Ui;

/// <summary>
/// pu~ 的 WPF 主窗口。服务层只依赖下面的公开方法，所有界面更新都会切回专用 STA 线程。
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private static readonly Brush Accent = FrozenBrush("#2F5BB5");
    private static readonly Brush Success = FrozenBrush("#2E9463");
    private static readonly Brush Muted = FrozenBrush("#66738A");
    private static readonly Brush Danger = FrozenBrush("#C02F47");

    private static readonly Duration ViewFadeDuration = new(TimeSpan.FromMilliseconds(240));
    private static readonly Duration ViewSlideDuration = new(TimeSpan.FromMilliseconds(280));
    private static readonly Duration ProgressDuration = new(TimeSpan.FromMilliseconds(300));

    private readonly ObservableCollection<FolderRow> _folderRows = [];
    private readonly DispatcherTimer _feedbackTimer;
    // 多按钮反馈：每个按钮记住自己的原始文本，超时后逐个还原（原来只记一个，
    // 1.6s 内连点两个按钮时第一个会永久卡在“✓ 已复制”）
    private readonly Dictionary<Button, string> _feedbackLabels = [];
    private string _baseUrl = "http://localhost"; // 兜底（无 provider 时）
    private Func<string>? _baseUrlProvider;       // 每次取用实时解析：Wi-Fi 切换后新链接/二维码跟上新 IP
    private string _currentUrl = "";
    private string _lastQrUrl = ""; // 二维码只随 URL 变化重建（进度刷新不重新编码 PNG）
    private MediaJob? _job;
    private FolderJob? _folder;
    private FolderJob? _renderedFolder; // 已渲染行数据的文件夹：同一文件夹再显示时原地刷新，不重建列表
    private IReadOnlyList<FolderFile>? _renderedFiles; // 渲染时的列表快照引用：列表被 Refresh 后引用不同 → 重建
    private bool _closeRequested;
    private bool _disposeRequested;
    private bool _allowClose;
    private bool _dotPulsing;
    private bool _firstShow = true;

    public event Action? CloseRequested;
    public event Action<int>? FolderFileClicked;

    /// <summary>文件夹行状态查询（按 job token）：转码中/就绪/失败徽标（Program 注入）。</summary>
    public Func<string, JobState?>? JobStateLookup { get; set; }

    private static readonly string[] QueenWords =
    ["全世界最可爱", "无敌漂亮", "闪闪发光", "人见人爱", "笑起来超甜", "元气满满", "聪明伶俐", "宇宙第一美少女"];

    private int _queenIndex;

    public MainWindow()
    {
        InitializeComponent();
        FolderList.ItemsSource = _folderRows;

        _feedbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1600),
        };
        _feedbackTimer.Tick += (_, _) => ResetActionFeedback();

        // 底部夸词循环：动画开关交给系统设置，禁用时直接换字
        var queenTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(2800),
        };
        queenTimer.Tick += (_, _) => CycleQueen();
        queenTimer.Start();

        Closing += OnWindowClosing;
        ShowIdle();
    }

    private void CycleQueen()
    {
        _queenIndex = (_queenIndex + 1) % QueenWords.Length;
        var next = QueenWords[_queenIndex] + "的噗噗大王~";
        if (!SystemParameters.ClientAreaAnimation)
        {
            QueenText.Text = next;
            return;
        }
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fadeOut.Completed += (_, _) =>
        {
            QueenText.Text = next;
            QueenText.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(280)));
        };
        QueenText.BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>在当前 STA 线程启动 WPF 消息循环；窗口由 ShowWindow 显式显示。</summary>
    public void Run()
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("必须先在 UI 线程创建 WPF Application");
        application.Run();
    }

    /// <summary>设置服务地址提供者（延迟到每次取用动态解析：Wi-Fi 切换 / DHCP 重分配后，
    /// 新任务的链接与二维码自动跟上新 IP，不用重启）。</summary>
    public void SetBaseUrl(Func<string> baseUrlProvider)
    {
        if (baseUrlProvider is null) return;
        OnUi(() =>
        {
            _baseUrlProvider = baseUrlProvider;
            if (JobView.Visibility == Visibility.Visible && _job is not null)
                ShowJob(_job);
            else if (FolderView.Visibility == Visibility.Visible && _folder is not null)
                ShowFolder(_folder);
        });
    }

    /// <summary>当前服务地址（实时解析，避免 URL 固定成启动时的旧 IP）。</summary>
    private string CurrentBaseUrl => (_baseUrlProvider?.Invoke() ?? _baseUrl).TrimEnd('/');

    /// <summary>job 进度/状态事件入口：只更新「当前选中 job」自己的事件。
    /// 历史 job 的事件永久挂着（WireJob 不取消订阅），新任务显示后它们不得把窗口抢回去；
    /// 切到文件夹/错误/空闲视图后（_job 已清空），后台任务的事件同样不再刷新窗口。</summary>
    public void SetJob(MediaJob? job)
    {
        OnUi(() =>
        {
            if (job is null)
            {
                if (_folder is not null) ShowFolder(_folder);
                else ShowIdle();
                return;
            }

            // 事件只属于当前选中 job：旧任务（或已切走的任务）的进度更新直接丢弃
            if (!ReferenceEquals(_job, job)) return;
            _job = job;
            ShowJob(job);
        });
    }

    /// <summary>新任务初始显示（分析完成 / 文件夹点开文件）：无条件切换到 job 视图。
    /// 与 SetJob 区分：这是有意的显示动作，不是事件驱动刷新，不受当前选中 job 约束。</summary>
    public void ActivateJob(MediaJob job)
    {
        OnUi(() =>
        {
            _job = job;
            ShowJob(job);
        });
    }

    /// <summary>
    /// 设置文件夹上下文。传入 null 只清理返回列表所需的上下文，不会闪退当前媒体视图。
    /// </summary>
    public void SetFolder(FolderJob? folder)
    {
        OnUi(() =>
        {
            _folder = folder;
            BackToFolderButton.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
            if (folder is null)
            {
                _folderRows.Clear();
                _renderedFolder = null;
                if (FolderView.Visibility == Visibility.Visible) ShowIdle();
                return;
            }

            _job = null;
            ShowFolder(folder);
        });
    }

    public void SetFolderFileError(int index, string message)
    {
        OnUi(() =>
        {
            var row = _folderRows.FirstOrDefault(item => item.Index == index);
            if (row is not null)
            {
                row.StateText = "打开失败";
                row.StateBrush = Danger;
                row.IsEnabled = true;
            }

            FolderFeedbackText.Foreground = Danger;
            FolderFeedbackText.Text = Compact(message, 34);
        });
    }

    public void ShowWindow()
    {
        OnUi(() =>
        {
            if (!IsVisible) base.Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (_firstShow)
            {
                _firstShow = false;
                FadeWindowIn();
            }
            Activate();
            Focus();
        });
    }

    public new void Hide()
    {
        OnUi(() =>
        {
            if (IsVisible) base.Hide();
        });
    }

    /// <summary>探测/决策期间的占位视图：窗口立刻有内容，避免「点了没反应」的卡顿感。</summary>
    public void ShowBusy(string path)
    {
        OnUi(() =>
        {
            _job = null;
            BusyTitleText.Text = Directory.Exists(path)
                ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))
                : Path.GetFileName(path);
            BusyHintText.Text = Directory.Exists(path) ? "正在扫描文件夹…" : "正在分析视频…";
            var animate = BusyView.Visibility != Visibility.Visible;
            BusyView.Visibility = Visibility.Visible;
            IdleView.Visibility = Visibility.Collapsed;
            JobView.Visibility = Visibility.Collapsed;
            FolderView.Visibility = Visibility.Collapsed;
            ErrorView.Visibility = Visibility.Collapsed;
            SetWindowStatus("正在分析", Accent);
            if (animate) AnimateIn(BusyView);
        });
    }

    private void ShowIdle()
    {
        var animate = IdleView.Visibility != Visibility.Visible;
        IdleView.Visibility = Visibility.Visible;
        JobView.Visibility = Visibility.Collapsed;
        FolderView.Visibility = Visibility.Collapsed;
        BusyView.Visibility = Visibility.Collapsed;
        ErrorView.Visibility = Visibility.Collapsed;
        _currentUrl = "";
        SetWindowStatus("等待任务", Muted);
        StopDotPulse();
        if (animate) AnimateIn(IdleView);
    }

    private void ShowJob(MediaJob job)
    {
        var animate = JobView.Visibility != Visibility.Visible;
        IdleView.Visibility = Visibility.Collapsed;
        JobView.Visibility = Visibility.Visible;
        FolderView.Visibility = Visibility.Collapsed;
        BusyView.Visibility = Visibility.Collapsed;
        ErrorView.Visibility = Visibility.Collapsed;
        if (animate) AnimateIn(JobView);

        _currentUrl = $"{CurrentBaseUrl}/s/{job.Token}";
        JobTitleText.Text = string.IsNullOrWhiteSpace(job.Title) ? Path.GetFileName(job.SourcePath) : job.Title;
        JobDescriptionText.Text = string.IsNullOrWhiteSpace(job.SourceDescription)
            ? Path.GetFileName(job.SourcePath)
            : job.SourceDescription;
        // 二维码编码 + 图片解码很贵：只在 URL 变化时做一次，进度刷新不重建
        if (!string.Equals(_currentUrl, _lastQrUrl, StringComparison.Ordinal))
        {
            _lastQrUrl = _currentUrl;
            JobLinkTextBox.Text = _currentUrl;
            JobQrImage.Source = BuildQr(_currentUrl);
        }
        BackToFolderButton.Visibility = _folder is null ? Visibility.Collapsed : Visibility.Visible;

        var percent = Math.Clamp((int)Math.Round(job.Progress * 100), 0, 100);
        AnimateProgress(percent);
        JobProgressText.Text = $"{percent}%";

        switch (job.State)
        {
            case JobState.Transcoding:
                SetWindowStatus("正在准备", Accent);
                JobStateDot.Fill = Accent;
                StartDotPulse();
                JobStateTitleText.Text = "正在准备视频";
                JobStateDetailText.Text = string.IsNullOrWhiteSpace(job.PlanExplanation)
                    ? "完成后会自动进入可播放状态"
                    : job.PlanExplanation;
                JobProgressBar.Visibility = Visibility.Visible;
                JobProgressText.Visibility = Visibility.Visible;
                break;

            case JobState.Serving:
                SetWindowStatus("已就绪", Success);
                JobStateDot.Fill = Success;
                StopDotPulse();
                JobStateTitleText.Text = "可以播放啦";
                JobStateDetailText.Text = "扫一扫，浏览器会直接打开";
                JobProgressBar.Visibility = Visibility.Collapsed;
                JobProgressText.Visibility = Visibility.Collapsed;
                break;

            case JobState.Failed:
                SetWindowStatus("处理失败", Danger);
                JobStateDot.Fill = Danger;
                StopDotPulse();
                JobStateTitleText.Text = "视频处理失败";
                JobStateDetailText.Text = string.IsNullOrWhiteSpace(job.Error) ? "请检查 ffmpeg 和文件格式" : job.Error;
                JobProgressBar.Visibility = Visibility.Collapsed;
                JobProgressText.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void ShowFolder(FolderJob folder)
    {
        var animate = FolderView.Visibility != Visibility.Visible;
        IdleView.Visibility = Visibility.Collapsed;
        JobView.Visibility = Visibility.Collapsed;
        FolderView.Visibility = Visibility.Visible;
        BusyView.Visibility = Visibility.Collapsed;
        ErrorView.Visibility = Visibility.Collapsed;
        StopDotPulse();
        if (animate) AnimateIn(FolderView);

        _currentUrl = $"{CurrentBaseUrl}/f/{folder.Token}";
        FolderTitleText.Text = string.IsNullOrWhiteSpace(folder.Title)
            ? Path.GetFileName(folder.FolderPath.TrimEnd(Path.DirectorySeparatorChar))
            : folder.Title;
        FolderCountText.Text = $"{folder.Files.Count}{(folder.Truncated ? "+" : "")} 个";
        FolderLinkTextBox.Text = _currentUrl;
        // 二维码编码 + 图片解码很贵：只在 URL 变化时做一次（与 ShowJob 的 _lastQrUrl 一致，
        // 同一文件夹重复显示时避免重编码；/s/ 与 /f/ 的 token 随机，两视图 URL 不可能相等）
        if (!string.Equals(_currentUrl, _lastQrUrl, StringComparison.Ordinal))
        {
            _lastQrUrl = _currentUrl;
            FolderQrImage.Source = BuildQr(_currentUrl);
        }
        FolderFeedbackText.Foreground = Muted;
        FolderFeedbackText.Text = folder.Files.Count == 0 ? "没有找到支持的媒体文件" : "选一个想看的文件";

        SetWindowStatus("文件夹", Accent);
        // 同一文件夹再次显示（返回列表）：同一列表快照时原地刷新状态徽标，滚动位置与行对象原样保留。
        // 会话复用后列表可能被 Refresh（新列表引用）：行数/内容都可能变，必须重建——
        // 否则行数变短时 folder.Files[row.Index] 越界崩 UI，行数相同但内容变时显示旧文件名
        if (ReferenceEquals(_renderedFolder, folder)
            && ReferenceEquals(_renderedFiles, folder.Files)
            && _folderRows.Count > 0)
        {
            UpdateFolderRows(folder);
            return;
        }
        _renderedFolder = folder;
        _renderedFiles = folder.Files;
        _folderRows.Clear();
        foreach (var file in folder.Files)
        {
            var (stateText, stateBrush) = RowStateFor(folder, file);
            _folderRows.Add(new FolderRow
            {
                Index = file.Index,
                DisplayIndex = (file.Index + 1).ToString("00"),
                Name = file.Name,
                SizeText = HumanSize.Format(file.SizeBytes),
                StateText = stateText,
                StateBrush = stateBrush,
                IsEnabled = true,
            });
        }
    }

    /// <summary>行状态徽标（打开/转码中/已打开/失败）：重建与原地刷新共用。</summary>
    private (string Text, Brush Brush) RowStateFor(FolderJob folder, FolderFile file)
    {
        var openedToken = folder.OpenedToken(file.Index);
        var jobState = openedToken is { } token && JobStateLookup is { } lookup ? lookup(token) : null;
        return jobState switch
        {
            JobState.Transcoding => ("转码中", Accent),
            JobState.Serving => ("已打开", Success),
            JobState.Failed => ("失败", Danger),
            _ => (openedToken is null ? "打开" : "已打开", openedToken is null ? Muted : Success),
        };
    }

    /// <summary>同一文件夹再次显示：按当前 job 状态原地刷新行徽标，不重建集合（保住滚动位置）。</summary>
    private void UpdateFolderRows(FolderJob folder)
    {
        // 一次快照：Refresh 在服务器线程并发替换列表（同一文件夹被重新右键时），
        // 逐行重读会在列表变短时 row.Index 越界崩 UI 线程；快照后越界的行直接跳过
        var files = folder.Files;
        foreach (var row in _folderRows)
        {
            if (row.Index >= files.Count) break;
            var file = files[row.Index];
            var (stateText, stateBrush) = RowStateFor(folder, file);
            row.StateText = stateText;
            row.StateBrush = stateBrush;
            row.IsEnabled = true;
        }
    }

    /// <summary>处理失败视图（探测/扫描/转码启动失败）：窗口给出错误，应用保持运行等待下一个任务。</summary>
    public void ShowError(string path, string message)
    {
        OnUi(() =>
        {
            _job = null;
            ErrorTitleText.Text = Directory.Exists(path)
                ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))
                : Path.GetFileName(path);
            ErrorMessageText.Text = Compact(message, 120);
            var animate = ErrorView.Visibility != Visibility.Visible;
            ErrorView.Visibility = Visibility.Visible;
            IdleView.Visibility = Visibility.Collapsed;
            JobView.Visibility = Visibility.Collapsed;
            FolderView.Visibility = Visibility.Collapsed;
            BusyView.Visibility = Visibility.Collapsed;
            _currentUrl = "";
            SetWindowStatus("处理失败", Danger);
            StopDotPulse();
            if (animate) AnimateIn(ErrorView);
        });
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlForAction(sender);
        if (string.IsNullOrWhiteSpace(url)) return;

        var button = (Button)sender;
        try
        {
            Clipboard.SetDataObject(url, true);
            ShowActionFeedback(button, "✓ 已复制");
        }
        catch
        {
            ShowActionFeedback(button, "复制失败");
        }
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlForAction(sender);
        if (string.IsNullOrWhiteSpace(url)) return;

        var button = (Button)sender;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            ShowActionFeedback(button, "打开失败");
        }
    }

    private string UrlForAction(object sender)
        => ReferenceEquals(sender, FolderCopyButton) || ReferenceEquals(sender, FolderOpenButton)
            ? FolderLinkTextBox.Text
            : JobLinkTextBox.Text;

    private void FolderItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FolderRow row }) return;

        row.StateText = "正在打开";
        row.StateBrush = Accent;
        row.IsEnabled = false;
        FolderFeedbackText.Foreground = Muted;
        FolderFeedbackText.Text = $"正在打开 {Compact(row.Name, 27)}";

        if (FolderFileClicked is { } handler)
            handler.Invoke(row.Index);
        else
        {
            row.StateText = "无法打开";
            row.StateBrush = Danger;
            row.IsEnabled = true;
        }
    }

    private void BackToFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_folder is null) return;
        _job = null; // 回到文件夹视图：后台 job 的进度事件不再把窗口抢回 job 视图
        ShowFolder(_folder);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => RequestClose();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        RequestClose();
    }

    private void RequestClose()
    {
        if (_closeRequested) return;
        _closeRequested = true;
        CloseButton.IsEnabled = false;
        SetWindowStatus("正在停止", Muted);

        if (CloseRequested is { } handler) handler.Invoke();
        else Dispose();
    }

    private void SetWindowStatus(string text, Brush brush)
    {
        WindowStatusText.Text = text;
        WindowStatusDot.Fill = brush;
    }

    // ── 动效：全部尊重系统「显示窗口动画」设置 ──

    private void FadeWindowIn()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var fade = new DoubleAnimation(0, 1, ViewFadeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        WindowFrame.BeginAnimation(OpacityProperty, fade);
    }

    private void AnimateIn(FrameworkElement view)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var fade = new DoubleAnimation(0, 1, ViewFadeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        view.BeginAnimation(OpacityProperty, fade);
        if (view.RenderTransform is TranslateTransform translate)
        {
            var slide = new DoubleAnimation(10, 0, ViewSlideDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    private void AnimateProgress(int percent)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            JobProgressBar.Value = percent;
            return;
        }
        var animation = new DoubleAnimation(percent, ProgressDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        JobProgressBar.BeginAnimation(ProgressBar.ValueProperty, animation);
    }

    private void StartDotPulse()
    {
        if (_dotPulsing || !SystemParameters.ClientAreaAnimation) return;
        _dotPulsing = true;
        var pulse = new DoubleAnimation(1.0, 0.3, new Duration(TimeSpan.FromMilliseconds(900)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        JobStateDot.BeginAnimation(OpacityProperty, pulse);
    }

    private void StopDotPulse()
    {
        if (!_dotPulsing) return;
        _dotPulsing = false;
        JobStateDot.BeginAnimation(OpacityProperty, null);
        JobStateDot.Opacity = 1;
    }

    private void ShowActionFeedback(Button button, string label)
    {
        ResetActionFeedback();
        _feedbackLabels[button] = button.Content?.ToString() ?? "";
        button.Content = label;
        _feedbackTimer.Start();
    }

    private void ResetActionFeedback()
    {
        _feedbackTimer.Stop();
        foreach (var (button, original) in _feedbackLabels)
            button.Content = original;
        _feedbackLabels.Clear();
    }

    private void OnUi(Action action)
    {
        if (_disposeRequested || Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) action();
        else _ = Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    public void Dispose()
    {
        if (_disposeRequested) return;
        _disposeRequested = true;

        if (Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) FinishDispose();
        else _ = Dispatcher.BeginInvoke(FinishDispose, DispatcherPriority.Send);
    }

    private void FinishDispose()
    {
        _feedbackTimer.Stop();
        _allowClose = true;
        if (IsLoaded) Close();
        Application.Current?.Shutdown();
    }

    private static ImageSource? BuildQr(string url)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            using var qr = new PngByteQRCode(data);
            var bytes = qr.GetGraphic(8);
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string Compact(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "未知错误";
        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

    private static Brush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private sealed class FolderRow : INotifyPropertyChanged
    {
        private string _stateText = "打开";
        private Brush _stateBrush = Muted;
        private bool _isEnabled = true;

        public required int Index { get; init; }
        public required string DisplayIndex { get; init; }
        public required string Name { get; init; }
        public required string SizeText { get; init; }

        public string StateText
        {
            get => _stateText;
            set { if (_stateText != value) { _stateText = value; Notify(); } }
        }

        public Brush StateBrush
        {
            get => _stateBrush;
            set { if (!ReferenceEquals(_stateBrush, value)) { _stateBrush = value; Notify(); } }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (_isEnabled != value) { _isEnabled = value; Notify(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
