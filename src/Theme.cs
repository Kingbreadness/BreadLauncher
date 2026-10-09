// Theme.cs —— 颜色/字体/圆角/亚克力 等视觉基础设施
//
// 目标：看着像 Windows 11 开始菜单的深色版。所有尺寸都乘 DPI 系数，125% 缩放下不会糊也不会错位。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BreadLauncher
{
    public static class Theme
    {
        public static readonly Color BgTop = Color.FromArgb(38, 38, 38);
        public static readonly Color BgBottom = Color.FromArgb(28, 28, 28);
        public static readonly Color Surface = Color.FromArgb(48, 48, 48);
        public static readonly Color SurfaceHover = Color.FromArgb(58, 58, 58);
        public static readonly Color Hover = Color.FromArgb(48, 48, 48);
        public static readonly Color HoverStrong = Color.FromArgb(60, 60, 60);
        public static readonly Color TextPrimary = Color.FromArgb(255, 255, 255);
        public static readonly Color TextSecondary = Color.FromArgb(205, 205, 205);
        public static readonly Color TextDim = Color.FromArgb(150, 150, 150);
        public static readonly Color Accent = Color.FromArgb(96, 205, 255);
        public static readonly Color Separator = Color.FromArgb(58, 58, 58);
        public static readonly Color Border = Color.FromArgb(64, 64, 64);

        /// <summary>亚克力模式下，这个颜色会被系统当成「透明」，用来透出后面的模糊。</summary>
        public static readonly Color TransparentKey = Color.FromArgb(20, 20, 20);

        // ---------------- 字体 ----------------

        private static string _uiFamily;
        private static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>();
        private static readonly object FontLock = new object();

        public static string UiFamily
        {
            get
            {
                if (_uiFamily == null) _uiFamily = ResolveFamily();
                return _uiFamily;
            }
        }

        private static string ResolveFamily()
        {
            try
            {
                string[] want = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };
                List<string> have = new List<string>();
                foreach (FontFamily f in FontFamily.Families) have.Add(f.Name);
                foreach (string w in want)
                    if (have.Contains(w)) return w;
            }
            catch { }
            return "Segoe UI";
        }

        public static Font Ui(float size, FontStyle style)
        {
            string key = "ui|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (int)style;
            lock (FontLock)
            {
                Font f;
                if (FontCache.TryGetValue(key, out f)) return f;
                f = new Font(UiFamily, size, style, GraphicsUnit.Point);
                FontCache[key] = f;
                return f;
            }
        }

        public static Font Ui(float size) { return Ui(size, FontStyle.Regular); }

        /// <summary>Segoe MDL2 Assets 里的图标字形字体（齿轮、加号、关闭叉等）。</summary>
        public static Font Glyph(float size)
        {
            string key = "glyph|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lock (FontLock)
            {
                Font f;
                if (FontCache.TryGetValue(key, out f)) return f;
                string family = "Segoe MDL2 Assets";
                try
                {
                    using (Font test = new Font(family, size)) { }
                }
                catch { family = UiFamily; }
                f = new Font(family, size, FontStyle.Regular, GraphicsUnit.Point);
                FontCache[key] = f;
                return f;
            }
        }

        // ---------------- 缩放与绘制 ----------------

        /// <summary>绘制异常日志去重用的「上一次记了什么」（见 PaintCatch）。</summary>
        private static string _lastPaintErrorLogged;

        /// <summary>
        /// 绘制异常的**统一兜底**：WinForms 遇到 OnPaint 里抛异常会把整块客户区画成
        /// 「白底 + 红色大叉」，用户只会说「报错了」，一点线索都没有。
        /// 这里统一：① 画一个朴素底色（不吓人）② 把类型 / 消息 / 堆栈写进日志。
        /// ★规矩（AGENTS.md 坑 6）：**每个 OnPaint 都要挂这个，改绘制代码时别把它去掉。**
        /// ★为什么做成公共函数：以前 7 个 OnPaint 里只有 2 个自带 try/catch，其余 5 个裸奔 ——
        ///   分散复制粘贴必然会漏，集中一处就不会。
        /// ★为什么按「位置 + 异常摘要」去重：绘制是每帧跑的，同一个异常会**每帧写一条日志**
        ///   （实测连打 200 次 = 32KB），几分钟就把日志刷满、还会让 1MB 轮转反复删 log.1.txt，
        ///   把真正有用的历史挤掉。同一条只记第一次，其他不同的仍然照记。
        /// </summary>
        public static void PaintCatch(Control c, PaintEventArgs e, string what, Exception ex)
        {
            try
            {
                if (e != null && e.Graphics != null && c != null)
                {
                    using (SolidBrush b = new SolidBrush(BgBottom))
                        e.Graphics.FillRectangle(b, c.ClientRectangle);
                }
            }
            catch (Exception) { }
            try
            {
                if (Program.AppDir != null && ex != null)
                {
                    string mid = (what ?? "") + "|" + ex.GetType().Name + "|" + ex.Message;
                    if (_lastPaintErrorLogged != mid)
                    {
                        _lastPaintErrorLogged = mid;
                        ConfigStore.Log(Program.AppDir, "★绘制异常（界面本来会变成白底红叉）"
                            + (string.IsNullOrEmpty(what) ? "" : " @ " + what) + "："
                            + ex.GetType().Name + "：" + ex.Message + "  @  " + ex.StackTrace
                            + "   [同一条只记第一次，后面重复的不再刷日志]");
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>纯换算：把「96 DPI 下的设计值」换成某个 DPI 下的像素数。
        /// ★单独抽出来是为了**能被探针直接断言**（96→原值、120→×1.25、144→×1.5）——
        ///   它不依赖运行环境，所以在探针里也验得动（见 `docs\dev-notes.md` 坑 50）。</summary>
        public static int PxFor(int dpi, double v)
        {
            if (dpi <= 0) dpi = 96;
            return (int)Math.Round(v * dpi / 96.0, MidpointRounding.AwayFromZero);
        }

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

        /// <summary>按控件记住算过的 DPI（弱引用，控件没了自动回收；不会随着开窗口越攒越多）。</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> DpiMemo
            = new System.Runtime.CompilerServices.ConditionalWeakTable<Control, object>();

        /// <summary>★★**真实 DPI** —— 别用 `Control.DeviceDpi`！
        /// .NET Framework 的 WinForms 只有在 exe 旁边放一份 `app.config`（写 `DpiAwareness`）时才会认高 DPI，
        /// 而本工程是**单文件**产品（拷一个 exe 就能用、不能带 .config）→ `DeviceDpi` **永远返回 96**：
        /// 所有 `Px()` 都按 100% 算，而字体是按**磅**画的、GDI+ 早就按真实 DPI 放大了 → 于是「框小、字大」：
        /// 图标显得小、底栏文字被截断。**这就是用户 2026-10-07 报的「换回笔记本屏后好小」的根因。**
        /// 实测（本机 150% 缩放）：`GetDpiForSystem()`=144、`GetDpiForWindow()`=144，而 `DeviceDpi`=96。
        /// ⚠ 运行时改不了：`SetProcessDpiAwarenessContext` 会被清单挡住（实测返回 False / 错误 5），
        ///   `SetThreadDpiAwarenessContext` 能提升线程感知、但 WinForms 照样守着 96（实测过）。
        /// 顺序：窗口 DPI → 系统 DPI → 96（前两个在**没有 DPI 清单**的进程里都会返回 96 —— 探针就是这种，
        ///   所以探针的渲染口径完全不变，那 795 条像素断言的数字依然算数）。</summary>
        public static int RealDpi(Control c)
        {
            if (c != null)
            {
                object hit;
                if (DpiMemo.TryGetValue(c, out hit)) return (int)hit;
            }
            int dpi = QueryDpi(c);
            if (c != null)
            {
                try { DpiMemo.Add(c, dpi); } catch (ArgumentException) { }
            }
            return dpi;
        }

        private static int QueryDpi(Control c)
        {
            try
            {
                if (c != null && c.IsHandleCreated)
                {
                    uint d = GetDpiForWindow(c.Handle);
                    if (d > 0) return (int)d;
                }
            }
            catch (Exception) { }
            try { uint s = GetDpiForSystem(); if (s > 0) return (int)s; }
            catch (Exception) { }
            return 96;
        }

        public static int Px(Control c, double v)
        {
            return PxFor(RealDpi(c), v);
        }

        // ★这里曾经有个 `SchemeCursor(role, fallback)` / `HandCursor()`（读用户指针方案里的手型），
        //   2026-10-09 用户拍板**不要换鼠标指针**之后删掉了 —— 两版都被他否掉：
        //   ① `Cursors.Hand`（Windows 通用手型）墨迹只占画布 36%，他看着"很小"；
        //   ② 读他自己方案里那只手（`HKCU\Control Panel\Cursors\Hand`，一只暗色的"弩"）更糟：
        //      不透明区域平均亮度只有 65/255，落在深色面板上就是"黑乎乎一团"（用户原话「怎么是黑色的」）。
        //   ⇒ 面板上一切保持系统默认箭头。**别再把这套加回来**；真要加先问用户（见 docs\dev-notes.md 坑 51）。

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return p;
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color color)
        {
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(color))
                g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Rectangle r, int radius, Color color, float width)
        {
            Rectangle rr = new Rectangle(r.X, r.Y, Math.Max(1, r.Width - 1), Math.Max(1, r.Height - 1));
            using (GraphicsPath p = Round(rr, radius))
            using (Pen pen = new Pen(color, width))
                g.DrawPath(pen, p);
        }

        /// <summary>
        /// 统一画文字（NoPrefix + EndEllipsis）。
        /// ★★**自己夹一次裁剪区**：`TextRenderer` 走的是 GDI，而 GDI **不认 GDI+ 的裁剪区**
        ///   （`Graphics.SetClip` 之后它照样把字画到裁剪区外面去）。实测：面板分组区设了裁剪，
        ///   屏幕外的空格子照样在页脚上画出一个「加号」—— 用户就是这么发现的。
        ///   这里完全落在裁剪区外就不画，部分相交就用相交矩形（宁可少画一点，也不许画到外面去）。
        /// </summary>
        public static void DrawText(Graphics g, string text, Font font, Rectangle rect, Color color,
                                    TextFormatFlags extra)
        {
            if (ClampToClip(g, ref rect) == false) return;
            TextRenderer.DrawText(g, text ?? string.Empty, font, rect, color,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | extra);
        }

        /// <summary>
        /// 画**可折行**的文字（小图标下面那种「名字常驻」标签用；`DrawText` 带的是 EndEllipsis，
        /// 只会截成一行）。裁剪区照样自己夹（原因见 DrawText 的说明）。
        /// </summary>
        public static void DrawTextWrapped(Graphics g, string text, Font font, Rectangle rect, Color color,
                                           TextFormatFlags extra)
        {
            if (g == null) return;
            if (ClampToClip(g, ref rect) == false) return;
            TextRenderer.DrawText(g, text ?? string.Empty, font, rect, color,
                TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | extra);
        }

        /// <summary>
        /// 画一个字（字形 / 勾 / 首字母这类**单个符号**）：和 <see cref="DrawText"/> 一样自己夹裁剪，
        /// 但**不带 EndEllipsis**（单字符用不上，带上反而会改变度量）。
        /// ★为什么必须有这个：这些调用点原来直接调 `TextRenderer`，而 **GDI 不认 GDI+ 的裁剪区** ——
        ///   2026-09-27 出图实测：面板高度 772~802 时，被下沿切掉的格子里那张**首字母色块（GDI+）
        ///   被裁得一个像素不剩，字母却照画**，于是底栏上方那条空白带里浮出三个孤立字母
        ///   （O x∈[289,308] y∈[750,771] 等，逐像素扫描确认）。这是当年「页脚冒出游离加号」的同族。
        /// </summary>
        public static void DrawTextRaw(Graphics g, string text, Font font, Rectangle rect, Color color,
                                       TextFormatFlags flags)
        {
            if (g == null) return;
            if (ClampToClip(g, ref rect) == false) return;
            TextRenderer.DrawText(g, text ?? string.Empty, font, rect, color,
                TextFormatFlags.NoPrefix | flags);
        }

        /// <summary>把 rect 夹进当前裁剪区；整块在外返回 false（GDI 不认 GDI+ 裁剪，得自己来）。</summary>
        private static bool ClampToClip(Graphics g, ref Rectangle rect)        {
            RectangleF vis = g.VisibleClipBounds;
            if (vis.Width <= 0 || vis.Height <= 0) return false;
            if (rect.Right <= vis.Left || rect.Left >= vis.Right || rect.Bottom <= vis.Top || rect.Top >= vis.Bottom)
                return false;
            int x1 = Math.Max(rect.Left, (int)Math.Floor(vis.Left));
            int y1 = Math.Max(rect.Top, (int)Math.Floor(vis.Top));
            int x2 = Math.Min(rect.Right, (int)Math.Ceiling(vis.Right));
            int y2 = Math.Min(rect.Bottom, (int)Math.Ceiling(vis.Bottom));
            if (x2 > x1 && y2 > y1 && (x1 != rect.Left || y1 != rect.Top || x2 != rect.Right || y2 != rect.Bottom))
                rect = new Rectangle(x1, y1, x2 - x1, y2 - y1);
            return true;
        }

        // ---------------- 亚克力（可选，默认关闭） ----------------

        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        private const int ACCENT_ENABLE_BLURBEHIND = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>
        /// 打开/关闭亚克力半透明。**返回实际生效的观感**：true = 背景真的透出桌面（窗口已挖空），
        /// false = 不透明（关掉了，或者系统既不支持亚克力也不支持模糊 —— 这时**主动退回不透明**）。
        /// ★为什么要有这个返回值：亚克力默认开启，而在老系统上"挖空成功、模糊失败"会得到一个
        ///   **完全透明、什么都没有**的破面板（只剩图标浮在桌面上）。宁可不透明，也不要破观感。
        /// </summary>
        public static bool ApplyBackdrop(Form f, bool acrylic)
        {
            if (f == null || !f.IsHandleCreated) return false;
            try
            {
                if (!acrylic)
                {
                    f.BackColor = BgBottom;
                    f.TransparencyKey = Color.Empty;
                    return false;
                }

                // Windows 的模糊只能生效在「透明像素」上：把整个窗体底色设成同一个 key 色，
                // 这个颜色会被系统挖空，后面的桌面/窗口模糊就透出来了。
                f.BackColor = TransparentKey;
                f.TransparencyKey = TransparentKey;

                AccentPolicy policy = new AccentPolicy();
                policy.AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND;
                // AABBGGRR：0xCC 透明度 + 深灰底色
                policy.GradientColor = unchecked((int)0xCC202020);
                policy.AccentFlags = 2;

                WindowCompositionAttributeData data = new WindowCompositionAttributeData();
                data.Attribute = WCA_ACCENT_POLICY;
                data.SizeOfData = Marshal.SizeOf(typeof(AccentPolicy));
                data.Data = Marshal.AllocHGlobal(data.SizeOfData);
                try
                {
                    Marshal.StructureToPtr(policy, data.Data, false);
                    // ★★**别拿这个 API 的返回值判成败**：它的返回值在不同 Windows 上不一致
                    //   （本机实测成功时返回 1，而很多资料里写成功返回 0）—— 按返回值判断会出现
                    //   「任何机器都开不了亚克力」的惨案（这个坑我自己踩过一次）。
                    //   规矩：**只要调用没抛异常，就认为生效**（用户拍板：别管那个兜底隐患，直接生效）。
                    SetWindowCompositionAttribute(f.Handle, ref data);
                    return true;
                }
                catch
                {
                    // 连调用本身都失败：尽力补一发普通的模糊；还是不行才退回不透明
                    try
                    {
                        policy.AccentState = ACCENT_ENABLE_BLURBEHIND;
                        Marshal.StructureToPtr(policy, data.Data, false);
                        SetWindowCompositionAttribute(f.Handle, ref data);
                        return true;
                    }
                    catch { }
                    f.BackColor = BgBottom;
                    f.TransparencyKey = Color.Empty;
                    return false;
                }
                finally
                {
                    Marshal.FreeHGlobal(data.Data);
                }
            }
            catch
            {
                try { f.BackColor = BgBottom; f.TransparencyKey = Color.Empty; } catch { }
                return false;
            }
        }

        // ---------------- 图标取不到时的「首字母色块」 ----------------

        private static readonly Color[] TilePalette = new Color[]
        {
            Color.FromArgb(0x4C, 0x8B, 0xF5), Color.FromArgb(0xE0, 0x6C, 0x75), Color.FromArgb(0x81, 0xC7, 0x84),
            Color.FromArgb(0xFF, 0xB7, 0x4D), Color.FromArgb(0xBA, 0x68, 0xC8), Color.FromArgb(0x4D, 0xD0, 0xE1),
            Color.FromArgb(0xFF, 0x8A, 0x65), Color.FromArgb(0xA1, 0x88, 0x7F)
        };

        /// <summary>首字母：英文/数字取大写首字符，中文就取第一个字。</summary>
        public static string Initial(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            string s = name.Trim();
            if (s.Length == 0) return "?";
            char c = s[0];
            if (c >= 'a' && c <= 'z') c = (char)(c - 32);
            return c.ToString();
        }

        /// <summary>
        /// 图标实在取不到时的兜底：按名字取一个颜色，画圆角块 + 首字母。
        /// 比系统那张「未知文件类型」的白纸好看，也更容易一眼认出是哪个软件。
        /// </summary>
        public static void DrawAppTile(Graphics g, Rectangle r, string name, int radius)
        {
            string safe = name == null ? string.Empty : name;
            string letter = Initial(safe);
            int h = 17;
            for (int i = 0; i < safe.Length; i++) h = unchecked(h * 31 + safe[i]);
            // ★`& 0x7FFFFFFF` 把符号位抹掉，保证下标一定落在 [0, 长度)：
            //   旧写法是 `if (h < 0) h = -h;`，碰上 int.MinValue 时 `-int.MinValue` 还是负数，
            //   取模结果是负的 → 数组越界。现在**只**能靠「8 个颜色恰好整除 2^31」侥幸不崩
            //   （int.MinValue % 8 == 0），哪天调色板加成 9 色就会当场崩。
            Color c = TilePalette[(h & 0x7FFFFFFF) % TilePalette.Length];
            FillRound(g, r, radius, c);
            float fs = Math.Max(7f, r.Height * 0.44f);
            // ★走 DrawTextRaw（内部夹裁剪）：直接调 TextRenderer 的话，被下沿切掉的格子里
            //   色块会被 GDI+ 裁光、字母却照画 → 底栏上方浮出孤立字母（2026-09-27 出图实测过）。
            DrawTextRaw(g, letter, Ui(fs, FontStyle.Bold), r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ---------------- 滚动度量（三处共用，别再各写各的） ----------------

        /// <summary>
        /// 滚动相关的**唯一**数字来源。主面板分组区、应用列表（选择器 / 查看全部）、滚轮步长
        /// 三处都从这里取 —— 以前是各写各的：墨水 3px vs 4px、最小滑块 36 vs 28、
        /// 滚轮 60px（写死）vs 120px（拿 Delta 当像素用），同一个程序里就能看出不一致。
        /// ★改滚动一律改这里，改完跑探针（有像素级断言钉着两边的滑块位置）。
        /// </summary>
        public static class Scroll
        {
            /// <summary>滑块墨水宽度（逻辑像素）。</summary>
            public const double Ink = 4;
            /// <summary>墨水右缘距控件右缘的距离。</summary>
            public const double Margin = 3;
            /// <summary>行右缘与滚动条之间再留的空（行别顶到墨水上）。</summary>
            public const double BarClearance = 3;
            /// <summary>滑块最小高度：内容再长也不能细成一根针。</summary>
            public const double MinThumb = 32;
            /// <summary>轨道上下留白。</summary>
            public const double TrackPad = 4;
            /// <summary>命中区在墨水之外留的容错（4px 的墨水太细，鼠标点不准）。</summary>
            public const double HitSlack = 1;
            /// <summary>「一格滚轮」= 120（Windows 的 WM_MOUSEWHEEL 整格）。碎 Delta 都按它折算。</summary>
            public const int Notch = 120;
            /// <summary>框选时贴边自动滚动：定时器周期(ms) / 每拍滚多少逻辑像素 / 触发带高度。</summary>
            public const int AutoScrollInterval = 30;
            public const double AutoScrollStep = 6;
            public const double AutoScrollEdge = 26;
            /// <summary>应用列表：一格滚轮 = 3 行（Windows 列表的标准手感）。</summary>
            public const double ListRowsPerNotch = 3;
            /// <summary>面板分组区：整格滚轮 = 1 行（行距 = TileH + GapY）。
            /// ★注意：只有**整格（120）**才保证停在整行上；碎 Delta（触控板）按比例走、会停在半行 ——
            ///   这是为了让触控板跟手，别把注释写成「永远停在整行上」（做不到）。</summary>
            public const double GridRowsPerNotch = 1;

            /// <summary>应用列表的行高（BuildRows / 窗口高度 / 滚轮步长都用它 —— 以前 42 在三个文件里各写一遍）。</summary>
            public const double ListRow = 42;
            /// <summary>网格模式（图标墙）的格子高。</summary>
            public const double GridCell = 92;

            // 第三处滚动指示：文件夹「第几页」的小点（大文件夹超过 9 个时才出现）。
            // 它和滚动条同属"位置指示"，数字也放这里，但**颜色故意比滚动条亮**
            // （小点只有 3px，用滚动条那种 58 的透明度根本看不见；当前页则是纯白）。
            /// <summary>小点直径 / 点间距 / 距文件夹底边。</summary>
            public const double PagerDot = 3;
            public const double PagerGap = 6;
            public const double PagerBottom = 8;
            /// <summary>非当前页的小点（比滚动条的 58 亮一档，3px 的点才看得见）。</summary>
            public static readonly Color PagerIdle = Color.FromArgb(96, 255, 255, 255);
            /// <summary>当前页的小点（= 主文字色，最亮）。两个色都在这里，改一个不会忘另一个。</summary>
            public static readonly Color PagerActive = TextPrimary;

            /// <summary>滑块颜色（半透明白，压在深色底上）。</summary>
            public static readonly Color Bar = Color.FromArgb(58, 255, 255, 255);

            public static int InkPx(Control c) { return Theme.Px(c, Ink); }
            public static int MarginPx(Control c) { return Theme.Px(c, Margin); }
            public static int MinThumbPx(Control c) { return Theme.Px(c, MinThumb); }
            public static int TrackPadPx(Control c) { return Theme.Px(c, TrackPad); }

            /// <summary>列表要在右侧留出的宽度（行别写到墨水底下）。</summary>
            public static int ReservePx(Control c) { return MarginPx(c) + InkPx(c) + Theme.Px(c, BarClearance); }

            /// <summary>命中区左边界（客户区坐标）：墨水左缘再往外放 HitSlack。
            /// **别加宽**：宽了会抢走行右端的勾选框（用户点勾选变成翻滚动）。
            /// 控件窄到放不下滚动条时返回 viewW（= 没有命中区），避免「整个控件都算滚动条」。</summary>
            public static int HitLeft(Control c, int viewW)
            {
                int w = MarginPx(c) + InkPx(c) + Theme.Px(c, HitSlack);
                if (viewW <= w) return viewW;
                return viewW - w;
            }

            /// <summary>把滚轮 Delta 归一化成像素：一格（Notch=120）固定滚 rowsPerNotch 行，除不尽按比例缩。
            /// 高精度滚轮 / 触控板会送 30~60 的碎 Delta，直接拿它当像素用就会「滚一格只动一丁点」。</summary>
            public static int WheelPixels(int delta, int rowPitch, double rowsPerNotch)
            {
                if (delta == 0 || rowPitch <= 0) return 0;
                int px = (int)Math.Round(delta * (rowPitch * rowsPerNotch) / (double)Notch);
                if (px == 0) px = delta > 0 ? 1 : -1;
                return px;
            }

            /// <summary>滑块高度（按内容/视口比例，但不小于 MinThumb）。</summary>
            public static int ThumbHeight(Control c, int trackH, int contentH, int viewH)
            {
                if (contentH <= viewH) return trackH;
                int h = (int)((double)trackH * viewH / contentH);
                int min = Math.Min(MinThumbPx(c), trackH);
                return Math.Max(min, h);
            }

            /// <summary>滑块矩形（两处共用同一套几何：改这里两边一起变，不会再各飘各的）。</summary>
            public static Rectangle BarRect(Control c, int trackTop, int trackH, int contentH, int viewH, int offset)
            {
                int thumbH = ThumbHeight(c, trackH, contentH, viewH);
                int max = Math.Max(1, contentH - viewH);
                int travel = Math.Max(0, trackH - thumbH);
                if (offset < 0) offset = 0;
                if (offset > max) offset = max;
                int thumbY = trackTop + (int)((long)travel * offset / max);
                int w = InkPx(c);
                int x = Math.Max(0, c.ClientSize.Width - w - MarginPx(c));
                return new Rectangle(x, thumbY, w, thumbH);
            }

            /// <summary>画滑块（颜色、圆角也统一）。</summary>
            public static void PaintBar(Graphics g, Rectangle bar)
            {
                FillRound(g, bar, Math.Max(1, bar.Width / 2), Bar);
            }
        }

        /// <summary>Win11 的圆角窗口；老系统不支持就返回错误码，忽略即可。</summary>
        public static void ApplyRoundedCorners(Form f)
        {
            if (f == null || !f.IsHandleCreated) return;
            try
            {
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(f.Handle, 33, ref pref, sizeof(int));
            }
            catch { }
        }
    }
}