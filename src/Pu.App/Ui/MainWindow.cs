using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Pu.Core.Serving;
using QRCoder;

namespace Pu.App.Ui;

/// <summary>
/// 原生主窗口（Win32 + GDI+ 自绘，方案.md：WinForms/WPF 不支持 NativeAOT）。
/// 显示二维码 / 链接（复制·打开）/ 转码进度 / 文件夹列表。
/// 必须在专用线程上构造并 Run()（窗口与消息循环同线程）。
/// </summary>
public sealed class MainWindow : IDisposable
{
    // ── 布局（96dpi 基准，运行时按 _scale 缩放）──
    private const int BaseW = 460, BaseH = 668;
    private const int TitleBarH = 48;
    private const int Qt = 24; // 内容左右边距

    private const uint WM_APP = 0x8000;
    private const uint WM_APP_REFRESH = WM_APP + 1;
    private const uint WM_APP_ICON = WM_APP + 2;
    private const uint WM_NCHITTEST = 0x0084, HTCAPTION = 0x0002, HTCLIENT = 0x0001;
    private const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEMOVE = 0x0200, WM_MOUSELEAVE = 0x02A3, WM_MOUSEWHEEL = 0x020A;
    private const uint WM_PAINT = 0x000F, WM_TIMER = 0x0113, WM_DESTROY = 0x0002, WM_CLOSE = 0x0010;
    private const uint WM_SETICON = 0x0080, ICON_SMALL = 0, ICON_BIG = 1;
    private const uint TME_LEAVE = 0x00000002;
    private const int SW_SHOW = 5, SW_HIDE = 0;

    private const int IdMin = 1, IdClose = 2, IdCopy = 3, IdOpen = 4;
    private const int TimerAnim = 1;
    private const int ListRowH = 54;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static readonly WndProcDelegate WndProc = WindowProc; // 防 GC
    private static MainWindow? s_current;

    // ── 颜色 ──
    private static readonly Color C_Bg = Color.FromArgb(0x0a, 0x0e, 0x14);
    private static readonly Color C_Surface = Color.FromArgb(0x11, 0x16, 0x1f);
    private static readonly Color C_Surface2 = Color.FromArgb(0x17, 0x1e, 0x29);
    private static readonly Color C_Line = Color.FromArgb(0x21, 0x2c, 0x3b);
    private static readonly Color C_Text = Color.FromArgb(0xe8, 0xee, 0xf6);
    private static readonly Color C_Text2 = Color.FromArgb(0x9f, 0xb0, 0xc3);
    private static readonly Color C_Text3 = Color.FromArgb(0x7c, 0x8c, 0xa0);
    private static readonly Color C_Mint = Color.FromArgb(0x5e, 0xea, 0xd4);
    private static readonly Color C_MintInk = Color.FromArgb(0x0c, 0x2b, 0x23);
    private static readonly Color C_Amber = Color.FromArgb(0xf5, 0xb8, 0x3d);
    private static readonly Color C_Red = Color.FromArgb(0xf8, 0x71, 0x71);
    private static readonly Color C_BtnHover = Color.FromArgb(0x1c, 0x26, 0x34);
    private static readonly Color C_MintHover = Color.FromArgb(0x75, 0xf0, 0xdd);

    private IntPtr _hwnd;
    private IntPtr _icon16, _icon32;
    private float _scale = 1f;
    private bool _disposed;

    // 跨线程状态（volatile 原子读写）
    private volatile MediaJob? _job;
    private volatile FolderJob? _folder;
    private volatile string _baseUrl = "";
    private volatile Bitmap? _qr;

    // UI 状态
    private int _hoverId, _pressedId;
    private double _anim = 0;          // 进度插值显示值
    private double _animTarget = 0;
    private int _listScroll;
    private string _clipMsg = "";      // “已复制”瞬态提示
    private long _clipMsgUntil;
    private Dictionary<float, Font> _fonts = new();
    private Bitmap? _backBuffer;
    private Graphics? _backG;

    public event Action? CloseRequested;
    public event Action<int>? FolderFileClicked;

    public MainWindow()
    {
        s_current = this;
    }

    public void Run()
    {
        _scale = GetDpiForSystem() / 96f;
        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASS
        {
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProc),
            hInstance = hInstance,
            hIcon = AppIcons.LoadHIcon(32),
            hCursor = LoadCursor(IntPtr.Zero, 32512), // IDC_ARROW
            lpszClassName = "PuMainWindow",
        };
        RegisterClass(ref wc);
        _icon32 = wc.hIcon;
        _icon16 = AppIcons.LoadHIcon(16);

        int w = (int)(BaseW * _scale), h = (int)(BaseH * _scale);
        _hwnd = CreateWindowEx(0, "PuMainWindow", "pu~", 0,
            (GetSystemMetrics(1) - w) / 2, (GetSystemMetrics(0) - h) / 2, w, h,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("创建主窗口失败");

        // Win11 圆角 + 阴影
        int corner = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, 33, ref corner, 4);
        SendMessage(_hwnd, WM_SETICON, (IntPtr)ICON_SMALL, _icon16);
        SendMessage(_hwnd, WM_SETICON, (IntPtr)ICON_BIG, _icon32);
        SetTimer(_hwnd, TimerAnim, 33, IntPtr.Zero);

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        KillTimer(_hwnd, TimerAnim);
        DestroyWindow(_hwnd);
        UnregisterClass("PuMainWindow", hInstance);
        s_current = null;
    }

    // ── 跨线程入口 ──

    public void SetBaseUrl(string url) { _baseUrl = url; }
    public void SetJob(MediaJob? job)
    {
        _job = job;
        _folder = null;
        _qr = job is null ? null : BuildQr(BaseUrlFor(job));
        _animTarget = job?.Progress ?? 1;
        PostRefresh();
    }

    public void SetFolder(FolderJob? folder)
    {
        _folder = folder;
        _job = null;
        _qr = null;
        PostRefresh();
    }

    public void ShowWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindowAsync(_hwnd, SW_SHOW);
            SetForegroundWindow(_hwnd);
        }
    }

    public void Hide() { if (_hwnd != IntPtr.Zero) ShowWindowAsync(_hwnd, SW_HIDE); }

    private string BaseUrlFor(MediaJob job) => _baseUrl + "/s/" + job.Token;
    private string BaseUrlFor(FolderJob folder) => _baseUrl + "/f/" + folder.Token;

    private void PostRefresh()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_APP_REFRESH, IntPtr.Zero, IntPtr.Zero);
    }

    // ── 消息处理 ──

    private static IntPtr WindowProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        => s_current is { } t ? t.OnMessage(h, m, w, l) : DefWindowProc(h, m, w, l);

    private IntPtr OnMessage(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        switch (m)
        {
            case WM_APP_REFRESH:
                InvalidateRect(_hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            case WM_APP_ICON:
                return IntPtr.Zero;
            case WM_PAINT:
                Paint();
                return IntPtr.Zero;
            case WM_TIMER when w == (IntPtr)TimerAnim:
                TickAnim();
                return IntPtr.Zero;
            case WM_NCHITTEST:
            {
                var x = l.ToInt32() & 0xFFFF; var y = (l.ToInt32() >> 16) & 0xFFFF;
                var pt = ScreenToClient(_hwnd, new POINT { X = x, Y = y });
                if (pt.Y >= 0 && pt.Y < (int)(TitleBarH * _scale)
                    && !HitTest(IdMin).Contains(pt.X, pt.Y)
                    && !HitTest(IdClose).Contains(pt.X, pt.Y))
                    return (IntPtr)HTCAPTION;
                return (IntPtr)HTCLIENT;
            }
            case WM_LBUTTONDOWN:
            {
                var pt = ClientPoint(l);
                _pressedId = _hoverId;
                SetCapture(_hwnd);
                return IntPtr.Zero;
            }
            case WM_LBUTTONUP:
            {
                var pt = ClientPoint(l);
                var hit = HitId(pt.X, pt.Y);
                if (_pressedId != 0 && hit == _pressedId) Activate(hit);
                _pressedId = 0;
                ReleaseCapture();
                return IntPtr.Zero;
            }
            case WM_MOUSEMOVE:
            {
                var pt = ClientPoint(l);
                var id = HitId(pt.X, pt.Y);
                if (id != _hoverId)
                {
                    _hoverId = id;
                    InvalidateRect(_hwnd, IntPtr.Zero, false);
                }
                var tme = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = _hwnd };
                TrackMouseEvent(ref tme);
                return IntPtr.Zero;
            }
            case WM_MOUSELEAVE:
                _hoverId = 0;
                InvalidateRect(_hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            case WM_MOUSEWHEEL:
                if (_folder is not null)
                {
                    var delta = (short)((w.ToInt64() >> 16) & 0xFFFF);
                    var max = Math.Max(0, (_folder.Files.Count * ListRowH) - ListViewHeight());
                    _listScroll = Math.Clamp(_listScroll - delta / 120 * 60, 0, max);
                    InvalidateRect(_hwnd, IntPtr.Zero, false);
                }
                return IntPtr.Zero;
            case WM_CLOSE:
                CloseRequested?.Invoke();
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(h, m, w, l);
    }

    private POINT ClientPoint(IntPtr l)
        => new() { X = l.ToInt32() & 0xFFFF, Y = (l.ToInt32() >> 16) & 0xFFFF };

    private void Activate(int id)
    {
        switch (id)
        {
            case IdMin:
                ShowWindowAsync(_hwnd, 6 /* SW_MINIMIZE */);
                break;
            case IdClose:
                CloseRequested?.Invoke();
                break;
            case IdCopy:
                CopyToClipboard(_job is not null ? BaseUrlFor(_job) : _baseUrl);
                _clipMsg = "已复制";
                _clipMsgUntil = Environment.TickCount64 + 1500;
                InvalidateRect(_hwnd, IntPtr.Zero, false);
                break;
            case IdOpen:
                if (_job is not null) OpenUrl(BaseUrlFor(_job));
                break;
            default:
                if (id >= 1000) FolderFileClicked?.Invoke(id - 1000);
                break;
        }
    }

    // ── 命中测试（布局）──

    private RectangleF HitTest(int id) => id switch
    {
        IdMin => new RectangleF(W() - 92 * F(), 0, 46 * F(), TitleBarH * F()),
        IdClose => new RectangleF(W() - 46 * F(), 0, 46 * F(), TitleBarH * F()),
        IdCopy => new RectangleF(Qt * F(), CopyBtnY(), (W() - 3 * Qt) / 2f * F(), 42 * F()),
        IdOpen => new RectangleF((Qt + (W() - 3 * Qt) / 2f + Qt) * F(), CopyBtnY(), (W() - 3 * Qt) / 2f * F(), 42 * F()),
        _ => RectangleF.Empty,
    };

    private int HitId(int x, int y)
    {
        foreach (var id in new[] { IdMin, IdClose })
            if (HitTest(id).Contains(x, y)) return id;
        if (_job is not null)
        {
            foreach (var id in new[] { IdCopy, IdOpen })
                if (HitTest(id).Contains(x, y)) return id;
        }
        else if (_folder is not null)
        {
            var idx = ListHitRow(x, y);
            if (idx >= 0) return 1000 + idx;
        }
        return 0;
    }

    private int ListHitRow(int x, int y)
    {
        var top = ListTop();
        var scrolled = _listScroll;
        var row = (int)((y - top + scrolled) / (ListRowH * F()));
        if (row < 0 || row >= (_folder?.Files.Count ?? 0)) return -1;
        return row;
    }

    private int ListViewHeight() => (int)((BaseH - TitleBarH - 120) * F());

    // ── 布局数值 ──

    private float F() => _scale;
    private float W() => BaseW * F();
    private float H() => BaseH * F();
    private float TitleY() => TitleBarH * F();
    private float QrTop() => (TitleBarH + 26) * F();
    private float QrSize() => 204 * F();
    private float QrX() => (W() - QrSize()) / 2;
    private float HintTop() => QrTop() + QrSize() + 14 * F();
    private float LinkTop() => HintTop() + 34 * F();
    private float LinkH() => 40 * F();
    private float CopyBtnY() => LinkTop() + LinkH() + 14 * F();
    private float StatusTop() => CopyBtnY() + 42 * F() + 18 * F();
    private float ListTop() => (TitleBarH + 20) * F();

    // ── 绘制 ──

    private void Paint()
    {
        var msg = new PAINTSTRUCT();
        var dc = BeginPaint(_hwnd, ref msg);
        try
        {
            var w = (int)W(); var h = (int)H();
            _backBuffer ??= new Bitmap(w, h);
            _backG ??= Graphics.FromImage(_backBuffer);
            var g = _backG;
            g.Clear(C_Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.CompositingQuality = CompositingQuality.HighQuality;

            DrawTitleBar(g);
            if (_job is not null) DrawJob(g);
            else if (_folder is not null) DrawFolder(g);
            else DrawIdle(g);

            g.Flush();
            using var dcG = Graphics.FromHdc(dc);
            dcG.DrawImageUnscaled(_backBuffer, 0, 0);
        }
        finally
        {
            EndPaint(_hwnd, ref msg);
        }
    }

    private void DrawTitleBar(Graphics g)
    {
        // 标题
        using var icon = Icon.FromHandle(_icon16);
        g.DrawIcon(icon, (int)(16 * F()), (int)((TitleBarH - 16) / 2 * F()));
        var titleFont = Font(12.5f, FontStyle.Bold);
        using var titleBrush = new SolidBrush(C_Text);
        g.DrawString("pu~", titleFont, titleBrush, 40 * F(), (TitleBarH - 20) / 2 * F());

        // 状态胶囊
        if (_job is { } job)
        {
            var (label, color) = JobPill(job);
            var pillFont = Font(9.5f);
            var size = g.MeasureString(label, pillFont);
            var pillW = size.Width + 28 * F();
            var pillX = W() - 92 * F() - pillW - 12 * F();
            var pillY = (TitleBarH - 22 * F()) / 2;
            using var pillBrush = new SolidBrush(C_Surface2);
            FillRound(g, new RectangleF(pillX, pillY, pillW, 22 * F()), 11 * F(), pillBrush);
            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, pillX + 10 * F(), pillY + (22 * F() - 7 * F()) / 2, 7 * F(), 7 * F());
            using var lbl = new SolidBrush(C_Text2);
            g.DrawString(label, pillFont, lbl, pillX + 22 * F(), pillY + 1 * F());
        }

        // 最小化 / 关闭
        DrawTitleBtn(g, IdMin, "─");
        DrawTitleBtn(g, IdClose, "✕");
    }

    private void DrawTitleBtn(Graphics g, int id, string glyph)
    {
        var r = HitTest(id);
        var hover = _hoverId == id;
        if (hover)
        {
            using var b = new SolidBrush(id == IdClose ? Color.FromArgb(60, 200, 60, 60) : C_BtnHover);
            g.FillRectangle(b, r);
        }
        var f = Font(11f);
        using var b2 = new SolidBrush(hover && id == IdClose ? Color.White : C_Text2);
        var sz = g.MeasureString(glyph, f);
        g.DrawString(glyph, f, b2, r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2 - 2 * F());
    }

    private void DrawJob(Graphics g)
    {
        var job = _job!;
        var title = job.Title;
        var tFont = Font(14f, FontStyle.Bold);
        using var tBrush = new SolidBrush(C_Text);
        g.DrawString(title, tFont, tBrush, Qt * F(), TitleY() + 10 * F());

        // 二维码
        if (_qr is { } qr)
        {
            var qrRect = new RectangleF(QrX(), QrTop(), QrSize(), QrSize());
            using var white = new SolidBrush(Color.White);
            FillRound(g, qrRect, 14 * F(), white);
            var imgW = qr.Width; var imgH = qr.Height;
            var scale = (QrSize() - 26 * F()) / imgW;
            g.DrawImage(qr, QrX() + 13 * F(), QrTop() + 13 * F(), imgW * scale, imgH * scale);
            // 角标
            using var mint = new Pen(C_Mint, 3 * F());
            DrawCorner(g, mint, qrRect);
        }

        var hintFont = Font(11f);
        using var hintBrush = new SolidBrush(C_Text2);
        var hint = "手机 / 平板扫码观看（同一 Wi-Fi）";
        var hsz = g.MeasureString(hint, hintFont);
        g.DrawString(hint, hintFont, hintBrush, (W() - hsz.Width) / 2, HintTop());

        // 链接框
        var linkRect = new RectangleF(Qt * F(), LinkTop(), W() - 2 * Qt * F(), LinkH());
        using var linkBg = new SolidBrush(C_Surface2);
        FillRound(g, linkRect, 10 * F(), linkBg);
        using var linkPen = new Pen(C_Line);
        DrawRound(g, linkRect, 10 * F(), linkPen);
        var linkFont = Font(10.5f, FontStyle.Regular, "Consolas");
        using var linkBrush = new SolidBrush(C_Text);
        var url = BaseUrlFor(job);
        var shown = url.Length > 52 ? "…" + url[^48..] : url;
        g.DrawString(shown, linkFont, linkBrush, linkRect.X + 12 * F(), linkRect.Y + (linkRect.Height - 18 * F()) / 2);

        // 按钮
        DrawButton(g, IdCopy, _clipMsg.Length > 0 && Environment.TickCount64 < _clipMsgUntil ? "已复制 ✓" : "复制链接", primary: false);
        DrawButton(g, IdOpen, "打开链接", primary: true);

        // 状态区
        var top = StatusTop();
        switch (job.State)
        {
            case JobState.Transcoding:
            {
                var p = (int)Math.Round(_anim * 100);
                var pf = Font(30f, FontStyle.Bold);
                using var pb = new SolidBrush(C_Text);
                var ps = $"{p}%";
                var psz = g.MeasureString(ps, pf);
                g.DrawString(ps, pf, pb, (W() - psz.Width) / 2, top);
                // 进度条
                var barRect = new RectangleF(Qt * F(), top + 52 * F(), W() - 2 * Qt * F(), 8 * F());
                using var barBg = new SolidBrush(C_Surface2);
                FillRound(g, barRect, 4 * F(), barBg);
                if (_anim > 0.01)
                {
                    var fillW = (float)(barRect.Width * Math.Clamp(_anim, 0, 1));
                    using var fillPath = Rounded(new RectangleF(barRect.X, barRect.Y, fillW, barRect.Height), 4 * F());
                    using var fillBrush = new LinearGradientBrush(barRect, Color.FromArgb(0x2f, 0xb8, 0x9c), C_Mint, LinearGradientMode.Horizontal);
                    g.FillPath(fillBrush, fillPath);
                }
                var planFont = Font(10.5f);
                using var planBrush = new SolidBrush(C_Text3);
                var plan = job.PlanExplanation;
                var planSz = g.MeasureString(plan, planFont);
                g.DrawString(plan, planFont, planBrush, (W() - planSz.Width) / 2, top + 66 * F());
                break;
            }
            case JobState.Serving:
            {
                var okFont = Font(13f, FontStyle.Bold);
                using var okBrush = new SolidBrush(C_Mint);
                var msg = "✓ 已就绪，扫码即可播放";
                var sz = g.MeasureString(msg, okFont);
                g.DrawString(msg, okFont, okBrush, (W() - sz.Width) / 2, top + 4 * F());
                break;
            }
            case JobState.Failed:
            {
                var errFont = Font(11.5f);
                using var errBrush = new SolidBrush(C_Red);
                var msg = "处理失败：" + (job.Error ?? "未知错误");
                var sz = g.MeasureString(msg, errFont);
                g.DrawString(msg, errFont, errBrush, (W() - sz.Width) / 2, top + 4 * F());
                break;
            }
        }
    }

    private void DrawFolder(Graphics g)
    {
        var folder = _folder!;
        var tFont = Font(14f, FontStyle.Bold);
        using var tBrush = new SolidBrush(C_Text);
        g.DrawString(folder.Title, tFont, tBrush, Qt * F(), TitleY() + 10 * F());
        var cFont = Font(10.5f);
        using var cBrush = new SolidBrush(C_Text3);
        var count = $"{folder.Files.Count} 个文件";
        var csz = g.MeasureString(count, cFont);
        g.DrawString(count, cFont, cBrush, W() - Qt * F() - csz.Width, TitleY() + 14 * F());

        var top = ListTop();
        var rowH = ListRowH * F();
        var viewH = ListViewHeight();
        var first = (int)(_listScroll / rowH);
        for (int i = first; i < folder.Files.Count; i++)
        {
            var y = top + i * rowH - _listScroll;
            if (y > top + viewH) break;
            if (y + rowH < top) continue;
            DrawFolderRow(g, folder, i, y, rowH);
        }
    }

    private void DrawFolderRow(Graphics g, FolderJob folder, int i, float y, float rowH)
    {
        var f = folder.Files[i];
        var row = new RectangleF(Qt * F(), y, W() - 2 * Qt * F(), rowH - 6 * F());
        var hover = _hoverId == 1000 + i;
        using var bg = new SolidBrush(hover ? C_BtnHover : C_Surface);
        FillRound(g, row, 12 * F(), bg);
        using var linePen = new Pen(C_Line);
        DrawRound(g, row, 12 * F(), linePen);

        var idxFont = Font(10f);
        using var idxBrush = new SolidBrush(C_Text3);
        g.DrawString((i + 1).ToString(), idxFont, idxBrush, row.X + 10 * F(), row.Y + (row.Height - 16 * F()) / 2);

        var nameFont = Font(11.5f);
        using var nameBrush = new SolidBrush(C_Text);
        var name = f.Name;
        var nameW = row.Width - 150 * F();
        if (g.MeasureString(name, nameFont).Width > nameW)
        {
            while (name.Length > 1 && g.MeasureString(name + "…", nameFont).Width > nameW) name = name[..^1];
            name += "…";
        }
        g.DrawString(name, nameFont, nameBrush, row.X + 40 * F(), row.Y + (row.Height - 17 * F()) / 2);

        var sizeFont = Font(9.5f);
        using var sizeBrush = new SolidBrush(C_Text3);
        g.DrawString(FormatSize(f.SizeBytes), sizeFont, sizeBrush, row.Right - 96 * F(), row.Y + (row.Height - 15 * F()) / 2);

        var state = "new";
        if (folder.OpenedToken(f.Index) is { } t2) state = "serving";
        var (label, color) = state switch
        {
            "serving" => ("就绪", C_Mint),
            _ => ("未打开", C_Text3),
        };
        var bFont = Font(9.5f);
        using var bBrush = new SolidBrush(color);
        var bsz = g.MeasureString(label, bFont);
        g.DrawString(label, bFont, bBrush, row.Right - 82 * F(), row.Y + (row.Height - 15 * F()) / 2);
    }

    private void DrawIdle(Graphics g)
    {
        var f = Font(12f);
        using var b = new SolidBrush(C_Text3);
        var msg = "右键任意视频 / 文件夹 → pu~";
        var sz = g.MeasureString(msg, f);
        g.DrawString(msg, f, b, (W() - sz.Width) / 2, (H() - sz.Height) / 2);
    }

    private void DrawButton(Graphics g, int id, string label, bool primary)
    {
        var r = HitTest(id);
        var hover = _hoverId == id;
        var pressed = _pressedId == id;
        using var bg = new SolidBrush(primary
            ? (hover ? C_MintHover : C_Mint)
            : (hover ? C_BtnHover : C_Surface2));
        FillRound(g, r, 10 * F(), bg);
        using var pen = new Pen(primary ? Color.Transparent : C_Line);
        if (!primary) DrawRound(g, r, 10 * F(), pen);
        var f = Font(11.5f, FontStyle.Bold);
        using var tb = new SolidBrush(primary ? C_MintInk : C_Text2);
        var sz = g.MeasureString(label, f);
        g.DrawString(label, f, tb, r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2 - (pressed ? 1 : 0));
    }

    private void TickAnim()
    {
        var job = _job;
        if (job is null) return;
        _animTarget = job.State == JobState.Transcoding ? job.Progress : 1;
        var diff = _animTarget - _anim;
        if (Math.Abs(diff) < 0.002) { if (_anim != _animTarget) { _anim = _animTarget; InvalidateRect(_hwnd, IntPtr.Zero, false); } return; }
        _anim += diff * 0.22;
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private static (string Label, Color Color) JobPill(MediaJob job) => job.State switch
    {
        JobState.Transcoding => ("转码中", C_Amber),
        JobState.Serving => ("就绪", C_Mint),
        _ => ("失败", C_Red),
    };

    // ── 工具 ──

    private Font Font(float size, FontStyle style = FontStyle.Regular, string family = "Microsoft YaHei UI")
    {
        var key = size * _scale + (int)style * 100 + (family == "Consolas" ? 1000 : 0);
        if (!_fonts.TryGetValue(key, out var f))
        {
            f = new Font(family, size * _scale, style);
            _fonts[key] = f;
        }
        return f;
    }

    private static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        if (rad <= 0) { p.AddRectangle(r); return p; }
        var d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static void FillRound(Graphics g, RectangleF r, float rad, Brush b)
    { using var p = Rounded(r, rad); g.FillPath(b, p); }

    private static void DrawRound(Graphics g, RectangleF r, float rad, Pen pen)
    { using var p = Rounded(r, rad); g.DrawPath(pen, p); }

    private static void DrawCorner(Graphics g, Pen mint, RectangleF r)
    {
        var s = 16f;
        g.DrawLine(mint, r.X + s, r.Y + 4, r.X + 4, r.Y + 4);
        g.DrawLine(mint, r.X + 4, r.Y + 4, r.X + 4, r.Y + s);
        g.DrawLine(mint, r.Right - s, r.Y + 4, r.Right - 4, r.Y + 4);
        g.DrawLine(mint, r.Right - 4, r.Y + 4, r.Right - 4, r.Y + s);
        g.DrawLine(mint, r.X + s, r.Bottom - 4, r.X + 4, r.Bottom - 4);
        g.DrawLine(mint, r.X + 4, r.Bottom - 4, r.X + 4, r.Bottom - s);
        g.DrawLine(mint, r.Right - s, r.Bottom - 4, r.Right - 4, r.Bottom - 4);
        g.DrawLine(mint, r.Right - 4, r.Bottom - 4, r.Right - 4, r.Bottom - s);
    }

    private static string FormatSize(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):F0} MB",
        _ => $"{Math.Max(1, b / 1024)} KB",
    };

    private static Bitmap? BuildQr(string url)
    {
        try
        {
            using var gen = new QRCodeGenerator();
            var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            using var qr = new PngByteQRCode(data);
            using var ms = new MemoryStream(qr.GetGraphic(8));
            return new Bitmap(ms);
        }
        catch { return null; }
    }

    private static void CopyToClipboard(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return;
        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var h = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, h, bytes.Length);
                if (SetClipboardData(13, h) == IntPtr.Zero) Marshal.FreeHGlobal(h); // CF_UNICODETEXT
            }
            catch { Marshal.FreeHGlobal(h); }
        }
        finally { CloseClipboard(); }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (_icon16 != IntPtr.Zero) DestroyIcon(_icon16);
        if (_icon32 != IntPtr.Zero) DestroyIcon(_icon32);
        _qr?.Dispose();
        _backBuffer?.Dispose();
        _backG?.Dispose();
        foreach (var f in _fonts.Values) f.Dispose();
        _fonts.Clear();
    }

    // ── P/Invoke ──

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public Rectangle rcPaint;
        public int fRestore;
        public int fIncUpdate;
        // BYTE rgbReserved[32]
        public long rgbReserved0;
        public long rgbReserved1;
        public long rgbReserved2;
        public long rgbReserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS w);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string name, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr h, IntPtr rect, bool erase);
    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr h, int id, uint ms, IntPtr proc);
    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr h, int id);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr inst, int id);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")]
    private static extern POINT ScreenToClient(IntPtr h, POINT pt);
    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
