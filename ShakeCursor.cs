// 搖晃滑鼠 → 游標放大（類似 macOS 的「搖晃以尋找游標」）
// 常駐系統匣；右鍵選單可設定開機自動啟動 / 結束。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

class ShakeCursor : Form
{
    // ===== 可調參數 =====
    const int    TickMs          = 15;    // 偵測頻率
    const int    MinSegmentPx    = 25;    // 每次來回至少移動多少像素才算一次
    const int    ReversalsNeeded = 4;     // 時間窗內要幾次換方向才觸發
    const int    WindowMs        = 800;   // 時間窗
    const float  BigScale        = 9f;    // 放大倍率（相對標準游標）
    const int    StillMs         = 450;   // 停止快速移動多久後縮回
    const int    FastMovePx      = 12;    // 每 tick 移動超過這個才算「還在晃」

    // ===== Win32 =====
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] static extern int GetDpiForSystem();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    const string RunKey  = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "ShakeCursor";

    // 標準箭頭外形（熱點在 0,0）
    static readonly PointF[] Arrow = {
        new PointF(0, 0), new PointF(0, 17), new PointF(4, 13.2f), new PointF(6.8f, 19.6f),
        new PointF(9.6f, 18.4f), new PointF(6.9f, 12.2f), new PointF(12.2f, 12.2f)
    };

    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly float dpiScale;

    POINT last;
    int dirX, dirY, segX, segY;
    readonly Queue<int> reversals = new Queue<int>();
    bool active;
    int lastFastMove;
    float scale;          // 目前繪製倍率，0 = 隱藏
    float drawnScale = -1;
    POINT drawnPos;
    bool visibleNow;

    ShakeCursor()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-10000, -10000);
        Size = new Size(1, 1);

        int dpi = 96;
        try { dpi = GetDpiForSystem(); } catch { }
        dpiScale = dpi / 96f;

        BuildTray();
        GetCursorPos(out last);
        timer.Interval = TickMs;
        timer.Tick += OnTick;
        timer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // LAYERED | TRANSPARENT(點擊穿透) | TOOLWINDOW | TOPMOST | NOACTIVATE
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8 | 0x8000000;
            return cp;
        }
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    // 一開始不顯示視窗
    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated) CreateHandle();
        base.SetVisibleCore(value && visibleNow);
    }

    void BuildTray()
    {
        var menu = new ContextMenuStrip();
        var startup = new ToolStripMenuItem("開機自動啟動") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += delegate { SetStartup(startup.Checked); };
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("結束", null, delegate { tray.Visible = false; Application.Exit(); });

        using (var bmp = RenderArrow(1.5f, 32))
            tray.Icon = Icon.FromHandle(bmp.GetHicon());
        tray.Text = "ShakeCursor：搖晃滑鼠放大游標";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
    }

    static bool IsStartupEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(AppName) != null;
    }

    static void SetStartup(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(AppName, false);
        }
    }

    void OnTick(object sender, EventArgs e)
    {
        POINT p;
        if (!GetCursorPos(out p)) return;
        int now = Environment.TickCount;
        int dx = p.X - last.X, dy = p.Y - last.Y;
        last = p;

        if (Math.Abs(dx) + Math.Abs(dy) >= FastMovePx * dpiScale) lastFastMove = now;
        TrackAxis(dx, ref dirX, ref segX, now);
        TrackAxis(dy, ref dirY, ref segY, now);
        while (reversals.Count > 0 && now - reversals.Peek() > WindowMs) reversals.Dequeue();

        if (reversals.Count >= ReversalsNeeded) { active = true; lastFastMove = now; }
        if (active && now - lastFastMove > StillMs) { active = false; reversals.Clear(); }

        float baseScale = dpiScale;
        float target = active ? BigScale * dpiScale : 0f;
        if (active && scale < baseScale) scale = baseScale;
        scale += (target - scale) * (active ? 0.28f : 0.22f);
        if (!active && scale < baseScale * 1.3f) scale = 0f;

        Render(p);
    }

    void TrackAxis(int d, ref int dir, ref int seg, int now)
    {
        if (Math.Abs(d) < 2) return;
        int s = Math.Sign(d);
        if (s == dir) { seg += Math.Abs(d); return; }
        if (dir != 0 && seg >= MinSegmentPx * dpiScale) reversals.Enqueue(now);
        dir = s;
        seg = Math.Abs(d);
    }

    void Render(POINT p)
    {
        if (scale <= 0f)
        {
            if (visibleNow) { visibleNow = false; Hide(); }
            drawnScale = -1;
            return;
        }
        if (Math.Abs(scale - drawnScale) < 0.01f && p.X == drawnPos.X && p.Y == drawnPos.Y) return;

        int pad = (int)Math.Ceiling(2 * scale);
        int size = (int)Math.Ceiling(21 * scale) + pad * 2;
        using (var bmp = RenderArrow(scale, size, pad))
            Push(bmp, p.X - pad, p.Y - pad);
        drawnScale = scale;
        drawnPos = p;

        if (!visibleNow)
        {
            visibleNow = true;
            Show();
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // TOPMOST, NOSIZE|NOMOVE|NOACTIVATE
        }
    }

    static Bitmap RenderArrow(float s, int size, int pad = -1)
    {
        if (pad < 0) pad = (int)Math.Ceiling(2 * s);
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var path = new GraphicsPath())
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var pts = new PointF[Arrow.Length];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new PointF(pad + Arrow[i].X * s, pad + Arrow[i].Y * s);
            path.AddPolygon(pts);

            // 陰影
            using (var shadow = (GraphicsPath)path.Clone())
            using (var m = new Matrix())
            using (var sb = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            {
                m.Translate(s * 0.6f, s * 0.9f);
                shadow.Transform(m);
                g.FillPath(sb, shadow);
            }
            g.FillPath(Brushes.White, path);
            using (var pen = new Pen(Color.Black, Math.Max(1f, s * 1.1f)) { LineJoin = LineJoin.Round })
                g.DrawPath(pen, path);
        }
        return bmp;
    }

    void Push(Bitmap bmp, int x, int y)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var dst = new POINT { X = x, Y = y };
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [STAThread]
    static void Main()
    {
        bool created;
        using (new Mutex(true, "ShakeCursor_SingleInstance", out created))
        {
            if (!created) return;
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.Run(new ShakeCursor());
        }
    }
}
