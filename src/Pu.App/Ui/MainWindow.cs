using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Pu.App.Shell;
using Pu.Core.Common;
using Pu.Core.Serving;
using QRCoder;

namespace Pu.App.Ui;

/// <summary>
/// pu~ 的 WPF 主窗口。服务层只依赖下面的公开方法，所有界面更新都会切回专用 STA 线程。
/// 形态：宽 440、高度随内容（SizeToContent），默认停在鼠标所在屏幕的右下角、向上长高；
/// 文件夹模式改为可上下拉伸并记住高度。手机打开链接后切到「送到了」，二维码收起。
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private static readonly Duration ViewFadeDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration ViewSlideDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly Duration ProgressDuration = new(TimeSpan.FromMilliseconds(400));

    private readonly ObservableCollection<FolderRow> _folderRows = [];
    private readonly DispatcherTimer _feedbackTimer;
    private readonly DispatcherTimer _folderRefreshTimer;
    private readonly DispatcherTimer _saveHeightTimer;
    // 多按钮反馈：每个按钮记住自己的原始文本，超时后逐个还原（只记一个的话，
    // 1.6s 内连点两个按钮时第一个会永久卡在“✓ 已复制”）
    private readonly Dictionary<Button, string> _feedbackLabels = [];
    private string _baseUrl = "http://localhost"; // 兜底（无 provider 时）
    private Func<string>? _baseUrlProvider;       // 每次取用实时解析：Wi-Fi 切换后新链接/二维码跟上新 IP
    private string _currentUrl = "";
    private string _jobQrUrl = "";    // 二维码只随 URL 变化重建（进度刷新不重新编码 PNG）
    private string _folderQrUrl = "";
    private MediaJob? _job;
    private FolderJob? _folder;
    private FolderJob? _renderedFolder; // 已渲染行数据的文件夹：同一文件夹再显示时原地刷新，不重建列表
    private IReadOnlyList<FolderFile>? _renderedFiles; // 渲染时的列表快照引用：列表被 Refresh 后引用不同 → 重建
    private string _qrExpandedFor = "";  // 送达后「再给一台设备扫码」展开了哪个 token 的二维码
    private EtaEstimator _eta = new();
    private string _etaFor = "";
    private bool _closeRequested;
    private bool _disposeRequested;
    private bool _allowClose;
    private bool _firstShow = true;
    private bool _anchored = true;  // 停靠右下角；用户拖动过窗口后就不再自作主张挪位置
    private bool _folderMode;       // 文件夹模式：手动高度（可拉伸），其余模式高度随内容

    public event Action? CloseRequested;
    public event Action<int>? FolderFileClicked;

    /// <summary>用户点了防火墙提示里的「放行」（Program 负责提权加规则、复查后回调 SetFirewall）。</summary>
    public event Action? FirewallFixRequested;

    // 防火墙判定（null = 读不到或未检查：不提示）；放行过程中的说明文字；放行进行中（按钮禁用）
    private FwVerdict? _firewall;
    private string? _firewallNote;
    private bool _firewallBusy;

    /// <summary>按 token 查任务（文件夹行显示转码中 42% / 就绪 / 失败；Program 注入）。</summary>
    public Func<string, MediaJob?>? JobLookup { get; set; }

    public MainWindow()
    {
        // 配色字典 + 控件样式装进 Application：必须在 InitializeComponent 之前（XAML 里的 DynamicResource/StaticResource 要能找到）
        if (Application.Current is { } app) ThemeManager.Install(app);
        InitializeComponent();
        FolderList.ItemsSource = _folderRows;
        foreach (var hint in new[] { JobFirewallHint, FolderFirewallHint })
        {
            hint.FixRequested += () => FirewallFixRequested?.Invoke();
            hint.SettingsRequested += FirewallCheck.OpenSettings;
        }

        _feedbackTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1600) };
        _feedbackTimer.Tick += (_, _) => ResetActionFeedback();

        // 文件夹视图开着时定时刷新行状态：手机上点开的集也会在这里显示转码进度 / 就绪
        _folderRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
        _folderRefreshTimer.Tick += (_, _) => { if (_folder is not null && FolderView.Visibility == Visibility.Visible) UpdateFolderRows(_folder); };

        _saveHeightTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        _saveHeightTimer.Tick += (_, _) => { _saveHeightTimer.Stop(); UiState.SaveFolderHeight(ActualHeight); };

        TaskbarItemInfo = new TaskbarItemInfo();
        Closing += OnWindowClosing;
        SizeChanged += OnSizeChanged;
        SourceInitialized += OnSourceInitialized;
        ShowIdle();
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
    /// 历史 job 的事件不得把窗口抢回去；切到文件夹/错误/空闲视图后（_job 已清空），
    /// 后台任务的事件同样不再刷新窗口。</summary>
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

    /// <summary>有局域网设备打开了链接（服务端 ClientArrived）：是当前显示的任务 / 文件夹就刷新成「送到了」。</summary>
    public void OnClientArrived(ClientArrival arrival)
    {
        OnUi(() =>
        {
            if (_job is not null && _job.Token == arrival.Token && JobView.Visibility == Visibility.Visible)
                ShowJob(_job);
            else if (_folder is not null && _folder.Token == arrival.Token && FolderView.Visibility == Visibility.Visible)
                ShowFolder(_folder);
        });
    }

    /// <summary>防火墙检查结果（启动时、每个新任务、放行之后由 Program 调用）。
    /// note：放行过程的说明（「正在等你确认…」「已取消」）；busy：放行进行中，按钮禁用防连点。</summary>
    public void SetFirewall(FwVerdict? verdict, string? note = null, bool busy = false)
    {
        OnUi(() =>
        {
            _firewall = verdict;
            _firewallNote = note;
            _firewallBusy = busy;
            if (_job is not null && JobView.Visibility == Visibility.Visible) ShowJob(_job);
            else if (_folder is not null && FolderView.Visibility == Visibility.Visible) ShowFolder(_folder);
        });
    }

    private void RenderFirewall(FirewallHint hint, bool show)
    {
        if (!show || _firewall is null or FwVerdict.Allowed)
        {
            hint.Visibility = Visibility.Collapsed;
            return;
        }
        hint.Render(_firewall.Value, _firewallNote, _firewallBusy);
        hint.Visibility = Visibility.Visible;
    }

    public void SetFolderFileError(int index, string message)
    {
        OnUi(() =>
        {
            var row = _folderRows.FirstOrDefault(item => item.Index == index);
            if (row is not null)
            {
                row.StateText = "打开失败";
                row.Kind = RowKind.Bad;
                row.IsEnabled = true;
            }
            FolderFeedbackText.SetResourceReference(TextBlock.ForegroundProperty, "BadBrush");
            FolderFeedbackText.Text = Compact(message, 24);
        });
    }

    public void ShowWindow()
    {
        OnUi(() =>
        {
            if (!IsVisible) base.Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            // 新任务到来：停靠在用户刚右键的那块屏幕（鼠标所在）的右下角
            if (_anchored) PlaceBottomRight(onCursorMonitor: true);
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
            BusyHintText.Text = Directory.Exists(path) ? "正在翻翻这个文件夹…" : "正在看看这是什么视频…";
            BusyBubble.Say(Words.Busy);
            SwitchTo(BusyView);
            SetTaskbar(TaskbarItemProgressState.Indeterminate);
        });
    }

    private void ShowIdle()
    {
        _currentUrl = "";
        IdleBubble.Say([.. Words.Idle, .. Words.Praise], 2.8);
        SwitchTo(IdleView);
        SetTaskbar(TaskbarItemProgressState.None);
    }

    private void ShowJob(MediaJob job)
    {
        SwitchTo(JobView);

        _currentUrl = $"{CurrentBaseUrl}/s/{job.Token}";
        JobTitleText.Text = string.IsNullOrWhiteSpace(job.Title) ? Path.GetFileName(job.SourcePath) : job.Title;
        JobDescriptionText.Text = string.IsNullOrWhiteSpace(job.SourceDescription)
            ? Path.GetFileName(job.SourcePath)
            : job.SourceDescription;
        // 二维码编码 + 图片解码很贵：只在 URL 变化时做一次，进度刷新不重建
        if (!string.Equals(_currentUrl, _jobQrUrl, StringComparison.Ordinal))
        {
            _jobQrUrl = _currentUrl;
            JobLinkTextBox.Text = _currentUrl;
            JobQrImage.Source = BuildQr(_currentUrl);
        }
        BackToFolderButton.Visibility = _folder is null ? Visibility.Collapsed : Visibility.Visible;

        // 送达：手机打开过之后，二维码收起（还能展开给第二台设备扫）
        var devices = job.Clients.Devices;
        var delivered = devices.Count > 0;
        var expanded = _qrExpandedFor == job.Token;
        JobDelivered.Visibility = delivered ? Visibility.Visible : Visibility.Collapsed;
        // 转码失败：扫了也只能看到出错页，二维码、链接、按钮整块收起，只留原因和下一步
        var failed = job.State == JobState.Failed;
        JobQrPanel.Visibility = !failed && (!delivered || expanded) ? Visibility.Visible : Visibility.Collapsed;
        JobShowQrButton.Visibility = delivered && !failed ? Visibility.Visible : Visibility.Collapsed;
        JobSharePanel.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
        JobShowQrButton.Content = expanded ? "收起二维码" : "再给一台设备扫码";
        if (delivered) JobDeliveredDevice.Text = JoinDevices(devices);
        // 已经有手机连进来 = 网络是通的，防火墙提示就不必了
        RenderFirewall(JobFirewallHint, show: !delivered && !failed);

        var percent = Math.Clamp((int)Math.Round(job.Progress * 100), 0, 100);
        switch (job.State)
        {
            case JobState.Transcoding:
                JobMascot.Face = MascotFace.Busy;
                if (delivered) JobBubble.Say(Words.Delivered);
                else JobBubble.Say(Words.Busy, 3.2);
                JobDeliveredHint.Text = "那边正在等转码，转好会自己开始";
                JobProgressPanel.Visibility = Visibility.Visible;
                JobPercentText.Text = percent.ToString();
                AnimateHatch(job.Progress);
                if (_etaFor != job.Token) { _etaFor = job.Token; _eta = new EtaEstimator(); }
                JobEtaText.Text = EtaEstimator.Format(job.Progress < 0.01 ? null : _eta.Push(job.Progress));
                SetStateText(string.IsNullOrWhiteSpace(job.PlanExplanation) ? "转好之后会自动变成可播放" : job.PlanExplanation, "Text3Brush");
                SetTaskbar(job.Progress < 0.01 ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.Normal, job.Progress);
                break;

            case JobState.Serving:
                JobMascot.Face = MascotFace.Ready;
                JobBubble.Say(delivered ? [.. Words.Delivered, .. Words.Praise] : Words.Praise, 2.8);
                JobDeliveredHint.Text = "那边可以看了";
                JobProgressPanel.Visibility = Visibility.Collapsed;
                SetStateText(delivered ? "" : "转好了，扫码就能看。", "OkBrush");
                SetTaskbar(TaskbarItemProgressState.None);
                break;

            case JobState.Failed:
                JobMascot.Face = MascotFace.Error;
                JobBubble.Hide();
                JobDeliveredHint.Text = "可惜这个视频转不了";
                JobProgressPanel.Visibility = Visibility.Collapsed;
                SetStateText("这个视频没能转好：" + (string.IsNullOrWhiteSpace(job.Error) ? "请检查 ffmpeg 和文件格式" : job.Error)
                    + "\n换一个文件，或者重新右键它再试一次。", "BadBrush");
                SetTaskbar(TaskbarItemProgressState.Error, 1);
                break;
        }
    }

    private void SetStateText(string text, string brushKey)
    {
        JobStateText.Text = text;
        JobStateText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        JobStateText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    private void ShowFolder(FolderJob folder)
    {
        SwitchTo(FolderView);

        _currentUrl = $"{CurrentBaseUrl}/f/{folder.Token}";
        FolderTitleText.Text = string.IsNullOrWhiteSpace(folder.Title)
            ? Path.GetFileName(folder.FolderPath.TrimEnd(Path.DirectorySeparatorChar))
            : folder.Title;
        var files = folder.Files;
        FolderCountText.Text = $"{files.Count}{(folder.Truncated ? "+" : "")} 个视频";
        FolderLinkTextBox.Text = _currentUrl;
        // 二维码编码 + 图片解码很贵：只在 URL 变化时做一次
        if (!string.Equals(_currentUrl, _folderQrUrl, StringComparison.Ordinal))
        {
            _folderQrUrl = _currentUrl;
            FolderQrImage.Source = BuildQr(_currentUrl);
        }

        var devices = folder.Clients.Devices;
        var delivered = devices.Count > 0;
        var expanded = _qrExpandedFor == folder.Token;
        FolderDelivered.Visibility = delivered ? Visibility.Visible : Visibility.Collapsed;
        FolderQrPanel.Visibility = !delivered || expanded ? Visibility.Visible : Visibility.Collapsed;
        FolderQrPanel.Margin = delivered ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        FolderShowQrButton.Visibility = delivered ? Visibility.Visible : Visibility.Collapsed;
        FolderShowQrButton.Content = expanded ? "收起二维码" : "再给一台设备扫码";
        if (delivered) FolderDeliveredDevice.Text = JoinDevices(devices);
        RenderFirewall(FolderFirewallHint, show: !delivered);

        FolderMascot.Face = files.Count == 0 ? MascotFace.Empty : delivered ? MascotFace.Ready : MascotFace.Idle;
        FolderBubble.Say(delivered ? [.. Words.Delivered, .. Words.Praise] : Words.Praise, 2.8);
        FolderFeedbackText.SetResourceReference(TextBlock.ForegroundProperty, "Text3Brush");
        FolderFeedbackText.Text = files.Count == 0 ? "没有找到支持的媒体文件" : "";
        SetTaskbar(TaskbarItemProgressState.None);

        // 同一文件夹再次显示（返回列表）：同一列表快照时原地刷新状态，滚动位置与行对象原样保留。
        // 会话复用后列表可能被 Refresh（新列表引用）：行数/内容都可能变，必须重建——
        // 否则行数变短时 folder.Files[row.Index] 越界崩 UI，行数相同但内容变时显示旧文件名
        if (ReferenceEquals(_renderedFolder, folder)
            && ReferenceEquals(_renderedFiles, files)
            && _folderRows.Count > 0)
        {
            UpdateFolderRows(folder);
            return;
        }
        _renderedFolder = folder;
        _renderedFiles = files;
        _folderRows.Clear();
        foreach (var file in files)
        {
            var (stateText, kind) = RowStateFor(folder, file);
            _folderRows.Add(new FolderRow
            {
                Index = file.Index,
                DisplayIndex = (file.Index + 1).ToString(),
                Name = Path.GetFileNameWithoutExtension(file.Name),
                SizeText = HumanSize.Format(file.SizeBytes),
                StateText = stateText,
                Kind = kind,
                IsEnabled = true,
            });
        }
    }

    /// <summary>行状态：转码中 42% / 就绪 / 失败；没打开过的留空（整行本身就是按钮）。</summary>
    private (string Text, RowKind Kind) RowStateFor(FolderJob folder, FolderFile file)
    {
        if (folder.OpenedToken(file.Index) is not { } token || JobLookup?.Invoke(token) is not { } job)
            return ("", RowKind.None);
        return job.State switch
        {
            JobState.Transcoding => ($"转码中 {(int)Math.Round(job.Progress * 100)}%", RowKind.Busy),
            JobState.Serving => ("就绪", RowKind.Ok),
            _ => ("失败", RowKind.Bad),
        };
    }

    /// <summary>同一文件夹再次显示 / 定时刷新：按当前 job 状态原地刷新行状态，不重建集合（保住滚动位置）。</summary>
    private void UpdateFolderRows(FolderJob folder)
    {
        // 一次快照：Refresh 在服务器线程并发替换列表（同一文件夹被重新右键时），
        // 逐行重读会在列表变短时 row.Index 越界崩 UI 线程；快照后越界的行直接跳过
        var files = folder.Files;
        if (!ReferenceEquals(files, _renderedFiles)) return; // 列表已换：等下一次 ShowFolder 重建
        foreach (var row in _folderRows)
        {
            if (row.Index >= files.Count) break;
            if (row.Kind == RowKind.Opening) continue; // 正在打开：等打开结果，不被定时刷新盖掉
            var (stateText, kind) = RowStateFor(folder, files[row.Index]);
            row.StateText = stateText;
            row.Kind = kind;
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
            ErrorMessageText.Text = Compact(message, 160);
            _currentUrl = "";
            SwitchTo(ErrorView);
            SetTaskbar(TaskbarItemProgressState.Error, 1);
        });
    }

    /// <summary>切换视图：只有一个可见。文件夹视图要手动高度（列表可滚动、窗口可拉伸），其余随内容。</summary>
    private void SwitchTo(FrameworkElement view)
    {
        var entering = view.Visibility != Visibility.Visible;
        SetFolderMode(ReferenceEquals(view, FolderView)); // 必须先于显示文件夹视图：否则列表在「随内容」模式下会把窗口撑到无限高
        foreach (var v in new FrameworkElement[] { IdleView, BusyView, ErrorView, JobView, FolderView })
            v.Visibility = ReferenceEquals(v, view) ? Visibility.Visible : Visibility.Collapsed;
        _folderRefreshTimer.IsEnabled = ReferenceEquals(view, FolderView);
        if (entering) AnimateIn(view);
    }

    private void SetFolderMode(bool on)
    {
        if (on == _folderMode) return;
        _folderMode = on;
        if (on)
        {
            SizeToContent = SizeToContent.Manual;
            Height = Math.Clamp(UiState.FolderHeight ?? 660, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32));
            ResizeMode = ResizeMode.CanResize;
            Chrome.ResizeBorderThickness = new Thickness(0, 6, 0, 6); // 只能上下拉，宽度固定
        }
        else
        {
            ResizeMode = ResizeMode.CanMinimize;
            Chrome.ResizeBorderThickness = new Thickness(0);
            SizeToContent = SizeToContent.Height;
        }
        if (_anchored && IsVisible) PlaceBottomRight(onCursorMonitor: false);
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

    /// <summary>链接框获得焦点即全选：手动 Ctrl+C 也方便。</summary>
    private void LinkBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => ((TextBox)sender).SelectAll();

    private void ShowQrButton_Click(object sender, RoutedEventArgs e)
    {
        var token = ReferenceEquals(sender, FolderShowQrButton) ? _folder?.Token : _job?.Token;
        if (token is null) return;
        _qrExpandedFor = _qrExpandedFor == token ? "" : token;
        if (ReferenceEquals(sender, FolderShowQrButton) && _folder is not null) ShowFolder(_folder);
        else if (_job is not null) ShowJob(_job);
    }

    private void FolderItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FolderRow row }) return;

        row.StateText = "正在打开";
        row.Kind = RowKind.Opening;
        row.IsEnabled = false;
        FolderFeedbackText.Text = "";

        if (FolderFileClicked is { } handler)
            handler.Invoke(row.Index);
        else
        {
            row.StateText = "无法打开";
            row.Kind = RowKind.Bad;
            row.IsEnabled = true;
        }
    }

    private void BackToFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_folder is null) return;
        _job = null; // 回到文件夹视图：后台 job 的进度事件不再把窗口抢回 job 视图
        // 刚才点开的那一行还停在「正在打开」：回到列表时按真实状态刷新
        foreach (var row in _folderRows) if (row.Kind == RowKind.Opening) row.Kind = RowKind.None;
        ShowFolder(_folder);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
            _anchored = false; // 用户自己摆了位置：之后不再自动挪回右下角
        }
        catch (InvalidOperationException) { }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    /// <summary>打开「关于」对话框（托盘菜单入口）：需要焦点/层级与主窗口一致。</summary>
    public void ShowAbout()
    {
        OnUi(() =>
        {
            var dialog = new AboutDialog
            {
                Owner = IsVisible ? this : null,
                WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            };
            dialog.ShowDialog();
        });
    }

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

        if (CloseRequested is { } handler) handler.Invoke();
        else Dispose();
    }

    // ── 位置：停靠右下角、向上长高 ──

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 系统切换深浅色时广播 WM_SETTINGCHANGE("ImmersiveColorSet")：转给主题管理器重新读设置
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero
            && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            ThemeManager.Refresh();
        return IntPtr.Zero;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_folderMode)
        {
            // 文件夹模式：高度是用户拉出来的，记住它（防抖写盘）；不挪位置，免得和拖边框打架
            if (e.HeightChanged && IsLoaded) { _saveHeightTimer.Stop(); _saveHeightTimer.Start(); }
            return;
        }
        // 随内容变高 / 变矮：底边钉住，向上长
        if (_anchored && IsVisible && e.HeightChanged) PlaceBottomRight(onCursorMonitor: false, e.NewSize.Height);
    }

    /// <summary>挪到屏幕工作区右下角（留 16 DIP 边）。工作区取自 Win32（按鼠标或窗口所在的显示器），
    /// 用窗口自己的设备变换换算成 DIP 后设置 Left/Top——必须走 WPF 属性而不是直接 SetWindowPos：
    /// WPF 按内容改高度时用的是它缓存的 Left/Top，绕开它挪窗口会被挪回去。
    /// heightDip：SizeChanged 里传新高度（事件先于窗口真正改尺寸触发，此时读到的还是旧高度）。</summary>
    private void PlaceBottomRight(bool onCursorMonitor, double? heightDip = null)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || WindowState != WindowState.Normal) return;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;
        IntPtr monitor;
        if (onCursorMonitor && NativeMethods.GetCursorPos(out var pt))
            monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        else
            monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        var toDip = target.TransformFromDevice;
        var bottomRight = toDip.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
        var topLeft = toDip.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
        const double margin = 16;
        var height = heightDip ?? ActualHeight;
        Left = bottomRight.X - ActualWidth - margin;
        Top = Math.Max(topLeft.Y + margin, bottomRight.Y - height - margin);
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

    private static void AnimateIn(FrameworkElement view)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var fade = new DoubleAnimation(0, 1, ViewFadeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        view.BeginAnimation(OpacityProperty, fade);
        if (view.RenderTransform is TranslateTransform translate)
        {
            var slide = new DoubleAnimation(8, 0, ViewSlideDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    private void AnimateHatch(double progress)
    {
        JobHatch.IsIndeterminate = progress < 0.01;
        if (!SystemParameters.ClientAreaAnimation)
        {
            JobHatch.BeginAnimation(HatchProgress.ValueProperty, null);
            JobHatch.Value = progress;
            return;
        }
        JobHatch.BeginAnimation(HatchProgress.ValueProperty, new DoubleAnimation(progress, ProgressDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>任务栏按钮上的进度：窗口最小化了也能看到转到哪了。</summary>
    private void SetTaskbar(TaskbarItemProgressState state, double value = 0)
    {
        TaskbarItemInfo.ProgressState = state;
        TaskbarItemInfo.ProgressValue = value;
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
        _folderRefreshTimer.Stop();
        _allowClose = true;
        if (IsLoaded) Close();
        Application.Current?.Shutdown();
    }

    /// <summary>「iPad」「iPad 和 iPhone」「iPad、iPhone 等 3 台」。</summary>
    private static string JoinDevices(IReadOnlyList<string> devices) => devices.Count switch
    {
        1 => devices[0],
        2 => $"{devices[0]} 和 {devices[1]}",
        _ => $"{devices[0]}、{devices[1]} 等 {devices.Count} 台",
    };

    private static ImageSource? BuildQr(string url)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            using var qr = new PngByteQRCode(data);
            var bytes = qr.GetGraphic(8, drawQuietZones: false); // 白卡片自带留白，二维码本身不再加静区
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

    private enum RowKind { None, Busy, Ok, Bad, Opening }

    private sealed class FolderRow : INotifyPropertyChanged
    {
        private string _stateText = "";
        private RowKind _kind;
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

        /// <summary>状态种类：模板按它选配色 token（DynamicResource，跟随深浅色）。</summary>
        public RowKind Kind
        {
            get => _kind;
            set { if (_kind != value) { _kind = value; Notify(); } }
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

    /// <summary>剩余时间估算（与网页 pu.js 同一算法）：最近 30 秒的进度速率，指数平滑防跳；
    /// 前 5 秒或速率不可信时返回 null（显示「正在估算」）。</summary>
    private sealed class EtaEstimator
    {
        private readonly Queue<(long Ms, double P)> _samples = new();
        private readonly long _t0 = Environment.TickCount64;
        private double? _smooth;

        public double? Push(double p)
        {
            var now = Environment.TickCount64;
            _samples.Enqueue((now, p));
            while (_samples.Count > 2 && now - _samples.Peek().Ms > 30_000) _samples.Dequeue();
            if (now - _t0 < 5000 || _samples.Count < 2) return null;
            var (ms0, p0) = _samples.Peek();
            var dt = (now - ms0) / 1000.0;
            var dp = p - p0;
            if (dp <= 0 || dt <= 0) return _smooth;
            var eta = (1 - p) / (dp / dt);
            _smooth = _smooth is { } s ? s * 0.7 + eta * 0.3 : eta;
            return _smooth;
        }

        public static string Format(double? seconds)
        {
            if (seconds is not { } s || double.IsInfinity(s) || double.IsNaN(s)) return "正在估算还要多久";
            if (s < 50) return "马上就好";
            var m = (int)Math.Round(s / 60);
            return m < 60 ? $"还要 {m} 分钟左右" : $"还要 {m / 60} 小时 {m % 60} 分钟左右";
        }
    }
}

/// <summary>窗口界面的小状态（文件夹模式的窗口高度），存 %LOCALAPPDATA%\Pu\ui.json。读写失败一律忽略。</summary>
internal static class UiState
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pu", "ui.json");

    public static double? FolderHeight
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                return doc.RootElement.TryGetProperty("folderHeight", out var h) && h.TryGetDouble(out var v) && v > 0 ? v : null;
            }
            catch { return null; }
        }
    }

    public static void SaveFolderHeight(double height)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, $"{{\"folderHeight\":{Math.Round(height)}}}");
        }
        catch { /* 记不住高度不影响使用 */ }
    }
}

internal static class NativeMethods
{
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
}
