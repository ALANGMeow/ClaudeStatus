using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClaudeStatus
{
    // ===== 状态枚举（按聚合优先级从高到低）=====
    enum LightState
    {
        None = 0,     // 无会话      ⚪ 灰
        Idle = 1,     // 空闲待命    🟢 绿
        Waiting = 2,  // 轮到你      🔴 红常亮
        Running = 3,  // 运行中      🟡 黄闪
        Attention = 4 // 需要注意    🔴 红常亮
    }

    // ===== 单个会话的记账 =====
    class SessionInfo
    {
        public LightState State;
        public DateTime LastSeen;     // 最后收到任意事件的时间
        public DateTime StateSince;   // 进入当前 State 的时间
    }

    class Config
    {
        public int Port = 51234;
        public int BlinkMs = 500;            // 黄灯闪烁间隔
        public int WaitingDecaySec = 60;     // 轮到你 -> 空闲 的衰减秒数
        public int StaleSec = 600;           // 会话陈旧超时（无活动则移除）
        public int OffsetX = 0;              // 相对默认锚点的横向微调
        public int OffsetY = 0;              // 相对默认锚点的纵向微调
        public int RightGap = 16;            // 右边缘到托盘图标区的间距（像素）
        public double Scale = 1.0;           // 整体尺寸缩放倍数
        public double DotScale = 1.5;        // 指示灯相对整体的额外缩放（独立于字体，2.0=灯放大一倍）
        public bool LogEvents = true;        // 把收到的 hook 事件写入 events.log（诊断用）
        public int RunningStaleSec = 150;    // 运行态超过此秒数无任何事件 -> 兜底降级为"轮到你"
        public bool HideOnFullscreen = true; // 全屏应用时隐藏
        public string FontName = "Segoe UI"; // 显示字体
        public int FontWeight = 400;         // 文字字重（GDI lfWeight，100-900；400=标准，700=加粗）
        public string TextRunning = "Active";
        public string TextWaiting = "Finished";   // 一轮结束（绿灯闪烁）
        public string TextAttention = "Ask";
        public string TextIdle = "Idle";
        public string TextNone = "Claude";

        // 各状态使用的灯色（取值为内置色名，见 SvgIcon 加载处：
        // white/red/orange/yellow/green/black/blue/purple/brown/hollow）。
        // 不支持新增颜色，但可在已有 10 色中任意组合。
        public string ColorRunning = "yellow";       // 运行中
        public string ColorRunningBlink = "orange";  // 运行中闪烁的交替色（黄<->橙）
        public string ColorAttention = "red";        // 需要注意
        public string ColorWaiting = "green";        // 一轮结束
        public string ColorWaitingBlink = "black";   // 一轮结束闪烁的交替色（绿<->黑）
        public string ColorIdle = "green";           // 空闲
        public string ColorNone = "white";           // 无会话
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            // 单实例：用命名 Mutex 防止重复启动
            bool createdNew;
            using (var mutex = new Mutex(true, "ClaudeStatus_SingleInstance_Mutex", out createdNew))
            {
                if (!createdNew) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm());
            }
        }
    }

    class WidgetForm : Form
    {
        // ---- Win32 ----
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOPMOST = 0x00000008;
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string cls, string win);
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        // ---- GDI 文字（支持任意字重 lfWeight）----
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFontIndirect(ref LOGFONT lf);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")]
        static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")]
        static extern int SetTextColor(IntPtr hdc, int color);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern bool TextOut(IntPtr hdc, int x, int y, string s, int len);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetTextExtentPoint32(IntPtr hdc, string s, int len, out GSIZE sz);

        const int TRANSPARENT = 1;
        const int DEFAULT_CHARSET = 1;
        const int CLEARTYPE_QUALITY = 5;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct LOGFONT
        {
            public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
            public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet,
                        lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GSIZE { public int cx, cy; }

        // ---- 数据 ----
        readonly Config cfg;
        readonly string cfgPath;
        readonly Dictionary<string, SessionInfo> sessions = new Dictionary<string, SessionInfo>();
        readonly object gate = new object();

        HttpListener listener;
        Thread listenThread;
        volatile bool running = true;

        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        bool blinkOn = true;
        bool paused = false;
        DateTime lastBlink = DateTime.Now;

        // 拖动微调
        bool dragging = false;
        Point dragStartMouse;
        Point dragStartLoc;

        // 字体（GDI HFONT，支持任意字重）
        IntPtr hFont;
        // 指示灯：内置色名 -> 解析好的矢量图标（来自 Noto Emoji SVG，按目标尺寸实时光栅化）
        readonly Dictionary<string, SvgIcon> icons = new Dictionary<string, SvgIcon>(StringComparer.OrdinalIgnoreCase);
        readonly Color colText = Color.White;

        // SVG 解析失败时的兜底纯色圆
        static readonly Dictionary<string, Color> fallbackColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            { "white",  Color.FromArgb(0xEE, 0xEE, 0xEE) },
            { "red",    Color.FromArgb(0xF4, 0x43, 0x36) },
            { "orange", Color.FromArgb(0xF7, 0x8C, 0x1F) },
            { "yellow", Color.FromArgb(0xFD, 0xD8, 0x35) },
            { "green",  Color.FromArgb(0x43, 0xA0, 0x47) },
            { "black",  Color.FromArgb(0x31, 0x37, 0x3D) },
            { "blue",   Color.FromArgb(0x1E, 0x88, 0xE5) },
            { "purple", Color.FromArgb(0x9C, 0x27, 0xB0) },
            { "brown",  Color.FromArgb(0x6D, 0x4C, 0x41) },
            { "hollow", Color.FromArgb(0xF4, 0x43, 0x36) },
        };
        readonly Color colGray = Color.FromArgb(0x9A, 0x9A, 0x9A);

        public WidgetForm()
        {
            cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            cfg = LoadConfig(cfgPath);
            if (cfg.Scale < 0.5) cfg.Scale = 1.0;
            hFont = CreateUiFont();

            // 加载指示灯矢量图标（assets 下的 Noto Emoji SVG；缺失/解析失败则该色回退为纯色圆）
            string adir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets");
            LoadIcon(adir, "white",  "emoji_u26aa.svg");  // U+26AA  白
            LoadIcon(adir, "red",    "emoji_u1f534.svg"); // U+1F534 红
            LoadIcon(adir, "orange", "emoji_u1f7e0.svg"); // U+1F7E0 橙
            LoadIcon(adir, "yellow", "emoji_u1f7e1.svg"); // U+1F7E1 黄
            LoadIcon(adir, "green",  "emoji_u1f7e2.svg"); // U+1F7E2 绿
            LoadIcon(adir, "black",  "emoji_u26ab.svg");  // U+26AB  黑
            LoadIcon(adir, "blue",   "emoji_u1f535.svg"); // U+1F535 蓝
            LoadIcon(adir, "purple", "emoji_u1f7e3.svg"); // U+1F7E3 紫
            LoadIcon(adir, "brown",  "emoji_u1f7e4.svg"); // U+1F7E4 棕
            LoadIcon(adir, "hollow", "emoji_u2b55.svg");  // U+2B55  空心红

            // 窗口外观：无边框、透明键、不进任务栏、置顶
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(1, 1, 1);     // 近黑，作为透明键
            TransparencyKey = Color.FromArgb(1, 1, 1);
            DoubleBuffered = true;
            Width = (int)(132 * cfg.Scale);
            Height = Math.Max((int)(28 * cfg.Scale), DotSize() + (int)(4 * cfg.Scale));

            BuildContextMenu();

            uiTimer.Interval = 200;
            uiTimer.Tick += (s, e) => OnTick();
            uiTimer.Start();

            StartHttpServer();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return cp;
            }
        }

        // 不抢焦点
        protected override bool ShowWithoutActivation { get { return true; } }

        void BuildContextMenu()
        {
            var menu = new ContextMenuStrip();
            var pause = new ToolStripMenuItem("暂停 / 恢复");
            pause.Click += (s, e) => { paused = !paused; Invalidate(); };
            var reposition = new ToolStripMenuItem("重新定位");
            reposition.Click += (s, e) => { cfg.OffsetX = 0; cfg.OffsetY = 0; SaveConfig(); Reposition(); };
            var exit = new ToolStripMenuItem("退出");
            exit.Click += (s, e) => Close();
            menu.Items.Add(pause);
            menu.Items.Add(reposition);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);
            ContextMenuStrip = menu;
        }

        // ===== HTTP 服务 =====
        void StartHttpServer()
        {
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + cfg.Port + "/");
                listener.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法监听 127.0.0.1:" + cfg.Port + "\n" + ex.Message,
                    "ClaudeStatus", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
                return;
            }
            listenThread = new Thread(ListenLoop) { IsBackground = true };
            listenThread.Start();
        }

        void ListenLoop()
        {
            while (running)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }
                try
                {
                    string evt = ctx.Request.Url.AbsolutePath.Trim('/');
                    string body = "";
                    using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                        body = sr.ReadToEnd();
                    HandleEvent(evt, body);
                    byte[] buf = Encoding.UTF8.GetBytes("ok");
                    ctx.Response.ContentLength64 = buf.Length;
                    ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                    ctx.Response.OutputStream.Close();
                }
                catch { /* 单个请求出错不影响服务 */ }
            }
        }

        // 直接正则抠字段，避免整体反序列化大 payload（含 tool_input 长命令）时偶发失败。
        static readonly Regex reSid = new Regex("\"session_id\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex reMsg = new Regex("\"message\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Compiled);

        static string ExtractSessionId(string body)
        {
            if (string.IsNullOrEmpty(body)) return "default";
            var m = reSid.Match(body);
            return m.Success ? m.Groups[1].Value : "default";
        }

        static string ExtractMessage(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            var m = reMsg.Match(body);
            return m.Success ? m.Groups[1].Value : "";
        }

        // Claude Code 在等待用户输入约 60s 时也会发 Notification（"... waiting for your input"）。
        // 这属于"轮到你"，不是需要紧急处理的告警，区别于权限请求。
        static bool IsIdleNotification(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return false;
            string m = msg.ToLowerInvariant();
            return m.Contains("waiting for your input") || m.Contains("waiting for input");
        }

        void Log(string line)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "events.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
            }
            catch { }
        }

        void HandleEvent(string evt, string body)
        {
            string sid = ExtractSessionId(body);
            string msg = ExtractMessage(body);
            DateTime now = DateTime.Now;
            if (cfg.LogEvents)
            {
                string raw = (body ?? "").Replace("\r", " ").Replace("\n", " ");
                if (raw.Length > 300) raw = raw.Substring(0, 300);
                Log(evt + "  sid=" + sid + (msg.Length > 0 ? "  msg=\"" + msg + "\"" : "") + "  body=" + raw);
            }
            lock (gate)
            {
                SessionInfo si;
                if (!sessions.TryGetValue(sid, out si))
                {
                    si = new SessionInfo { State = LightState.Idle, StateSince = now };
                    sessions[sid] = si;
                }
                si.LastSeen = now;

                switch (evt)
                {
                    case "UserPromptSubmit":
                    case "PreToolUse":
                    case "PostToolUse":
                        SetState(si, LightState.Running, now);
                        break;
                    case "Notification":
                        // 空闲提醒 -> 轮到你；其余（权限等）-> 需要注意
                        SetState(si, IsIdleNotification(msg) ? LightState.Waiting : LightState.Attention, now);
                        break;
                    case "Stop":
                    case "SubagentStop":
                        SetState(si, LightState.Waiting, now);
                        break;
                    case "SessionStart":
                        SetState(si, LightState.Idle, now);
                        break;
                    case "SessionEnd":
                        sessions.Remove(sid);
                        break;
                }
            }
        }

        static void SetState(SessionInfo si, LightState s, DateTime now)
        {
            if (si.State != s) { si.State = s; si.StateSince = now; }
        }

        // 计算全局聚合状态
        LightState Aggregate()
        {
            DateTime now = DateTime.Now;
            LightState best = LightState.None;
            lock (gate)
            {
                var dead = new List<string>();
                foreach (var kv in sessions)
                {
                    var si = kv.Value;
                    // 陈旧会话清理
                    if ((now - si.LastSeen).TotalSeconds > cfg.StaleSec) { dead.Add(kv.Key); continue; }
                    // 兜底：运行态长时间无任何事件（Stop 丢失/卡死）-> 降级为"轮到你"
                    if (si.State == LightState.Running &&
                        (now - si.LastSeen).TotalSeconds > cfg.RunningStaleSec)
                    { si.State = LightState.Waiting; si.StateSince = now; }
                    // 轮到你 -> 衰减为空闲
                    if (si.State == LightState.Waiting &&
                        (now - si.StateSince).TotalSeconds > cfg.WaitingDecaySec)
                        si.State = LightState.Idle;
                    if (si.State > best) best = si.State;
                }
                foreach (var k in dead) sessions.Remove(k);
            }
            return best;
        }

        // ===== 定时：定位 / 置顶 / 闪烁 / 全屏隐藏 =====
        void OnTick()
        {
            // 闪烁节拍
            if ((DateTime.Now - lastBlink).TotalMilliseconds >= cfg.BlinkMs)
            {
                blinkOn = !blinkOn;
                lastBlink = DateTime.Now;
            }

            // 全屏隐藏
            if (cfg.HideOnFullscreen && IsForegroundFullscreen())
            {
                if (Visible) Hide();
                return;
            }
            if (!Visible) Show();

            AutoSizeWidth();
            Reposition();
            // 持续重断言置顶（壳层有时会覆盖）
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            Invalidate();
        }

        bool IsForegroundFullscreen()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            // 桌面/壳层不算
            if (fg == FindWindow("Shell_TrayWnd", null)) return false;
            IntPtr progman = FindWindow("Progman", null);
            if (fg == progman) return false;
            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            var scr = Screen.FromHandle(fg).Bounds;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            return w >= scr.Width && h >= scr.Height;
        }

        // 当前聚合状态 -> 文字/是否闪烁/灯图标(及闪烁替图)/兜底色
        void StateVisual(out string text, out bool blink, out SvgIcon icon, out SvgIcon blinkIcon, out Color fallback)
        {
            LightState st = paused ? LightState.None : Aggregate();
            blink = false; blinkIcon = null;
            switch (st)
            {
                case LightState.Running:   // 黄，闪烁时与橙交替
                    text = cfg.TextRunning;   blink = true;
                    icon = IconByName(cfg.ColorRunning, "yellow"); blinkIcon = IconByName(cfg.ColorRunningBlink, "orange");
                    fallback = FallbackColor(cfg.ColorRunning, "yellow"); break;
                case LightState.Attention: // 红
                    text = cfg.TextAttention;
                    icon = IconByName(cfg.ColorAttention, "red");
                    fallback = FallbackColor(cfg.ColorAttention, "red"); break;
                case LightState.Waiting:   // 绿，闪烁时与黑交替
                    text = cfg.TextWaiting;   blink = true;
                    icon = IconByName(cfg.ColorWaiting, "green"); blinkIcon = IconByName(cfg.ColorWaitingBlink, "black");
                    fallback = FallbackColor(cfg.ColorWaiting, "green"); break;
                case LightState.Idle:      // 绿
                    text = cfg.TextIdle;
                    icon = IconByName(cfg.ColorIdle, "green");
                    fallback = FallbackColor(cfg.ColorIdle, "green"); break;
                default:                   // 白
                    text = paused ? "Paused" : cfg.TextNone;
                    icon = IconByName(cfg.ColorNone, "white");
                    fallback = colGray; break;
            }
        }

        // 按色名取图标；色名无效时用 def，仍无则返回 null（绘制时回退纯色圆）
        SvgIcon IconByName(string name, string def)
        {
            SvgIcon ic;
            if (!string.IsNullOrEmpty(name) && icons.TryGetValue(name, out ic)) return ic;
            if (icons.TryGetValue(def, out ic)) return ic;
            return null;
        }

        Color FallbackColor(string name, string def)
        {
            Color c;
            if (!string.IsNullOrEmpty(name) && fallbackColors.TryGetValue(name, out c)) return c;
            if (fallbackColors.TryGetValue(def, out c)) return c;
            return colGray;
        }

        // 指示灯直径（独立于字体缩放）
        int DotSize() { double ds = cfg.DotScale <= 0 ? 1.0 : cfg.DotScale; return (int)(12 * cfg.Scale * ds); }

        // 文字左起点（灯左边距 + 灯宽 + 间距），供绘制与自动宽度共用
        int TextLeft() { double s = cfg.Scale; return (int)(6 * s) + DotSize() + (int)(5 * s); }

        // 按当前文字自动计算所需窗口宽度
        int DesiredWidth()
        {
            string text; bool blink; SvgIcon icon, blinkIcon; Color fallback;
            StateVisual(out text, out blink, out icon, out blinkIcon, out fallback);
            return TextLeft() + MeasureText(text).cx + (int)(10 * cfg.Scale);
        }

        // 用当前 GDI 字体测量文字尺寸
        GSIZE MeasureText(string text)
        {
            GSIZE sz = new GSIZE();
            using (var g = CreateGraphics())
            {
                IntPtr hdc = g.GetHdc();
                IntPtr old = SelectObject(hdc, hFont);
                GetTextExtentPoint32(hdc, text, text.Length, out sz);
                SelectObject(hdc, old);
                g.ReleaseHdc(hdc);
            }
            return sz;
        }

        // 自动调整窗口宽度（右边缘锚定，故宽度变化只向左生长）
        void AutoSizeWidth()
        {
            int w = DesiredWidth();
            if (w != Width) Width = w;
        }

        // 定位：右边缘锚定在托盘图标区左侧固定间距处
        void Reposition()
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;
            RECT tr;
            if (!GetWindowRect(tray, out tr)) return;
            int taskTop = tr.Top, taskBottom = tr.Bottom;
            int taskHeight = taskBottom - taskTop;

            // 托盘通知区左边界
            int trayLeft = tr.Right;
            IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero)
            {
                RECT nr;
                if (GetWindowRect(notify, out nr)) trayLeft = nr.Left;
            }

            int rightEdge = trayLeft - cfg.RightGap + cfg.OffsetX;
            int x = rightEdge - Width;
            int y = taskTop + (taskHeight - Height) / 2 + cfg.OffsetY;

            if (Left != x || Top != y)
                SetWindowPos(Handle, HWND_TOPMOST, x, y, Width, Height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        // ===== 绘制 =====
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(TransparencyKey);

            string text;
            bool blink;
            SvgIcon icon, blinkIcon;
            Color fallback;
            StateVisual(out text, out blink, out icon, out blinkIcon, out fallback);

            // 灯
            double s = cfg.Scale;
            int d = DotSize();
            int cx = (int)(6 * s), cy = (Height - d) / 2;
            var box = new Rectangle(cx, cy, d, d);
            bool off = blink && !blinkOn;

            // 闪烁：有替色（黄<->橙、绿<->黑）则换图标；否则同色降低不透明度兜底
            if (off)
            {
                if (blinkIcon != null) blinkIcon.Draw(g, box, 1.0f);
                else if (icon != null) icon.Draw(g, box, 0.40f);
                else DrawFallback(g, box, Darken(fallback, 0.30));
            }
            else
            {
                if (icon != null) icon.Draw(g, box, 1.0f);
                else DrawFallback(g, box, fallback);
            }

            // 文字（GDI，吃 lfWeight 字重）
            if (hFont != IntPtr.Zero && !string.IsNullOrEmpty(text))
            {
                IntPtr hdc = g.GetHdc();
                IntPtr old = SelectObject(hdc, hFont);
                SetBkMode(hdc, TRANSPARENT);
                SetTextColor(hdc, ColorTranslator.ToWin32(colText));
                GSIZE sz; GetTextExtentPoint32(hdc, text, text.Length, out sz);
                int ty = (Height - sz.cy) / 2;
                TextOut(hdc, TextLeft(), ty, text, text.Length);
                SelectObject(hdc, old);
                g.ReleaseHdc(hdc);
            }
        }

        static void DrawFallback(Graphics g, Rectangle box, Color c)
        {
            using (var b = new SolidBrush(c)) g.FillEllipse(b, box);
        }

        static Color Darken(Color c, double f)
        {
            return Color.FromArgb(c.A,
                (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
        }

        // ===== 拖动微调位置 =====
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                dragStartMouse = Cursor.Position;
                dragStartLoc = new Point(Left, Top);
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                Point now = Cursor.Position;
                cfg.OffsetX += (now.X - dragStartMouse.X);
                cfg.OffsetY += (now.Y - dragStartMouse.Y);
                dragStartMouse = now;
                Reposition();
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging) { dragging = false; SaveConfig(); }
            base.OnMouseUp(e);
        }

        // ===== 生命周期 =====
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            Reposition();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
            try { if (hFont != IntPtr.Zero) DeleteObject(hFont); } catch { }
            base.OnFormClosing(e);
        }

        // ===== 配置读写 =====
        static Config LoadConfig(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var s = new JavaScriptSerializer();
                    var c = s.Deserialize<Config>(File.ReadAllText(path));
                    if (c != null) return c;
                }
            }
            catch { }
            return new Config();
        }

        void SaveConfig()
        {
            try
            {
                var s = new JavaScriptSerializer();
                File.WriteAllText(cfgPath, s.Serialize(cfg));
            }
            catch { }
        }

        // 按 config 构建 GDI 字体（lfWeight 直接吃 100-900 的字重）
        IntPtr CreateUiFont()
        {
            string fam = string.IsNullOrEmpty(cfg.FontName) ? "Segoe UI" : cfg.FontName;
            int weight = cfg.FontWeight;
            if (weight < 1) weight = 400;
            if (weight > 1000) weight = 1000;
            int dpiY;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpiY = (int)g.DpiY;
            float pt = (float)(9.0 * cfg.Scale);
            var lf = new LOGFONT
            {
                lfHeight = -(int)Math.Round(pt * dpiY / 72.0),
                lfWeight = weight,
                lfCharSet = DEFAULT_CHARSET,
                lfQuality = CLEARTYPE_QUALITY,
                lfFaceName = fam
            };
            return CreateFontIndirect(ref lf);
        }

        // 解析一个 SVG 文件，成功则以色名存入 icons
        void LoadIcon(string dir, string name, string file)
        {
            var ic = SvgIcon.Load(Path.Combine(dir, file));
            if (ic != null) icons[name] = ic;
        }
    }

    // ===== 极简 SVG 渲染器 =====
    // 仅支持本项目用到的 Noto Emoji 圆形图标：<circle> 与 <path>（M/L/H/V/C/S/Q/T/Z，
    // 含相对命令），fill / opacity / fill-opacity 样式。按目标尺寸实时矢量光栅化，任意
    // 缩放都清晰；解析失败返回 null，由调用方回退纯色圆。
    sealed class SvgIcon
    {
        sealed class Shape { public GraphicsPath Path; public Color Fill; public float Opacity; }
        readonly List<Shape> shapes = new List<Shape>();
        float viewW = 128f, viewH = 128f;

        public static SvgIcon Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string svg = File.ReadAllText(path);
                var icon = new SvgIcon();

                var vb = Regex.Match(svg, "viewBox\\s*=\\s*\"\\s*[-\\d.]+\\s+[-\\d.]+\\s+([-\\d.]+)\\s+([-\\d.]+)");
                if (vb.Success)
                {
                    icon.viewW = ParseF(vb.Groups[1].Value, 128f);
                    icon.viewH = ParseF(vb.Groups[2].Value, 128f);
                }

                // 按文档顺序匹配 <circle .../> 与 <path .../>
                foreach (Match m in Regex.Matches(svg, "<(circle|path)\\b([^>]*?)/?>", RegexOptions.Singleline))
                {
                    string tag = m.Groups[1].Value;
                    string attrs = m.Groups[2].Value;
                    GraphicsPath gp = tag == "circle" ? CircleToPath(attrs) : PathToPath(Attr(attrs, "d"));
                    if (gp == null) continue;
                    Color fill; float op;
                    ParseStyle(attrs, out fill, out op);
                    icon.shapes.Add(new Shape { Path = gp, Fill = fill, Opacity = op });
                }
                return icon.shapes.Count > 0 ? icon : null;
            }
            catch { return null; }
        }

        public void Draw(Graphics g, Rectangle box, float globalAlpha)
        {
            var saved = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(box.X, box.Y);
            g.ScaleTransform(box.Width / viewW, box.Height / viewH);
            foreach (var sh in shapes)
            {
                int a = (int)Math.Round(255 * sh.Opacity * globalAlpha);
                if (a <= 0) continue;
                if (a > 255) a = 255;
                using (var b = new SolidBrush(Color.FromArgb(a, sh.Fill)))
                    g.FillPath(b, sh.Path);
            }
            g.Restore(saved);
        }

        // ---- 属性 / 样式 ----
        static string Attr(string attrs, string name)
        {
            var m = Regex.Match(attrs, name + "\\s*=\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        static void ParseStyle(string attrs, out Color fill, out float opacity)
        {
            fill = Color.Black; opacity = 1f;          // SVG 默认填充黑、不透明
            string style = Attr(attrs, "style") ?? "";
            string fAttr = Attr(attrs, "fill");
            if (fAttr != null) { Color c; if (TryColor(fAttr, out c)) fill = c; }
            float fillOp = 1f;
            foreach (var decl in style.Split(';'))
            {
                int k = decl.IndexOf(':');
                if (k <= 0) continue;
                string key = decl.Substring(0, k).Trim().ToLowerInvariant();
                string val = decl.Substring(k + 1).Trim();
                if (key == "fill") { Color c; if (TryColor(val, out c)) fill = c; }
                else if (key == "opacity") opacity = ParseF(val, 1f);
                else if (key == "fill-opacity") fillOp = ParseF(val, 1f);
            }
            opacity *= fillOp;
        }

        static bool TryColor(string v, out Color c)
        {
            c = Color.Black;
            if (string.IsNullOrEmpty(v)) return false;
            v = v.Trim();
            if (v == "none") return false;
            if (v[0] == '#')
            {
                string h = v.Substring(1);
                if (h.Length == 3) h = "" + h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
                if (h.Length >= 6)
                {
                    int r, g, b;
                    if (int.TryParse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                        int.TryParse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                        int.TryParse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
                    { c = Color.FromArgb(r, g, b); return true; }
                }
                return false;
            }
            try { c = Color.FromName(v); return c.A != 0 || v.Equals("transparent", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        static GraphicsPath CircleToPath(string attrs)
        {
            float cx = ParseF(Attr(attrs, "cx"), 0f);
            float cy = ParseF(Attr(attrs, "cy"), 0f);
            float r = ParseF(Attr(attrs, "r"), 0f);
            if (r <= 0) return null;
            var gp = new GraphicsPath();
            gp.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
            return gp;
        }

        static float ParseF(string s, float def)
        {
            float f;
            return !string.IsNullOrEmpty(s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : def;
        }

        // ---- SVG path 数据 -> GraphicsPath ----
        static GraphicsPath PathToPath(string d)
        {
            if (string.IsNullOrEmpty(d)) return null;
            var toks = new List<string>();
            foreach (Match m in Regex.Matches(d, "[MmLlHhVvCcSsQqTtAaZz]|[-+]?(?:\\d*\\.\\d+|\\d+\\.?)(?:[eE][-+]?\\d+)?"))
                if (m.Value.Length > 0) toks.Add(m.Value);

            var gp = new GraphicsPath();
            int i = 0;
            float cx = 0, cy = 0, sx = 0, sy = 0; // 当前点 / 子路径起点
            float pcx = 0, pcy = 0;               // 上一三次贝塞尔控制点（用于 S）
            float pqx = 0, pqy = 0;               // 上一二次贝塞尔控制点（用于 T）
            char cmd = ' ', prev = ' ';
            bool open = false;

            while (i < toks.Count)
            {
                string t = toks[i];
                if (char.IsLetter(t[0])) { cmd = t[0]; i++; }
                else if (cmd == 'M') cmd = 'L';   // M 后续隐式坐标按 L 处理
                else if (cmd == 'm') cmd = 'l';
                if (cmd == ' ') { i++; continue; }

                bool rel = char.IsLower(cmd);
                char C = char.ToUpper(cmd);
                try
                {
                    switch (C)
                    {
                        case 'M':
                        {
                            float x = Num(toks, ref i), y = Num(toks, ref i);
                            if (rel) { x += cx; y += cy; }
                            cx = sx = x; cy = sy = y;
                            gp.StartFigure(); open = true;
                            break;
                        }
                        case 'L':
                        {
                            float x = Num(toks, ref i), y = Num(toks, ref i);
                            if (rel) { x += cx; y += cy; }
                            gp.AddLine(cx, cy, x, y); cx = x; cy = y;
                            break;
                        }
                        case 'H':
                        {
                            float x = Num(toks, ref i); if (rel) x += cx;
                            gp.AddLine(cx, cy, x, cy); cx = x;
                            break;
                        }
                        case 'V':
                        {
                            float y = Num(toks, ref i); if (rel) y += cy;
                            gp.AddLine(cx, cy, cx, y); cy = y;
                            break;
                        }
                        case 'C':
                        {
                            float x1 = Num(toks, ref i), y1 = Num(toks, ref i);
                            float x2 = Num(toks, ref i), y2 = Num(toks, ref i);
                            float x = Num(toks, ref i), y = Num(toks, ref i);
                            if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                            gp.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                            pcx = x2; pcy = y2; cx = x; cy = y;
                            break;
                        }
                        case 'S':
                        {
                            float x2 = Num(toks, ref i), y2 = Num(toks, ref i);
                            float x = Num(toks, ref i), y = Num(toks, ref i);
                            if (rel) { x2 += cx; y2 += cy; x += cx; y += cy; }
                            float x1 = (prev == 'C' || prev == 'S') ? 2 * cx - pcx : cx;
                            float y1 = (prev == 'C' || prev == 'S') ? 2 * cy - pcy : cy;
                            gp.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                            pcx = x2; pcy = y2; cx = x; cy = y;
                            break;
                        }
                        case 'Q':
                        {
                            float qx = Num(toks, ref i), qy = Num(toks, ref i);
                            float x = Num(toks, ref i), y = Num(toks, ref i);
                            if (rel) { qx += cx; qy += cy; x += cx; y += cy; }
                            AddQuad(gp, cx, cy, qx, qy, x, y);
                            pqx = qx; pqy = qy; cx = x; cy = y;
                            break;
                        }
                        case 'T':
                        {
                            float x = Num(toks, ref i), y = Num(toks, ref i);
                            if (rel) { x += cx; y += cy; }
                            float qx = (prev == 'Q' || prev == 'T') ? 2 * cx - pqx : cx;
                            float qy = (prev == 'Q' || prev == 'T') ? 2 * cy - pqy : cy;
                            AddQuad(gp, cx, cy, qx, qy, x, y);
                            pqx = qx; pqy = qy; cx = x; cy = y;
                            break;
                        }
                        case 'A':
                        {
                            // 椭圆弧：本项目素材未用到，读掉 7 个参数后以直线连到终点兜底
                            Num(toks, ref i); Num(toks, ref i); Num(toks, ref i);
                            Num(toks, ref i); Num(toks, ref i);
                            float x = Num(toks, ref i), y = Num(toks, ref i);
                            if (rel) { x += cx; y += cy; }
                            gp.AddLine(cx, cy, x, y); cx = x; cy = y;
                            break;
                        }
                        case 'Z':
                        {
                            if (open) { gp.CloseFigure(); open = false; }
                            cx = sx; cy = sy;
                            break;
                        }
                    }
                }
                catch { break; } // token 不足等异常：停止解析，保留已构建部分
                prev = C;
            }
            return gp;
        }

        static void AddQuad(GraphicsPath gp, float x0, float y0, float qx, float qy, float x, float y)
        {
            // 二次贝塞尔升阶为三次
            float c1x = x0 + 2f / 3f * (qx - x0), c1y = y0 + 2f / 3f * (qy - y0);
            float c2x = x + 2f / 3f * (qx - x), c2y = y + 2f / 3f * (qy - y);
            gp.AddBezier(x0, y0, c1x, c1y, c2x, c2y, x, y);
        }

        static float Num(List<string> toks, ref int i)
        {
            if (i >= toks.Count) throw new FormatException();
            string t = toks[i];
            if (char.IsLetter(t[0])) throw new FormatException(); // 参数不足，遇到下一个命令
            i++;
            return float.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
