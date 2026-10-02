// MainForm.cs —— 面板本体：大文件夹分组、启动、右键菜单
//
// 交互约定（**照代码重写过一遍**，2026-09-27 核实）：
//   · 点大文件夹里的小图标 = 直接启动。**默认启动完不关面板**，可以连着点好几个
//     （只有设置里打开「启动应用后：关闭面板」时才关，默认关闭：ConfigStore.CloseAfterLaunch = false）
//   · 点大文件夹主体 = 打开「添加应用」选择器
//   · 右键小图标 / 大文件夹 / 空白 = 各自的菜单
//   · 按住空白拖动 = 搬面板（**点空白不再关面板** —— 那和拖动打架）
//   · 关掉面板 = 进程退出，没有托盘、没有后台
//
// ★★关面板的途径**只有这 5 条，全是用户主动触发**（改之前先数一遍）：
//     ① 底栏「关闭」按钮（:400）
//     ② Esc 键（:1362）
//     ③ 启动应用成功 + 设置里打开了「启动应用后：关闭面板」（:2035）
//     ④ 右键菜单「打开文件所在位置」（:2311）
//     ⑤ 右键菜单「打开软件所在文件夹」（:2395）
// ★★**已经删掉、别再装回来**（用户明确要求去掉的）：
//     ✗ 点空白处关闭    ✗ 失去焦点自动关闭
//     这两条以前都有，用户的原话感受是「被别的窗口压住时点它一下，面板自己就没了」。
//     本文件头以前把这两条当成"现行约定"写着 —— 照着它改代码，正好把用户受不了的行为装回去。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BreadLauncher
{
    public class MainForm : Form
    {
        public bool PreviewMode = false;

        /// <summary>非空时设置只从这个文件读写（自检 / 预览用，见 MainForm(string)）。</summary>
        private readonly string _settingsFile;

        /// <summary>读配置时的出错原因（OnLoad 里提醒一次就清掉）。</summary>
        private string _settingsLoadError;

        /// <summary>本次会话里第一次「配置没写进去」的原因。**不打扰用户**，只在「关于」里写一笔
        /// （用户 2026-09-27 明确选的方式：出事了要能查到，但别弹窗打断）。</summary>
        private string _lastWriteFailText;

        private Settings _settings;
        private List<AppEntry> _all = new List<AppEntry>();
        private List<GroupView> _groups = new List<GroupView>();
        private IconService _icons;

        private FlatButton _settingsBtn;
        private FlatButton _newGroupBtn;
        private FlatButton _closeBtn;

#pragma warning disable 0414   // 这两个标记仍由菜单 / 模态框维护，只是不再参与「失焦是否关面板」的判断
        private bool _suppressDeactivate;
        private bool _modalOpen;
        /// <summary>亚克力**实际生效**没有（系统不支持时会自动退回不透明）。PaintAll 靠它决定要不要画背景。</summary>
        private bool _acrylicActive;
#pragma warning restore 0414
        private DateTime _shownAt = DateTime.Now;
        private int _pendingIcons;
        /// <summary>取图 / 扫描的「代数」：刷新一次就 +1，用来作废上一轮在途的后台回调。</summary>
        private int _iconGen;
        private int _scanGen;

        /// <summary>启动时：缓存比这还旧就在后台重扫一遍（单位**分钟**）。
        /// ★2026-10-01 从 12 小时降到 5 分钟 —— 用户报「刚装的软件在「添加应用」里找不到」。
        /// 面板是「用完就退」的，每次双击都是新进程：12 小时意味着下午装的游戏晚上还看不见。
        /// ⚠ 这不是定时器，**只在启动那一刻判一次**；关掉面板进程就没了，不留任何后台。</summary>
        private const int CacheRefreshMinutes = 5;

        /// <summary>打开「添加应用」之前：缓存比这还旧就先同步重扫一次（单位**分钟**）。
        /// 1 分钟是为了「同一个会话里连着开几次窗口不用反复扫」，而不是为了省事 ——
        /// 刚装完软件的那一刻，用户正站在这个窗口里，这是唯一能救他的地方（窗口是模态的，
        /// 开着它的时候主面板右键点不动，那个「刷新应用列表」根本够不着）。</summary>
        private const int PickerRescanMinutes = 1;

        // 分组区布局（OnPaint 和命中测试共用同一套数）
        private int _groupLeft;
        private int _groupTop;
        private int _groupWidth;
        private int _groupHeight;
        private int _scrollY;
        private int _wheelAcc;         // 文件夹翻页用的滚轮累计（碎 Delta 要攒满一格 120 才翻页）
        private int _hoverGroup = -1;
        private int _hoverSlot = -2; // -2 = 不在格子上；-1 = 格子主体；0..8 = 3x3 里的小图标

        // 拖动分组（调位置）：按下记来源，移动超过阈值才算拖动，松手落到目标下标
        private int _dragFrom = -1;
        private int _dragTo = -1;
        private bool _dragActive;
        private bool _dragOnIcon;     // 从图标上起拖 = 需要更大位移，避免吃掉「点一下启动」
        private Point _dragStart;
        private int _hoverZone;        // 鼠标当前压在哪条边（0 = 没有）—— 用来高亮那条边

        // 固定的拖动把手：顶部正中间那根小横条（用户要求「有个固定的地方去拖」）
        private Rectangle _gripRect;   // 画出来的那根条
        private Rectangle _gripHit;    // 可抓范围（比条大一圈）
        private bool _gripHot;         // 鼠标是否停在把手上

        // 面板自身的搬动 / 缩放（用户要求：位置和大小都能调，而且记得住）
        private bool _panelMoving;    // 在空白处按住 → 准备搬整个面板
        private bool _panelMoved;     // 真的移动过（点一下空白现在什么都不做，面板不再靠点空白来关）
        private bool _panelResizing;
        private int _resizeZone;      // 位标记：1=N 2=S 4=W 8=E
        private int _lastResizeZone;
        private Point _panelGrabPos;   // 按下时的**屏幕坐标**（不是客户区坐标！见 OnMouseMove 里的说明）
        private Rectangle _panelGrabBounds;
        private Point _downPoint;      // 这一次按下时的位置：用来区分「点击」和「拖了一下」
        private bool _downValid;

        // 拖动 / 缩放期间的更新方式：**每个鼠标事件立刻应用**（不做时间节流 ——
        // 节流的固定间隔会和鼠标上报频率形成拍频：有的帧走两次、有的帧一次不走，看着就是「抖」）。
        // _dragTimer 只当兜底：万一最后一个鼠标事件之后没有新事件，它还能把目标补应用上
        //（WM_TIMER 是低优先级消息，队列被鼠标事件塞满时会被饿死，所以不能只靠它）。
        // ★真正的关键是位移用**屏幕坐标**算，见 OnMouseMove 里的说明。
        private readonly System.Windows.Forms.Timer _dragTimer = new System.Windows.Forms.Timer();
        private bool _dragDirty;
        private bool _dragWantResize;
        private Rectangle _dragTarget;
        private Rectangle _dragWorkArea;

        private void BeginDrag()
        {
            _dragWorkArea = CurrentWorkArea();   // 一次拖动只在开始时问一次屏幕工作区
            _dragDirty = false;
            SuspendTransparencyForDrag();        // 拖动期间临时关掉颜色键透明（合成开销最大的那块）
            _dragTimer.Start();
        }

        private void EndDrag()
        {
            _dragTimer.Stop();
            ApplyDragTarget();                   // 把最后的目标落实（否则松手位置会差一点点）
            ResumeTransparencyAfterDrag();       // 恢复亚克力/透明外观
            // ★★拖动/缩放结束后**必须把边缘高亮清掉**（用户 2026-09-28 报：「这个侧边的蓝色怎么不自己消失呢」）。
            //   机制：`SuspendTransparencyForDrag()` 在拖动期间把 `TransparencyKey` 清空 → 面板**临时变成不透明**，
            //   鼠标事件这才回到面板，`_hoverZone` 被点亮；松手后 `ResumeTransparencyAfterDrag()` 又把它变回
            //   **颜色键穿透**（亚克力默认开启时，面板空白处本来就是穿透的）→ 之后鼠标再怎么动，
            //   面板**都收不到 MouseMove/MouseLeave** → 那条边会一直亮着，直到下次把鼠标移回面板上。
            //   ⇒ 平时"靠近边缘就高亮"其实只在**面板不透明**时生效（也就是拖动期间）；所以收尾一定要清。
            _hoverZone = 0;
        }

        // 拖动期间临时关掉颜色键透明的现场
        private bool _bgSuspended;
        private Color _bgSavedKey;
        private Color _bgSavedBack;

        /// <summary>
        /// 拖动/缩放期间临时取消「颜色键透明」：面板开了亚克力时是**分层窗口**，
        /// Windows 每次移动/改大小都要重新做一次透明合成 —— 这是除绘制之外最大的一笔开销。
        /// 松手立刻恢复，所以平时看到的外观不变。
        /// </summary>
        private void SuspendTransparencyForDrag()
        {
            if (PreviewMode || _bgSuspended) return;
            try
            {
                if (TransparencyKey == Color.Empty) return;   // 本来就不透明，不用折腾
                _bgSavedKey = TransparencyKey;
                _bgSavedBack = BackColor;
                _bgSuspended = true;
                TransparencyKey = Color.Empty;
                BackColor = Theme.BgBottom;
            }
            catch (Exception) { _bgSuspended = false; }
        }

        private void ResumeTransparencyAfterDrag()
        {
            if (_bgSuspended == false) return;
            _bgSuspended = false;
            try
            {
                BackColor = _bgSavedBack;
                TransparencyKey = _bgSavedKey;
                Invalidate();
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 记录拖动/缩放的「最新目标」并**立刻应用**。
        /// ★为什么不做时间节流：节流的固定间隔和鼠标上报频率会形成**拍频** —— 有的帧连走两次、
        /// 有的帧一次都不走，看着就是「抖」。每个鼠标事件都应用，跟手最紧。
        /// 定时器只当兜底（万一最后一个事件之后没有新事件，也能把目标应用上）。
        /// </summary>
        private void QueueDragTarget(Rectangle r, bool resize)
        {
            _dragTarget = r;
            _dragWantResize = resize;
            _dragDirty = true;
            ApplyDragTarget();
        }

        private void ApplyDragTarget()
        {
            if (_dragDirty == false) return;
            _dragDirty = false;
            Rectangle r = _dragTarget;
            if (_dragWantResize)
            {
                if (Bounds != r) Bounds = r;
            }
            else if (Left != r.X || Top != r.Y)
            {
                MoveWindowFast(r.X, r.Y);
            }
        }

        /// <summary>搬窗口：直接走 Win32 `SetWindowPos`，并且**不重画客户区**。
        /// 移动时客户区内容跟着窗口一起走、本来就不需要重画，省下每帧那次整窗重画（实测 3ms/帧）。
        /// 松手时 `ResumeTransparencyAfterDrag` 里有一次完整 `Invalidate()`，不会留下残影。</summary>
        private void MoveWindowFast(int x, int y)
        {
            try
            {
                SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW);
            }
            catch (Exception) { Location = new Point(x, y); }
        }

        /// <summary>光标在面板四条边的抓取范围内时返回区域标记（否则 0）。</summary>
        private int ResizeZoneAt(Point p)
        {
            // ★热区按「普通人点得准」定：左右上 14px、下方 12px。
            //   面板没边框，这条边带本身是**隐形**的 —— 所以靠近时 OnPaint 会高亮那一条边，
            //   右下角还常驻一个斜纹拉伸手柄；否则用户根本不知道「边」在哪（这是用户反馈「很难调整大小」的真原因）。
            int mx = Theme.Px(this, 14);
            int mTop = Theme.Px(this, 14);
            int mBottom = Theme.Px(this, 12);
            int z = 0;
            if (p.Y <= mTop) z |= 1;
            if (p.Y >= Height - mBottom) z |= 2;
            if (p.X <= mx) z |= 4;
            if (p.X >= Width - mx) z |= 8;
            // 保险：下边带若和底栏按钮重叠，按在按钮上就不算「拉大小」
            if ((z & 2) != 0 && OnFooterButton(p)) z &= ~2;
            // ★保险二：分组区那根滚动条是**纯指示**（滚轮才是滚动方式）。它正好画在右侧 14px 缩放带里，
            //   不让位的话「点一下滚动条」会变成「把面板拉宽」并且**落盘**（下次打开还是变形的）。
            //   只让出墨水那几列：[W-7,W-4] 点它没反应；最外面 3px 与 W-14..W-8 照样能拉大小。
            if ((z & 8) != 0 && MaxScroll > 0 && p.Y >= _groupTop && p.Y <= _groupTop + _groupHeight)
            {
                Rectangle bar = Theme.Scroll.BarRect(this, _groupTop, _groupHeight, GroupContentHeight, _groupHeight, _scrollY);
                if (p.X >= bar.X && p.X <= bar.Right - 1) z &= ~8;
            }
            return z;
        }

        /// <summary>这个点是不是落在底栏某个按钮上（防止下边带吃掉按钮的点击）。</summary>
        private bool OnFooterButton(Point p)
        {
            FlatButton[] bs = new FlatButton[] { _settingsBtn, _newGroupBtn, _closeBtn };
            for (int i = 0; i < bs.Length; i++)
            {
                FlatButton b = bs[i];
                if (b != null && b.Visible && b.Bounds.Contains(p)) return true;
            }
            return false;
        }

        private static Cursor ZoneCursor(int zone)
        {
            if (zone == 0) return null;
            bool n = (zone & 1) != 0, s = (zone & 2) != 0, w = (zone & 4) != 0, e = (zone & 8) != 0;
            if ((n && w) || (s && e)) return Cursors.SizeNWSE;
            if ((n && e) || (s && w)) return Cursors.SizeNESW;
            if (n || s) return Cursors.SizeNS;
            return Cursors.SizeWE;
        }

        /// <summary>当前面板所在的屏幕可用区域（缩放/搬动时用它兜底，别把面板搞到看不见的地方）。</summary>
        private Rectangle CurrentWorkArea()
        {
            try
            {
                Screen s = Screen.FromPoint(new Point(Left + Width / 2, Top + Theme.Px(this, 10)));
                if (s != null) return s.WorkingArea;
            }
            catch (Exception) { }
            return Screen.PrimaryScreen.WorkingArea;
        }

        private void DoResize(Point p)
        {
            int dx = p.X - _panelGrabPos.X;
            int dy = p.Y - _panelGrabPos.Y;
            Rectangle b = _panelGrabBounds;
            Rectangle wa = _dragWorkArea.Width > 0 ? _dragWorkArea : CurrentWorkArea();
            int minW = Theme.Px(this, 360);
            int minH = Theme.Px(this, 220);
            int maxW = Math.Max(minW, wa.Width - Theme.Px(this, 8));    // 最大不超过屏幕可用区域
            int maxH = Math.Max(minH, wa.Height - Theme.Px(this, 8));
            int left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom;
            if ((_resizeZone & 4) != 0) left = b.Left + dx;
            if ((_resizeZone & 8) != 0) right = b.Right + dx;
            if ((_resizeZone & 1) != 0) top = b.Top + dy;
            if ((_resizeZone & 2) != 0) bottom = b.Bottom + dy;
            if (right - left < minW) { if ((_resizeZone & 4) != 0) left = right - minW; else right = left + minW; }
            if (bottom - top < minH) { if ((_resizeZone & 1) != 0) top = bottom - minH; else bottom = top + minH; }
            if (right - left > maxW) { if ((_resizeZone & 4) != 0) left = right - maxW; else right = left + maxW; }
            if (bottom - top > maxH) { if ((_resizeZone & 1) != 0) top = bottom - maxH; else bottom = top + maxH; }
            // 只有面板本来就在屏幕附近时才把它拉进工作区；预览 / 自检时窗口故意放在屏幕外，别硬拽回来
            bool offscreen = (right < wa.Left || left > wa.Right || bottom < wa.Top || top > wa.Bottom);
            if (!offscreen && top < wa.Top) top = wa.Top;
            // 只记录目标并节流应用（每个鼠标事件都设 Bounds 会卡）
            QueueDragTarget(new Rectangle(left, top, right - left, bottom - top), true);
        }

        /// <summary>把当前的位置/大小存进配置（下次打开就用这个，位置和大小都记得住）。</summary>
        private void SavePanelBounds(bool vertical)
        {
            if (_settings == null) return;
            _settings.PanelX = Left;
            _settings.PanelY = Top;
            _settings.PanelW = Width;
            if (vertical) _settings.PanelH = Height;   // 只有上下拉过才固定高度；左右拉仍保持自动高度
            PersistSettings();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // 注意：基类构造期间就可能触发 OnResize，那时控件还没建出来 —— 不加这个守卫会 NullReference
            if (!IsHandleCreated || _settingsBtn == null) return;
            LayoutUi();
            // 只失效自己的客户区：三个按钮的位置由 LayoutUi 改，它们会自己重画。
            // 以前这里是 Invalidate(true)（连子控件一起失效），缩放时每帧多画一遍按钮，明显更卡。
            Invalidate();
        }

        private const int FolderCapacity = 9;

        private readonly HashSet<string> _requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public MainForm() : this(null)
        {
        }

        /// <summary>settingsFile 非空时用它替掉 build\settings.json（自检 / 预览出图时别碰用户真实分组）。</summary>
        public MainForm(string settingsFile)
        {
            _settingsFile = settingsFile;

            Text = "BreadLauncher";
            // ★★窗口 / 任务栏图标必须**自己设**。不设的话 WinForms 会用它**自带的默认图标**（一张彩色拼图），
            //   任务栏按钮和 Alt-Tab 上就一直是那张，跟资源管理器 / 桌面快捷方式看到的那张（面包方块）对不上
            //   —— 用户 2026-10-01 报的就是这个（「这图片是不是不太对」）。
            //   实测证据：运行中窗口的图标与 .NET Framework 版 WinForms 的默认图标**逐字节相同**（16×16 与 32×32 两份）。
            //   从**自己的 exe** 里取（csc 编译时 `/win32icon` 已经把 assets\BreadLauncher.ico 嵌进去了）——
            //   这样单文件拷贝到哪儿都对，不依赖旁边的 .ico；
            //   ⚠ 别改成 `new Icon(typeof(MainForm), "BreadLauncher.ico")`：那要求把 ico 当**托管资源**嵌进去
            //     （要改编译命令，探针那份编译没有它就会抛异常）。
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            // 置顶：面板是「叫出来用一下就关」的东西，被别的窗口压住会让人以为点不动/点一下就没了。
            // 用户明确要求「主动置顶」，所以这里固定 TopMost。
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            // 拖动 / 缩放期间的合并定时器（见字段注释）
            _dragTimer.Interval = 10;
            _dragTimer.Tick += delegate { ApplyDragTarget(); };

            _settings = string.IsNullOrEmpty(_settingsFile)
                ? ConfigStore.LoadSettings(Program.AppDir)
                : ConfigStore.LoadSettingsFile(_settingsFile);
            // 配置读坏了要告诉用户一声（否则他改半天分组，全落在默认值上）
            _settingsLoadError = ConfigStore.LastReadError;
            ConfigStore.LastReadError = null;
            _icons = new IconService(Program.AppDir);

            // ★★第一帧就必须出现在正确的位置上。不设这两行的话，窗口会以默认的 (0,0)（= 屏幕左上角）
            //   被创建出来，而真正的位置要等 OnLoad → PositionWindow() 才摆上去 —— 于是「左上角闪一下再跳过来」
            //   （用户 2026-09-28 报的「每次打开左上角都会弹窗然后消失」）。
            //   这里用的是和 OnLoad 完全相同的那份存档值，等价、不会多一次缩放；
            //   Location 是屏幕绝对像素，不需要按 DPI 换算。
            if (_settings != null)
            {
                if (_settings.PanelW > 0 && _settings.PanelH > 0)
                    Size = new Size(_settings.PanelW, _settings.PanelH);
                if (_settings.PanelX >= 0 && _settings.PanelY >= 0)
                    Location = new Point(_settings.PanelX, _settings.PanelY);
            }

            BuildUi();
        }

        /// <summary>还有多少个图标在后台取（自检模式用它等加载完成）。</summary>
        public int PendingIcons
        {
            get { lock (this) { return _pendingIcons; } }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW：窗口外一圈淡阴影
                return cp;
            }
        }

        // 尺寸（统一走 Theme.Px，跟着 DPI 缩放；再乘一个「文件夹大小」系数，设置菜单里可调）
        // 名字叫 UiScale：别叫 Scale，会盖住 Control.Scale(float)（编译器会警告 CS0108）。
        private double UiScale
        {
            get
            {
                if (_settings == null || _settings.FolderScale <= 0) return 1.0;
                return _settings.FolderScale / 100.0;
            }
        }
        private int TileW { get { return Theme.Px(this, 132 * UiScale); } }
        private int ContainerH { get { return Theme.Px(this, 132 * UiScale); } }
        private int GroupNameH { get { return Theme.Px(this, 26 * UiScale); } }
        private int TileH { get { return ContainerH + GroupNameH; } }
        private int GapX { get { return Theme.Px(this, 14); } }
        private int GapY { get { return Theme.Px(this, 16); } }
        private int MiniPad { get { return Theme.Px(this, 7 * UiScale); } }
        private int MiniGap { get { return Theme.Px(this, 6 * UiScale); } }
        private int MiniBox { get { return (TileW - MiniPad * 2 - MiniGap * 2) / 3; } }
        private int MiniIconSize { get { return Theme.Px(this, 30 * UiScale); } }
        private int PadOuter { get { return Theme.Px(this, 28); } }
        // ============================================================
        // 建界面
        // ============================================================
        private void BuildUi()
        {
            _settingsBtn = new FlatButton();
            _settingsBtn.Style = FlatButton.Look.IconText;
            _settingsBtn.Glyph = "\uE713";
            _settingsBtn.Text = "设置";
            _settingsBtn.Click += delegate { ShowSettingsMenu(); };

            _newGroupBtn = new FlatButton();
            _newGroupBtn.Style = FlatButton.Look.IconText;
            _newGroupBtn.Glyph = "\uE710";
            _newGroupBtn.Text = "新建分组";
            _newGroupBtn.Click += delegate { NewGroup(); };

            _closeBtn = new FlatButton();
            _closeBtn.Style = FlatButton.Look.IconText;
            _closeBtn.Glyph = "\uE8BB";
            _closeBtn.Text = "关闭";
            _closeBtn.Click += delegate { Close(); };

            Controls.Add(_settingsBtn);
            Controls.Add(_newGroupBtn);
            Controls.Add(_closeBtn);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            Size = new Size(
                (_settings != null && _settings.PanelW > 0) ? _settings.PanelW : Theme.Px(this, 640),
                (_settings != null && _settings.PanelH > 0) ? _settings.PanelH : Theme.Px(this, 400));
            Theme.ApplyRoundedCorners(this);
            _acrylicActive = Theme.ApplyBackdrop(this, _settings.Acrylic);   // 返回值 = 实际生效没有

            // ★★启动时**不许弹任何提示**（用户 2026-09-28 明确要求：第一眼就弹窗很烦，
            //   「用户用的时候自行接纳就是了」）。以前这里有一次性的「亚克力已开启」提示框，
            //   已经**整条拆掉**（`ShowAcrylicHint` 方法也删了，别再加回来）。
            //   亚克力的代价（空白处鼠标穿透）本来就写在设置菜单那一行字里：
            //   「亚克力半透明（空白处会穿透鼠标）：已开启」，不需要再弹窗重复一遍。

            _shownAt = DateTime.Now;
            TopMost = true;   // 构造期设过，但窗口创建/定位之后会被吃掉一次，这里再钉一遍
            LoadData(false);
            ApplyPanelHeight();
            if (PreviewMode == false) PositionWindow(); // 自检模式留在屏幕外，别打扰用户
            LayoutUi();
            WarnIfSettingsBroken();
        }

        /// <summary>配置文件读坏了要让人知道：不然用户改半天分组，全落在默认值上。</summary>
        private void WarnIfSettingsBroken()
        {
            if (PreviewMode) return;
            if (string.IsNullOrEmpty(_settingsLoadError)) return;
            string err = _settingsLoadError;
            _settingsLoadError = null;
            _modalOpen = true;
            try
            {
                MessageBox.Show(this,
                    "配置文件读不了，已经按默认设置启动。\n\n" + err +
                    "\n\n原文件已尽量备份成 settings.json.bad；要是没备份成功，先别改分组，先把它拷出来。",
                    "BreadLauncher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { _modalOpen = false; }
        }

        private void PositionWindow()
        {
            Screen scr;
            try { scr = Screen.FromPoint(Cursor.Position); }
            catch (Exception) { scr = Screen.PrimaryScreen; }
            if (scr == null) scr = Screen.PrimaryScreen;

            Rectangle wa = scr.WorkingArea;
            int w = Size.Width;
            int h = Size.Height;

            // 用户自己挪过 / 拉过：按存下来的位置和大小放，但仍然夹在当前屏幕的可视范围里，
            // 免得换显示器或改分辨率之后面板跑到看不见的地方。
            if (_settings != null && _settings.PanelX >= 0 && _settings.PanelY >= 0)
            {
                if (w > wa.Width - Theme.Px(this, 8)) w = wa.Width - Theme.Px(this, 8);
                if (h > wa.Height - Theme.Px(this, 8)) h = wa.Height - Theme.Px(this, 8);
                Size = new Size(w, h);
                int left = _settings.PanelX;
                int top = _settings.PanelY;
                if (left > wa.Right - Theme.Px(this, 40)) left = wa.Right - Theme.Px(this, 40);
                if (top > wa.Bottom - Theme.Px(this, 40)) top = wa.Bottom - Theme.Px(this, 40);
                if (left < wa.Left - w + Theme.Px(this, 40)) left = wa.Left - w + Theme.Px(this, 40);
                if (top < wa.Top) top = wa.Top;
                // 整块塞进工作区（能塞下的话）：免得下边缘连同底栏跑到屏幕外
                if (h <= wa.Height && top + h > wa.Bottom) top = wa.Bottom - h;
                if (w <= wa.Width && left + w > wa.Right) left = wa.Right - w;
                if (left < wa.Left) left = wa.Left;
                Left = left;
                Top = top;
                return;
            }

            if (h > wa.Height - Theme.Px(this, 16)) h = wa.Height - Theme.Px(this, 16);
            Size = new Size(w, h);

            Left = wa.Left + (wa.Width - w) / 2;
            Top = wa.Bottom - h - Theme.Px(this, 6);
        }

        // ============================================================
        // 布局
        // ============================================================
        private int Columns
        {
            get
            {
                int width = _groupWidth > 0 ? _groupWidth : Math.Max(1, ClientSize.Width - PadOuter * 2);
                int cols = (width + GapX) / (TileW + GapX);
                return Math.Max(1, cols);
            }
        }

        private int GroupRows
        {
            get
            {
                int c = Columns;
                if (_groups.Count == 0) return 0;
                return (_groups.Count + c - 1) / c;
            }
        }

        private int GroupContentHeight
        {
            get
            {
                int rows = GroupRows;
                if (rows == 0) return TileH;
                return rows * TileH + (rows - 1) * GapY;
            }
        }

        private int MaxScroll
        {
            get
            {
                int extra = GroupContentHeight - _groupHeight;
                return extra > 0 ? extra : 0;
            }
        }

        private void PrimeGroupWidth()
        {
            _groupWidth = Math.Max(Theme.Px(this, 200), ClientSize.Width - PadOuter * 2);
        }

        /// <summary>面板高度 = 顶边距 + 分组内容 + 底栏；超出一屏就贴合工作区。用户拉过高就固定成那个高度。</summary>
        private int DesiredHeight()
        {
            if (_settings != null && _settings.PanelH > 0)
                return Math.Max(200, _settings.PanelH);

            int footer = Theme.Px(this, 38) + Theme.Px(this, 12);
            int h = Theme.Px(this, 18) + GroupContentHeight + Theme.Px(this, 10) + footer;

            int cap = 900;
            try
            {
                Screen scr = Screen.PrimaryScreen;
                if (PreviewMode == false) scr = Screen.FromPoint(Cursor.Position);
                if (scr != null) cap = scr.WorkingArea.Height - Theme.Px(this, 16);
            }
            catch (Exception) { }
            if (cap < 200) cap = 200;

            return Math.Max(200, Math.Min(h, cap));
        }

        /// <summary>改高度时保持底边不动（面板贴着任务栏弹出），别让它往上跳。用户固定过高度就不动它。</summary>
        private void ApplyPanelHeight()
        {
            PrimeGroupWidth();
            if (_settings != null && _settings.PanelH > 0) return;   // 用户自己拉过高度：别自作主张改
            int target = DesiredHeight();
            if (Height == target) return;

            int bottom = Bottom;
            Size = new Size(Size.Width, target);
            if (PreviewMode) return;
            Top = bottom - target;
            try
            {
                Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                if (Top < wa.Top + Theme.Px(this, 4)) Top = wa.Top + Theme.Px(this, 4);
            }
            catch (Exception) { }
        }

        private void LayoutUi()
        {
            int pad = PadOuter;
            int w = ClientSize.Width;
            int h = ClientSize.Height;

            int footerH = Theme.Px(this, 38);
            int footerY = h - Theme.Px(this, 12) - footerH;

            _groupTop = Theme.Px(this, 18);
            _groupWidth = Math.Max(Theme.Px(this, 200), w - pad * 2);

            // 固定拖动把手：顶部正中一根小横条（和顶部 14px 的「拉边框」热区**有重叠**，
            // 靠 OnMouseDown 的判断顺序解决：把手 → 边框缩放 → 文件夹拖动 → 空白搬动）
            // 尺寸按「普通人点得准」定：可见条 60×6，可抓范围 120×20（上下都留了余量）
            // ★可抓范围**下沿必须收在分组区上沿**：原来固定 y=6、高 20 → 伸到 y=26，
            //   而第一行文件夹从 _groupTop（18）就开始，于是把手把第一行中间那一列**压住 8px** ——
            //   点那儿本来是点文件夹，结果变成搬面板。
            int gripW = Theme.Px(this, 60);
            int gripH = Theme.Px(this, 6);
            _gripRect = new Rectangle((w - gripW) / 2, Theme.Px(this, 10), gripW, gripH);
            int gripTop = Theme.Px(this, 6);
            int gripHitH = Math.Max(Theme.Px(this, 10), Math.Min(Theme.Px(this, 20), _groupTop - gripTop));
            _gripHit = new Rectangle(_gripRect.X - Theme.Px(this, 30), gripTop,
                                     _gripRect.Width + Theme.Px(this, 60), gripHitH);

            int cols = Columns;
            int gridW = cols * TileW + (cols - 1) * GapX;
            _groupLeft = pad + Math.Max(0, (_groupWidth - gridW) / 2);
            _groupHeight = Math.Max(Theme.Px(this, 40), footerY - Theme.Px(this, 10) - _groupTop);

            int max = MaxScroll;
            if (_scrollY > max) _scrollY = max;
            if (_scrollY < 0) _scrollY = 0;

            _settingsBtn.Bounds = new Rectangle(pad - Theme.Px(this, 10), footerY, Theme.Px(this, 110), footerH);
            _newGroupBtn.Bounds = new Rectangle(pad + Theme.Px(this, 110), footerY, Theme.Px(this, 152), footerH);
            _closeBtn.Bounds = new Rectangle(w - pad - Theme.Px(this, 110), footerY, Theme.Px(this, 110), footerH);
        }
        // ============================================================
        // 绘制
        // ============================================================
        /// <summary>★绘制里抛任何异常，WinForms 都会把整块客户区画成「白底 + 红色大叉」，
        /// 用户只会说「报错了」，而且什么线索都没有。这里统一兜住并写日志（含堆栈与异常类型），
        /// 失败时画一个朴素底色，至少不吓人。**改绘制代码时别把 try 去掉。**</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                PaintAll(e);
            }
            catch (Exception ex)
            {
                try
                {
                    Graphics g = e.Graphics;
                    using (SolidBrush b = new SolidBrush(Theme.BgBottom))
                        g.FillRectangle(b, ClientRectangle);
                }
                catch (Exception) { }
                try
                {
                    if (Program.AppDir != null)
                        ConfigStore.Log(Program.AppDir, "★绘制异常（界面本来会变成白底红叉）："
                            + ex.GetType().Name + "：" + ex.Message + "  @  " + ex.StackTrace);
                }
                catch (Exception) { }
            }
        }

        private void PaintAll(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingMode = CompositingMode.SourceOver;

            bool acrylic = _acrylicActive;      // ★用"实际生效"而不是设置值：系统不支持时这里会是 false → 画不透明背景
            // ★亚克力 = 不画背景（整块透出桌面，最好看）。代价：被挖空的像素**连鼠标一起穿透**
            //   （MSDN：color-keyed 区域会让鼠标消息穿过去）—— 「鼠标在面板空白处滚滚轮，别的应用滚了」就是这个。
            //   用户拍板：**好看优先**，靠设置菜单的文案 + 打开时的一次提示说清代价（那个"是否穿透"的开关已按用户要求删掉）。
            if (acrylic == false)
            {
                using (LinearGradientBrush b = new LinearGradientBrush(
                    new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)),
                    Theme.BgTop, Theme.BgBottom, LinearGradientMode.Vertical))
                {
                    g.FillRectangle(b, new Rectangle(0, 0, Width, Height));
                }
            }

            DrawGroups(g);

            // 拖动把手：平时是淡淡的灰条，鼠标停上去变亮（提示「这里可以拖」）
            if (_gripRect.Width > 0)
            {
                Color gc = (_gripHot || _panelMoving)
                    ? Color.FromArgb(200, 255, 255, 255)
                    : Color.FromArgb(92, 255, 255, 255);
                Theme.FillRound(g, _gripRect, Math.Max(1, _gripRect.Height / 2), gc);
            }

            // ★边缘提示：面板没有边框，不加这个提示用户根本找不到「边」在哪（也很难拉大小）。
            //   鼠标压到哪条边就高亮哪条；正在拉的时候一直亮着。
            int hz = _panelResizing ? _lastResizeZone : _hoverZone;
            if (hz != 0)
            {
                int t = Math.Max(2, Theme.Px(this, 2));
                using (SolidBrush hb = new SolidBrush(Color.FromArgb(160, Theme.Accent)))
                {
                    if ((hz & 1) != 0) g.FillRectangle(hb, 0, 0, Width, t);                    // 上
                    if ((hz & 2) != 0) g.FillRectangle(hb, 0, Height - t, Width, t);           // 下
                    if ((hz & 4) != 0) g.FillRectangle(hb, 0, 0, t, Height);                   // 左
                    if ((hz & 8) != 0) g.FillRectangle(hb, Width - t, 0, t, Height);           // 右
                }
            }

            // ★右下角常驻「斜纹拉伸手柄」（就是窗口右下角那种三道斜线）：一眼就知道这里能拉大小。
            //   刻意**不加** PreviewMode 守卫：预览出图要能反映真实观感，否则「画面改动必须出图看」就成了空话。
            {
                using (Pen gp = new Pen(Color.FromArgb(_hoverZone == 10 || _panelResizing ? 255 : 150, 255, 255, 255), 1f))
                {
                    for (int i = 1; i <= 3; i++)
                    {
                        int off = Theme.Px(this, i * 5);
                        g.DrawLine(gp, Width - Theme.Px(this, 5) - off, Height - Theme.Px(this, 5),
                                       Width - Theme.Px(this, 5), Height - Theme.Px(this, 5) - off);
                    }
                }
            }

            using (Pen pen = new Pen(Theme.Border))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        /// <summary>
        /// 悬停在一个小图标上时，在它下面画一条应用名（深色圆角底 + 白字，压在图标下沿上）。
        /// 位置夹在分组方框内，名字太长就省略号 —— 别让它跑出格子。
        /// </summary>
        /// <summary>鼠标悬停要不要浮出名字（设置菜单里可关；默认开）。</summary>
        private bool HoverNamesOn
        {
            get { return _settings == null || _settings.HoverNames; }
        }

        private void DrawHoverName(Graphics g, string name, Rectangle cell)
        {
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                Font f = Theme.Ui(8.5f);
                // ★这是**浮层提示**，可以比格子宽：长名字（Blackmagic Proxy Generator 这种）在格子宽度里
                //   只会显示成「Blackmagic Pro…」，那提示就白给了。上限放到「面板宽度 - 边距」。
                int maxW = Math.Max(Theme.Px(this, 80), Math.Min(Theme.Px(this, 260), Width - Theme.Px(this, 16)));
                Size sz = TextRenderer.MeasureText(name, f, new Size(maxW, Theme.Px(this, 20)), TextFormatFlags.NoPrefix);
                int w = Math.Min(maxW, sz.Width + Theme.Px(this, 12));
                int h = Theme.Px(this, 17);
                int cx = cell.X + (cell.Width - w) / 2;
                // 水平夹在面板内：贴着最左/最右的文件夹时，标签别跑出窗口
                if (cx < Theme.Px(this, 4)) cx = Theme.Px(this, 4);
                if (cx + w > Width - Theme.Px(this, 4)) cx = Math.Max(Theme.Px(this, 4), Width - Theme.Px(this, 4) - w);
                int cy = cell.Bottom - h / 2;                 // 压在图标下沿：不挡图标主体，也不跑出格子
                Rectangle box2 = new Rectangle(cx, cy, w, h);
                Theme.FillRound(g, box2, Theme.Px(this, 4), Color.FromArgb(215, 20, 20, 20));
                Theme.DrawText(g, name, f,
                    new Rectangle(box2.X + Theme.Px(this, 6), box2.Y, Math.Max(8, box2.Width - Theme.Px(this, 12)), box2.Height),
                    Theme.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            catch (Exception) { }
        }

        private void DrawGroups(Graphics g)
        {
            int cols = Columns;
            int tw = TileW;
            int th = ContainerH;
            int thAll = TileH;
            int gx = GapX;
            int gy = GapY;
            int miniPad = MiniPad;
            int miniGap = MiniGap;
            int miniBox = MiniBox;
            int radius = Theme.Px(this, 18);
            int corner = Theme.Px(this, 8);

            GraphicsState state = g.Save();
            g.SetClip(new Rectangle(0, _groupTop, Math.Max(1, Width), Math.Max(1, _groupHeight)), CombineMode.Replace);

            for (int i = 0; i < _groups.Count; i++)
            {
                int r = i / cols;
                int c = i % cols;
                int x = _groupLeft + c * (tw + gx);
                int y = _groupTop + r * (thAll + gy) - _scrollY;
                if (y + thAll < _groupTop) continue;
                if (y > _groupTop + _groupHeight) continue;

                GroupView gv = _groups[i];
                bool hoverBody = _hoverGroup == i;

                Rectangle box = new Rectangle(x, y, tw, th);
                Theme.FillRound(g, box, radius, hoverBody ? Theme.SurfaceHover : Theme.Surface);
                Theme.DrawRound(g, box, radius, Theme.Border, 1f);

                // 拖动反馈：来源压暗，落点描一圈强调色
                if (_dragActive && _dragFrom == i)
                {
                    Theme.FillRound(g, box, radius, Color.FromArgb(120, 16, 16, 16));
                }
                else if (_dragActive && _dragTo == i)
                {
                    Theme.DrawRound(g, box, radius, Theme.Accent, 2f);
                }

                int count = gv.Apps.Count;
                if (count == 0)
                {
                    // ★空格子的加号：**格子完整露在可见分组区里**才画。
                    //   半截被面板下沿切掉的空格子也画加号的话，看着就是「面板外/页脚那儿冒出一个加号」
                    //   （用户报了两次）。被切掉的格子留个干净背景就好，滚动下去它自然会出现。
                    bool fullyVisible = (box.Y >= _groupTop) && (box.Bottom <= _groupTop + _groupHeight);
                    if (fullyVisible)
                        Theme.DrawText(g, "\uE710", Theme.Glyph(Theme.Px(this, 18)), box, Theme.TextDim,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else
                {
                    int page = PageOf(gv);
                    int baseIdx = page * FolderCapacity;
                    for (int s = 0; s < FolderCapacity; s++)
                    {
                        int idx = baseIdx + s;
                        if (idx >= count) break;
                        int mr = s / 3;
                        int mc = s % 3;
                        Rectangle cell = new Rectangle(x + miniPad + mc * (miniBox + miniGap),
                                                       y + miniPad + mr * (miniBox + miniGap),
                                                       miniBox, miniBox);
                        if (_hoverGroup == i && _hoverSlot == s)
                            Theme.FillRound(g, cell, corner, Theme.HoverStrong);

                        AppEntry cellApp = gv.Apps[idx];
                        // 「名字常驻显示」（右键 → 勾上）：图标让出下面一条给名字；名字**只在本格内**画，
                        // 所以勾再多也不会互相压（和悬停那种浮层不一样）。
                        bool withLabel = cellApp.ShowName;
                        Rectangle inner = cell;
                        Rectangle nameR = Rectangle.Empty;
                        if (withLabel)
                            withLabel = NameLabelLayout(cell, miniGap, tw, thAll, x, y, out inner, out nameR);
                        Bitmap icon = _icons == null ? null : _icons.Get(cellApp, MiniIconSize);
                        if (icon == null)
                        {
                            // 取不到图标（系统也只给得出白纸）：画首字母色块，比白纸好看也好认
                            int phPad = Theme.Px(this, withLabel ? 5 : 3);
                            Rectangle ph = new Rectangle(inner.X + phPad, inner.Y + phPad,
                                                         Math.Max(8, inner.Width - phPad * 2),
                                                         Math.Max(8, inner.Height - phPad * 2));
                            Theme.DrawAppTile(g, ph, cellApp.DisplayName, Theme.Px(this, 6));
                        }
                        else if (withLabel)
                        {
                            int side = Math.Min(inner.Width, inner.Height);      // 缩小画（图是 MiniIconSize 的）
                            int ix = inner.X + (inner.Width - side) / 2;
                            int iy = inner.Y + (inner.Height - side) / 2;
                            g.DrawImage(icon, new Rectangle(ix, iy, side, side));
                        }
                        else
                        {
                            int ix = cell.X + (cell.Width - icon.Width) / 2;
                            int iy = cell.Y + (cell.Height - icon.Height) / 2;
                            g.DrawImageUnscaled(icon, ix, iy);
                        }
                        if (withLabel)
                            Theme.DrawText(g, cellApp.DisplayName, Theme.Ui(6.5f), nameR, Theme.TextSecondary,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                        // ★鼠标停在这个小图标上 → 在它下面显示应用名（面板里原本只有图标、没有名字）。
                        //   用自定义显示名（右键 →「重命名…」改的就是它）。
                        if (_hoverGroup == i && _hoverSlot == s && HoverNamesOn)
                            DrawHoverName(g, gv.Apps[idx].DisplayName, cell);
                    }

                    // 分页指示：超过 9 个就在文件夹底部画一排小点（滚轮在文件夹上滚 = 翻页）
                    int pages = PageCount(gv);
                    if (pages > 1)
                    {
                        int dot = Theme.Px(this, Theme.Scroll.PagerDot);
                        int gap = Theme.Px(this, Theme.Scroll.PagerGap);
                        int totalW = pages * dot + (pages - 1) * gap;
                        int dy = y + th - Theme.Px(this, Theme.Scroll.PagerBottom);
                        int dx = x + (tw - totalW) / 2;
                        for (int d = 0; d < pages; d++)
                        {
                            Rectangle dr = new Rectangle(dx + d * (dot + gap), dy, dot, dot);
                            Theme.FillRound(g, dr, dot, d == page
                                ? Theme.Scroll.PagerActive
                                : Theme.Scroll.PagerIdle);
                        }
                    }
                }

                Rectangle nameRect = new Rectangle(x, y + th, tw, GroupNameH);
                Theme.DrawText(g, gv.Name, Theme.Ui(10f), nameRect,
                    hoverBody ? Theme.TextPrimary : Theme.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            int max = MaxScroll;
            if (max > 0)
                // 滚动条几何/颜色和「应用列表」那根是同一份代码（Theme.Scroll）：
                // 以前这里写 3px 宽、距右 11px、最小 36，列表那边是 4px / 距右 3 / 最小 28 —— 同一程序两套数。
                Theme.Scroll.PaintBar(g,
                    Theme.Scroll.BarRect(this, _groupTop, _groupHeight, GroupContentHeight, _groupHeight, _scrollY));

            g.Restore(state);
        }
        // ============================================================
        // 命中测试 / 鼠标
        // ============================================================
        /// <summary>返回格子下标；slot: -2 = 不在任何格子上，-1 = 格子主体，0..8 = 小图标。</summary>
        private int GroupHitTest(Point p, out int index, out int slot)
        {
            index = -1;
            slot = -2;
            // ★可见区守卫（必须和 SlotIndexAt:1074 一模一样）：绘制在 DrawGroups 末尾用
            //   SetClip(0, _groupTop, Width, _groupHeight) **真裁剪**，而这里原来一个边界都不查 ——
            //   于是分组区下沿到页脚之间那条 ~10px 的空白缝里点一下，会命中一个**被裁掉、一个像素都没画**
            //   的格子：left=0 直接启动那个组的前几个应用，right=0 弹出它的右键菜单。
            //   实测（build\audit\0b-recheck-this-window.md §三）：11 组 / 604x738 时
            //   y=678..682 命中 slot=0/1/2、y=690..717 命中 slot=3/4/5。
            //   判据：格子完整可见才画（坑清单第 3 条）—— 命中同样只认可见区。
            if (p.Y < _groupTop || p.Y >= _groupTop + _groupHeight) return -1;   // ★半开区间：和 SetClip 的 [_groupTop, _groupTop+_groupHeight) 对齐（原来用 > 会多算最下面那一行，点得到但一个像素没画）
            int cols = Columns;
            int tw = TileW;
            int thAll = TileH;
            int gx = GapX;
            int gy = GapY;

            for (int i = 0; i < _groups.Count; i++)
            {
                int r = i / cols;
                int c = i % cols;
                int x = _groupLeft + c * (tw + gx);
                int y = _groupTop + r * (thAll + gy) - _scrollY;

                Rectangle tile = new Rectangle(x, y, tw, thAll);
                if (tile.Contains(p) == false) continue;

                index = i;
                if (p.Y < y + ContainerH)
                {
                    int mx = p.X - x - MiniPad;
                    int my = p.Y - y - MiniPad;
                    int step = MiniBox + MiniGap;
                    if (mx >= 0 && my >= 0)
                    {
                        int ci = mx / step;
                        int ri = my / step;
                        if (ci < 3 && ri < 3)
                        {
                            int ox = mx - ci * step;
                            int oy = my - ri * step;
                            // 格子本身的宽度就是 MiniBox，MiniGap 那段是死区：
                            // 用 <= 会把缝隙算成左边那个图标，点缝隙就启动了旁边的应用
                            if (ox < MiniBox && oy < MiniBox)
                            {
                                slot = ri * 3 + ci;
                                return index;
                            }
                        }
                    }
                    slot = -1;
                    return index;
                }
                slot = -1;
                return index;
            }
            return index;
        }

        // ============================================================
        // 分组内分页：一个文件夹最多 9 格，超过 9 个就分页（滚轮停在文件夹上 = 翻页）
        // ============================================================
        private readonly Dictionary<AppGroup, int> _page = new Dictionary<AppGroup, int>();

        /// <summary>这组分几页（每页 9 格）；空组也算 1 页。</summary>
        private static int PageCount(GroupView gv)
        {
            if (gv == null || gv.Apps.Count == 0) return 1;
            return (gv.Apps.Count + FolderCapacity - 1) / FolderCapacity;
        }

        /// <summary>当前页（0 起），永远夹在合法范围内。</summary>
        private int PageOf(GroupView gv)
        {
            if (gv == null || gv.Group == null) return 0;
            int p;
            if (_page.TryGetValue(gv.Group, out p) == false) return 0;
            int max = PageCount(gv) - 1;
            // ★夹取之后要**写回**：组先「瘦身」（页数变少）再「回涨」时，
            //   残留的那个第 3 页会在回涨后被原样取出来，跳到最后一页。
            //   写回之后它就跟着当前页数一起收敛了（两条调用路径共用同一份状态）。
            if (p < 0) p = 0;
            if (p > max) p = max;
            _page[gv.Group] = p;
            return p;
        }

        private void SetPage(GroupView gv, int page)
        {
            if (gv == null || gv.Group == null) return;
            int max = PageCount(gv) - 1;
            if (page < 0) page = 0;
            if (page > max) page = max;
            _page[gv.Group] = page;
        }

        /// <summary>这一页第 slot 格是哪个应用；空格子返回 null。</summary>
        private AppEntry AppAt(GroupView gv, int slot)
        {
            if (gv == null) return null;
            if (slot < 0 || slot >= FolderCapacity) return null;
            int idx = PageOf(gv) * FolderCapacity + slot;
            if (idx < 0 || idx >= gv.Apps.Count) return null;
            return gv.Apps[idx];
        }

        /// <summary>这个格子是不是某个应用的图标（点一下直接启动）。</summary>
        private bool IsRealMiniSlot(GroupView gv, int slot)
        {
            return AppAt(gv, slot) != null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // 左键按在文件夹上（图标上也可以）就准备拖动；移动超过阈值才算真拖。
            // 图标上起拖要求更大的位移（见 OnMouseMove 的 need），不然「点一下启动」会被手抖吃掉。
            // 这里不判 PreviewMode：预览窗口本来就收不到真鼠标，而探针要靠这条路径验证拖动；
            // 真正会启动应用/关面板的是 OnMouseUp 里的点击分支，那条仍然有 PreviewMode 守卫。
            if (e.Button == MouseButtons.Left)
            {
                _downPoint = e.Location;
                _downValid = true;

                // 0) 抓在**固定把手**上 = 搬面板（优先判断，因为它就在顶部那一条里）
                if (_gripHit.Width > 0 && _gripHit.Contains(e.Location))
                {
                    _resizeZone = 0;
                    _panelMoving = true;
                    _panelMoved = false;
                    _panelGrabPos = PointToScreen(e.Location);   // 存屏幕坐标：窗口一动，客户区坐标就被抵消（见 OnMouseMove 说明）
                    _panelGrabBounds = Bounds;
                    BeginDrag();
                    Capture = true;
                    base.OnMouseDown(e);
                    return;
                }

                // 1) 抓在面板边缘 = 缩放整个面板
                _resizeZone = ResizeZoneAt(e.Location);
                if (_resizeZone != 0)
                {
                    _panelResizing = true;
                    _lastResizeZone = _resizeZone;
                    _panelGrabPos = PointToScreen(e.Location);   // 存屏幕坐标：窗口一动，客户区坐标就被抵消（见 OnMouseMove 说明）
                    _panelGrabBounds = Bounds;
                    BeginDrag();
                    Capture = true;
                    base.OnMouseDown(e);
                    return;
                }

                int index, slot;
                GroupHitTest(e.Location, out index, out slot);
                if (index >= 0)
                {
                    _dragFrom = index;
                    _dragTo = index;
                    _dragStart = e.Location;
                    _dragActive = false;
                    _dragOnIcon = slot >= 0;
                    Capture = true;   // 抓鼠标：确保松手一定收到 MouseUp，不会卡在「拖动中」
                }
                else
                {
                    // 2) 空白处按住 = 准备搬走整个面板；一动不动地松手仍然是原来的「点空白关闭」
                    _panelMoving = true;
                    _panelMoved = false;
                    _panelGrabPos = PointToScreen(e.Location);   // 存屏幕坐标：窗口一动，客户区坐标就被抵消（见 OnMouseMove 说明）
                    _panelGrabBounds = Bounds;
                    BeginDrag();
                    Capture = true;
                }
            }
            base.OnMouseDown(e);
        }

        /// <summary>光标不在任何分组上时，按网格位置算一个落点下标（这样拖到空白处、拖到末尾也能落）。</summary>
        private int SlotIndexAt(Point p)
        {
            if (_groups.Count == 0) return -1;
            if (p.Y < _groupTop || p.Y >= _groupTop + _groupHeight) return -1;   // ★半开区间：和 SetClip 的 [_groupTop, _groupTop+_groupHeight) 对齐（原来用 > 会多算最下面那一行，点得到但一个像素没画）
            int cols = Columns;
            int col = (p.X - _groupLeft + GapX / 2) / (TileW + GapX);
            int row = (p.Y - _groupTop + _scrollY + GapY / 2) / (TileH + GapY);
            if (col < 0) col = 0;
            if (col >= cols) col = cols - 1;
            if (row < 0) row = 0;
            int idx = row * cols + col;
            if (idx < 0) idx = 0;
            if (idx >= _groups.Count) idx = _groups.Count - 1;
            return idx;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // 缩放整个面板中
            if (_panelResizing)
            {
                DoResize(PointToScreen(e.Location));   // 传屏幕坐标（和 _panelGrabPos 一致）
                base.OnMouseMove(e);
                return;
            }

            // 搬走整个面板中（空白处按住拖动）
            if (_panelMoving)
            {
                int mdx, mdy;
                {
                    // ★位移必须用**屏幕坐标**算。窗口自己在动，用客户区坐标的话窗口位移会把鼠标位移
                    //   抵消掉一部分（鼠标真实走 2px、客户区只变 1px → 目标位置还是原地），
                    //   结果就是面板一步只走一半、然后突然跳一下 —— 用户说的「抖」「不跟手」就是这个。
                    Point nowScr = PointToScreen(e.Location);
                    mdx = nowScr.X - _panelGrabPos.X;
                    mdy = nowScr.Y - _panelGrabPos.Y;
                }                if (_panelMoved == false && Math.Abs(mdx) + Math.Abs(mdy) > Theme.Px(this, 3))
                    _panelMoved = true;
                if (_panelMoved)
                {
                    int nx = _panelGrabBounds.X + mdx;
                    int ny = _panelGrabBounds.Y + mdy;
                    Rectangle scr = _dragWorkArea.Width > 0 ? _dragWorkArea : CurrentWorkArea();
                    // 整块面板尽量留在屏幕可用区域里（面板比屏幕还大时至少保证上边和左边可见）
                    if (Width <= scr.Width)
                    {
                        if (nx < scr.Left) nx = scr.Left;
                        if (nx + Width > scr.Right) nx = scr.Right - Width;
                    }
                    else if (nx > scr.Left) nx = scr.Left;
                    if (Height <= scr.Height)
                    {
                        if (ny < scr.Top) ny = scr.Top;
                        if (ny + Height > scr.Bottom) ny = scr.Bottom - Height;
                    }
                    else if (ny > scr.Top) ny = scr.Top;
                    // 只记录目标并节流应用（分层窗口上每个事件都设位置会排队）
                    QueueDragTarget(new Rectangle(nx, ny, Width, Height), false);
                    Cursor = Cursors.SizeAll;
                    base.OnMouseMove(e);
                    return;
                }
            }

            // 拖动中：跟踪落点并高亮（松手才真的重排）。这里不查 MouseButtons —— 按下时已经 Capture，
            // 松手必定收到 OnMouseUp 把状态清掉，所以只看 _dragFrom 就够了（也方便自动化测试）。
            if (_dragFrom >= 0)
            {
                int need = _dragOnIcon ? Theme.Px(this, 12) : Theme.Px(this, 6);
                if (_dragActive == false
                    && Math.Abs(e.X - _dragStart.X) + Math.Abs(e.Y - _dragStart.Y) > need)
                    _dragActive = true;
                if (_dragActive)
                {
                    int ti, ts;
                    GroupHitTest(e.Location, out ti, out ts);
                    if (ti < 0) ti = SlotIndexAt(e.Location);   // 落在空白处也算一个落点
                    _dragTo = ti;
                    Cursor = Cursors.SizeAll;
                    Invalidate();
                    base.OnMouseMove(e);
                    return;
                }
            }

            int index, slot;
            GroupHitTest(e.Location, out index, out slot);
            bool gripHot = _gripHit.Width > 0 && _gripHit.Contains(e.Location);
            if (gripHot != _gripHot) { _gripHot = gripHot; Invalidate(); }
            int zNow = ResizeZoneAt(e.Location);
            if (zNow != _hoverZone) { _hoverZone = zNow; Invalidate(); }   // 靠近边缘就高亮那条边
            Cursor zoneCur = gripHot ? Cursors.SizeAll : ZoneCursor(zNow);
            if (index == _hoverGroup && slot == _hoverSlot)
            {
                Cursor = zoneCur != null ? zoneCur : (index >= 0 ? Cursors.Hand : Cursors.Default);
                base.OnMouseMove(e);
                return;
            }
            if (index != _hoverGroup) _wheelAcc = 0;   // 换到别的文件夹：别把上一个组攒的零头带过来
            _hoverGroup = index;
            _hoverSlot = slot;
            Cursor = zoneCur != null ? zoneCur : (index >= 0 ? Cursors.Hand : Cursors.Default);
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _wheelAcc = 0;   // 鼠标离开窗口：翻页攒的零头一起丢掉（否则移回来接着攒，会莫名其妙多翻一页）
            // ★★鼠标离开窗口时**边缘高亮也必须灭掉**（用户 2026-09-28 报：「这个侧边的蓝色怎么不自己消失呢，
            //   很影响美观」）。`_hoverZone` 只在 OnMouseMove 里更新 —— 鼠标一旦贴着边离开面板，
            //   就再也不会有 MouseMove 进来，那条蓝边会**一直亮着**。实测：鼠标在面板外 265px 处时，
            //   左边那条 2px 蓝线仍然 706/706 个像素通高亮着（探针里有回归断言钉着这条）。
            bool needRepaint = false;
            if (_hoverZone != 0) { _hoverZone = 0; needRepaint = true; }
            if (_hoverGroup >= 0 || _hoverSlot >= -1)
            {
                _hoverGroup = -1;
                _hoverSlot = -2;
                Cursor = Cursors.Default;
                needRepaint = true;
            }
            if (needRepaint) Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            try { Capture = false; } catch (Exception) { }

            // 面板缩放结束
            if (_panelResizing)
            {
                _panelResizing = false;
                _downValid = false;            // ★见下面 L1266 的说明：这三条 return 都必须作废「本次按下」
                EndDrag();                     // 停掉合并定时器并落实最后的大小
                bool vertical = (_lastResizeZone & 3) != 0;
                _resizeZone = 0;
                _lastResizeZone = 0;
                SavePanelBounds(vertical);
                Cursor = Cursors.Default;
                Invalidate();
                return;
            }

            // 面板搬动结束
            if (_panelMoving)
            {
                _panelMoving = false;
                _downValid = false;            // ★同上
                EndDrag();                     // 停掉合并定时器并落实最后的位置
                if (_panelMoved)
                {
                    _panelMoved = false;
                    SavePanelBounds(false);
                    Cursor = Cursors.Default;
                    Invalidate();
                    return;   // 搬过面板了，这一次不算「点空白关闭」
                }
                // 没移动 → 继续往下走：**点空白什么都不做**（关闭那条路早就删了；能走到这里说明
                // 按下去几乎没动，就当成一次普通点击交给下面处理）
            }

            // 拖动结束：按落点重排分组，并且这次不能再当成「点击」
            if (_dragActive)
            {
                int from = _dragFrom;
                int to = _dragTo;
                _dragActive = false;
                _downValid = false;            // ★同上
                _dragFrom = -1;
                _dragTo = -1;
                Cursor = Cursors.Default;
                if (to >= 0 && to != from) MoveGroupTo(from, to);
                Invalidate();
                return;
            }
            _dragFrom = -1;
            _dragTo = -1;

            // ★关键：只要按下去之后移动过几个像素，这一次就**不算点击** ——
            // 既不会启动应用，也不会把面板关掉。否则「想拖一下」很容易变成「点空白 = 关闭」。
            // ★★而且：命中这条判定时**不分鼠标键**，所以它会被「拖完之后的下一次右键」误伤 ——
            //    右键在 OnMouseDown 里不刷新 `_downPoint`（只有左键刷），于是右键拿的是上一次左键的
            //    按下点；拖完面板松手时 `_downPoint` 还停在拖动的起点（相隔几十像素）→ 这次右键
            //    直接在这里 return，**菜单不弹**，要再点一次才有。
            //    → 修法就是上面那三条 return 之前把 `_downValid` 作废（拖完了，这一次按下已经结束）。
            //    （2026-09-27 静态复现：`_panelMoved`/`_dragActive`/`_panelResizing` 三条路径都会跳过
            //      下面的 `_downValid = false`；探针里有对应的回归断言。）
            if (_downValid && Math.Abs(e.X - _downPoint.X) + Math.Abs(e.Y - _downPoint.Y) > Theme.Px(this, 3))
            {
                _downValid = false;
                Cursor = Cursors.Default;
                Invalidate();
                return;
            }
            _downValid = false;

            if (PreviewMode == false && _groups.Count > 0)
            {
                int index, slot;
                GroupHitTest(e.Location, out index, out slot);
                bool overTile = index >= 0;

                if (e.Button == MouseButtons.Left)
                {
                    if (overTile == false)
                    {
                        // 用户要求：点空白处**不再关闭面板**（那和「按住空白拖动面板」打架）。
                        // 关面板的途径：底栏「关闭」按钮、Esc、点「打开文件所在位置 / 打开软件所在文件夹」，
                        // 以及**设置里打开「启动应用后：关闭面板」时**的启动成功（默认不关，见 ShouldCloseAfterLaunch）。
                        return;
                    }
                    GroupView gv = _groups[index];
                    AppEntry mini = AppAt(gv, slot);
                    if (mini != null) { LaunchEntry(mini); return; }
                    OpenPicker(gv);
                    return;
                }

                if (e.Button == MouseButtons.Right)
                {
                    if (overTile == false)
                    {
                        ShowEmptyMenu();
                    }
                    else
                    {
                        // ★应用菜单和文件夹菜单已经**整合成一个**（见 ShowTileMenu / FillTileMenu）：
                        //   点中小图标 → 多一段「那个应用」；没点中 → 只有文件夹那段。
                        //   以前这里是 if (mini != null) ShowEntryMenu(...) else ShowGroupMenu(...) 两套菜单，
                        //   而小图标才二十来像素，差几像素就换一套 —— 用户抱怨「文件夹和应用不太好分别点」。
                        GroupView gv = _groups[index];
                        ShowTileMenu(gv, AppAt(gv, slot));
                    }
                    return;
                }
            }
            else if (PreviewMode == false && e.Button == MouseButtons.Left)
            {
                return;   // 一个分组都没有时点一下也不关（同上：别和拖动打架）
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // 鼠标停在「超过 9 个的大文件夹」上时，滚轮翻这一组的内页（iOS 文件夹那种翻页）；
            // 翻到头/尾再继续滚，就落回面板自身的滚动，这样两层滚动都不会卡住。
            int scrollDelta = e.Delta;      // 默认按本次事件的 Delta 折算；翻页攒满一格时改用它（见下）
            if (_hoverGroup >= 0 && _hoverGroup < _groups.Count)
            {
                GroupView hv = _groups[_hoverGroup];
                if (PageCount(hv) > 1)
                {
                    // ★翻页要攒满「一格」（Scroll.Notch）才动：高精度滚轮 / 触控板一次手势会送几十个
                    //   30~60 的碎 Delta，不攒的话一次滑动就把文件夹从头翻到尾。
                    _wheelAcc += e.Delta;
                    if (Math.Abs(_wheelAcc) < Theme.Scroll.Notch)
                        return;                       // 还没攒够一格：这次事件就这么消化掉，别半格半格地滚面板
                    // ★落回面板滚动时要用「攒出来的那一格」，不是最后一个碎 Delta ——
                    //   否则末页上滚一次只走 1/4 行（既不是整行、看着也像没反应）
                    scrollDelta = _wheelAcc > 0 ? Theme.Scroll.Notch : -Theme.Scroll.Notch;
                    int dir = _wheelAcc > 0 ? -1 : 1;
                    _wheelAcc = 0;
                    int before = PageOf(hv);
                    SetPage(hv, before + dir);
                    if (PageOf(hv) != before)
                    {
                        _hoverSlot = -2;
                        Invalidate();
                        return;
                    }
                    // 已经翻到第一页/最后一页：把这一格让给面板滚动（下面照常走）
                }
            }
            _wheelAcc = 0;   // 不在翻页语境里：别把零头留到下次

            // 整格滚轮 = **一整行**文件夹（行距 = TileH + GapY）：一格 120 时永远停在整行上，不会把一排格子切成半截。
            // 碎 Delta（高精度滚轮 / 触控板会送 30~60）按比例缩 —— 会停在半行，这是为了跟手，是有意的。
            int step = Theme.Scroll.WheelPixels(scrollDelta, TileH + GapY, Theme.Scroll.GridRowsPerNotch);
            if (step != 0) ScrollGroups(-step);
            base.OnMouseWheel(e);
        }

        private void ScrollGroups(int delta)
        {
            if (MaxScroll <= 0) return;
            int target = _scrollY + delta;
            if (target < 0) target = 0;
            if (target > MaxScroll) target = MaxScroll;
            if (target == _scrollY) return;
            _scrollY = target;
            _hoverGroup = -1;
            _hoverSlot = -2;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.SuppressKeyPress = true; return; }
            base.OnKeyDown(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // TransparencyKey / 亚克力会让 WinForms 重建窗口句柄，重建后样式全丢 ——
            // 所以每次句柄创建都重新钉一次置顶（预览模式不动窗，跳过）。
            // ★这里**不许**顺带 ShowWindow（PinTopMost(false)）：句柄第一次创建、以及亚克力改
            //   TransparencyKey 触发的那次重建，都发生在 OnLoad 摆位置**之前**，那时窗口还在默认的
            //   (0,0)。带上 SWP_SHOWWINDOW 就等于「把还没摆好的窗口强行显示出来」→ 左上角闪一下。
            if (PreviewMode == false) PinTopMost(false);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // WinForms 的 TopMost 属性在这种「无边框 + TransparencyKey/亚克力」窗口上不可靠
            // （属性为 true 但 WS_EX_TOPMOST 没设上），所以这里直接用 Win32 再钉一次。
            if (PreviewMode == false) PinTopMost();
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOREDRAW = 0x0008;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        /// <summary>把面板钉在最前面（用户要求「主动置顶」）。allowShow=false 时不带 SWP_SHOWWINDOW。</summary>
        /// <remarks>
        /// ★`SWP_SHOWWINDOW` 会把窗口**强行显示出来**，所以它只能在「窗口本来就该可见」的时机用（OnShown）。
        /// 句柄创建的时机（OnHandleCreated，含亚克力改 TransparencyKey 引起的那次重建）窗口还没摆好位置，
        /// 带上它就是「左上角闪一下」的直接原因之一（用户 2026-09-28 报的那条）。
        /// </remarks>
        private void PinTopMost()
        {
            PinTopMost(true);
        }

        private void PinTopMost(bool allowShow)
        {
            uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
            if (allowShow) flags |= SWP_SHOWWINDOW;
            try { SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, flags); }
            catch (Exception) { }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            // 以前这里是「失去焦点自动关」，但用户要求去掉：面板被别的窗口压住时点它，
            // 焦点先跑到别的窗口、面板就自己没了，看起来像「点一下就关了」。
            // 现在关面板只能靠：底栏「关闭」按钮 / Esc /（设置里打开时才有的）启动应用后关闭。
            // 都是用户主动触发 —— 失焦自关和点空白关闭都已经删掉了。
            base.OnDeactivate(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _dragTimer.Stop(); } catch (Exception) { }
            try { PersistSettings(); }
            // ★关面板是**最后一次落盘**：异常被吞掉的话，这一次会话改的东西全丢，而且日志里
            //   一个字都不会有（用户只会觉得"我改的分组怎么没了"）。所以这里必须留痕。
            catch (Exception ex) { try { ConfigStore.Log(Program.AppDir, "关面板时最后一次落盘抛异常（本次改动可能没保存）：" + ex.GetType().Name + " " + ex.Message); } catch (Exception) { } }
            base.OnFormClosed(e);
        }

        /// <summary>
        /// 落盘设置。返回是否成功；预览 / 自检模式一概不写：出图验证不该动用户真实的分组数据。
        /// 写成功后往 log.txt 记一行「N 组 / 共 M 个应用」——分组被人改没了的时候，这行日志就是证据。
        /// </summary>
        private bool PersistSettings()
        {
            if (PreviewMode) return false;
            SaveResult r;      // 枚举在 BreadLauncher 命名空间下，不在 ConfigStore 里
            if (string.IsNullOrEmpty(_settingsFile))
            {
                r = ConfigStore.SaveSettingsResult(ConfigStore.SettingsFile(Program.AppDir), _settings);
            }
            else
            {
                r = ConfigStore.SaveSettingsResult(_settingsFile, _settings);
            }
            // ★只有「真的写进去了」才记那行分组概况日志：内容没变时每天开关面板几十次，
            //   每都记一遍只会把日志刷满，反而找不到「谁把分组改没了」那一行。
            if (r == SaveResult.Written) LogGroupState();
            if (r == SaveResult.Failed && string.IsNullOrEmpty(_lastWriteFailText))
                _lastWriteFailText = ConfigStore.LastSettingsWriteError ?? ConfigStore.LastWriteError;
            return r != SaveResult.Failed;
        }

        /// <summary>
        /// 算出小图标那一格「名字常驻显示」要占的地方。
        /// ★为什么单独一个方法：探针要能**直接断言**「小档位下宁可不画名字，也不把图标压到认不出」
        ///   （格子只有 40 来像素宽，名字占 14px 之后图标会掉到 14~18px）。
        /// ★注意这是**要求**，真正画的时候还要跟格子做交集（下面 WithLabel 那一段）。
        /// </summary>
        private bool NameLabelLayout(Rectangle cell, int miniGap, int tw, int thAll, int x, int y,
                                     out Rectangle inner, out Rectangle nameR)
        {
            int labelH = Theme.Px(this, 14);
            int want = Math.Max(Theme.Px(this, 12), cell.Height - labelH - Theme.Px(this, 2));
            // ★阈值为什么是 Px(17)：三档实测（MiniBox = (TileW - 2*MiniPad - 2*MiniGap) / 3）——
            //     FolderScale  85 → 格 30px → 让出名字后图标只剩 14px（认不出）→ **不画**
            //     FolderScale 100 → 格 35px → 剩 19px → 还认得 → **画**（默认档必须画！）
            //     FolderScale 120 → 格 42px → 剩 26px → **画**
            //   ★踩过的坑：阈值一度定成 Px(22)，结果**默认档 100 也变成不画** —— 功能静默失效，
            //     而当时那条探针断言写成了 `(drew && h>=X) || drew == false`（后半句恒真）根本抓不到。
            //     现在断言拆成「85 不画 / 100 要画 / 120 要画」三条分别查。
            if (want < Theme.Px(this, 17))
            {
                inner = cell;
                nameR = Rectangle.Empty;
                return false;
            }
            inner = new Rectangle(cell.X, cell.Y, cell.Width, want);
            // 标签**借用两侧间隙**（每个格子各借一半），能多显示一个字左右；不会压到邻居
            int borrow = Math.Max(0, miniGap / 2);
            nameR = new Rectangle(cell.X - borrow, inner.Bottom + Theme.Px(this, 1),
                                  cell.Width + borrow * 2, labelH);
            nameR = Rectangle.Intersect(nameR, new Rectangle(x, y, tw, thAll));   // 别跑出文件夹方块
            return true;
        }

        /// <summary>把当前分组概况写进日志（谁在什么时候把分组改成了什么样，事后查得到）。</summary>
        private void LogGroupState()
        {
            try
            {
                if (_settings == null) return;
                int total = 0;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (AppGroup g in _settings.Groups)
                {
                    if (g == null) continue;
                    total += g.Keys.Count;
                    if (sb.Length > 0) sb.Append("、");
                    sb.Append(g.Name).Append("=").Append(g.Keys.Count);
                }
                ConfigStore.Log(Program.AppDir, "落盘：" + _settings.Groups.Count + " 组 / 共 " + total + " 个应用（" + sb.ToString() + "）");
            }
            catch (Exception) { }
        }
        // ============================================================
        // 数据
        // ============================================================
        private void LoadData(bool forceScan)
        {
            AppCache cache = ConfigStore.LoadCache(Program.AppDir);
            List<AppEntry> list = null;
            bool usedCache = false;

            if (forceScan == false && cache != null && cache.Apps != null && cache.Apps.Count > 0)
            {
                list = AppsFolderScanner.FromCache(cache);
                usedCache = list.Count > 0;
            }

            if (list == null || list.Count == 0)
            {
                try
                {
                    list = AppsFolderScanner.Scan(Program.AppDir, ScanDirsSnapshot());
                    // ★预览/探针模式**不写应用列表缓存**：以前只挡住了 settings 落盘（PersistSettings 里判
                    //   PreviewMode），缓存是照写的 —— 探针一跑就把临时目录的条目写进用户真实的
                    //   cache\apps-cache.json，而且 ScannedUtc 变成「刚刚」→ 12 小时内不再自动刷新，
                    //   于是那几条指向已删文件的幽灵条目会一直挂在候选里（实测踩到过）。
                    if (list.Count > 0 && PreviewMode == false) ConfigStore.SaveCache(Program.AppDir, AppsFolderScanner.ToCache(list));
                }
                catch (Exception ex)
                {
                    ConfigStore.Log(Program.AppDir, "扫描失败：" + ex.Message);
                    list = cache == null ? new List<AppEntry>() : AppsFolderScanner.FromCache(cache);
                }
            }

            _all = list;
            ApplyMetadata();
            RefreshContent();

            if (usedCache && ConfigStore.IsCacheStale(cache, CacheRefreshMinutes)) StartBackgroundRefresh();
        }

        private void StartBackgroundRefresh()
        {
            int gen;
            lock (this) { gen = _scanGen; }

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try
                {
                    List<AppEntry> fresh = AppsFolderScanner.Scan(Program.AppDir, ScanDirsSnapshot());
                    if (fresh.Count == 0) return;
                    if (PreviewMode == false) ConfigStore.SaveCache(Program.AppDir, AppsFolderScanner.ToCache(fresh));   // 同上：预览不写用户缓存
                    try
                    {
                        if (IsDisposed == false && IsHandleCreated)
                        {
                            BeginInvoke(new Action(delegate
                            {
                                if (gen != _scanGen) return; // 期间用户手动刷新过：别拿更旧的结果盖掉新数据
                                _all = fresh;
                                ApplyMetadata();
                                RefreshContent();
                            }));
                        }
                    }
                    catch (Exception) { }
                }
                catch (Exception ex)
                {
                    ConfigStore.Log(Program.AppDir, "后台刷新失败：" + ex.Message);
                }
            });
        }

        /// <summary>首次运行时预置一个「常用」分组（只做一次；以后用户删掉了就不再自动加回）。</summary>
        private void SeedDefaultGroups()
        {
            if (_settings.Seeded) return;

            if (_settings.Groups.Count == 0)
            {
                AppGroup g = new AppGroup("常用");
                string[] want = new string[]
                {
                    "文件资源管理器", "设置", "计算器", "截图工具", "终端", "画图",
                    "记事本", "Microsoft Store", "微信", "QQ", "Edge", "Google Chrome"
                };
                foreach (string name in want)
                {
                    if (g.Keys.Count >= 6) break;
                    foreach (AppEntry en in _all)
                    {
                        if (string.Equals(en.Name, name, StringComparison.CurrentCultureIgnoreCase))
                        {
                            g.Add(en.Key);
                            break;
                        }
                    }
                }
                _settings.Groups.Add(g);
            }
            // 只有真写进盘了才算「预置过」；写不进去（只读目录等）就保持未预置，下次启动还能再来一遍
            // ★AlreadyCurrent 也算「盘上是对的」：内容没变说明某次已经写成功过，再算成「没预置」
            //   就会每次启动都往「常用」组里塞一遍（虽然 Normalize 会去重，但白忙活还容易出岔）。
            if (PersistSettings()) _settings.Seeded = true;
        }

        private void ApplyMetadata()
        {
            long now = UnixNow();
            SeedDefaultGroups();
            AppNames.ApplyAll(_settings, _all);      // 右键起的自定义显示名（跨重启生效）
            bool firstRun = _settings.FirstSeen.Count == 0;

            foreach (AppEntry en in _all)
            {
                en.IsPinned = InAnyGroup(en.Key);
                long seen;
                if (_settings.FirstSeen.TryGetValue(en.Key, out seen))
                {
                    en.IsNew = firstRun == false && (now - seen) < Settings.NewBadgeDays * 86400L;
                }
                else
                {
                    _settings.FirstSeen[en.Key] = now;
                    en.IsNew = false;
                }
            }
            PersistSettings();
        }

        private static long UnixNow()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>按配置顺序把 Key 解析成 AppEntry；同一个应用只留在最先出现的组里。</summary>
        private void BuildGroupViews()
        {
            _groups = new List<GroupView>();

            Dictionary<string, AppEntry> byKey = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (AppEntry en in _all)
                if (byKey.ContainsKey(en.Key) == false) byKey[en.Key] = en;

            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AppGroup ag in _settings.Groups)
            {
                if (ag == null) continue;
                GroupView gv = new GroupView();
                gv.Group = ag;
                foreach (string key in ag.Keys)
                {
                    if (string.IsNullOrEmpty(key)) continue;
                    if (used.Contains(key)) continue;
                    AppEntry en;
                    if (byKey.TryGetValue(key, out en) == false) continue; // 扫不到的条目先留在配置里，下次可能回来
                    used.Add(key);
                    gv.Apps.Add(en);
                }
                _groups.Add(gv);
            }
        }

        private void RefreshContent()
        {
            BuildGroupViews();
            _scrollY = 0;
            _hoverGroup = -1;
            _hoverSlot = -2;

            foreach (GroupView gv in _groups)
                foreach (AppEntry en in gv.Apps)
                    RequestIcon(en, MiniIconSize);

            ApplyPanelHeight();
            LayoutUi();
            Invalidate(true);
        }

        private void RequestIcon(AppEntry en, int size)
        {
            if (en == null) return;
            string k = en.Key + "|" + size;
            lock (_requested)
            {
                if (_requested.Contains(k)) return;
                _requested.Add(k);
            }
            int gen;
            lock (this) { gen = _iconGen; _pendingIcons++; }
            _icons.BeginGet(en, size, delegate(AppEntry e, Bitmap b) { OnIconReady(gen, e, b); });
        }

        /// <summary>
        /// gen 用来作废「刷新应用列表」之前排队的那批回调：
        /// 旧写法里刷新时把 _pendingIcons 直接清 0，在途回调回来还会 --，计数被减成负数，
        /// 于是 --preview 的等待循环立刻为假，图还没取全就截图了。
        /// </summary>
        private void OnIconReady(int gen, AppEntry en, Bitmap bmp)
        {
            lock (this)
            {
                if (gen != _iconGen) return;
                _pendingIcons--;
            }
            try
            {
                if (IsDisposed || Disposing || IsHandleCreated == false) return;
                BeginInvoke(new Action(delegate
                {
                    if (IsDisposed) return;
                    Invalidate();
                }));
            }
            catch (Exception) { }
        }
        // ============================================================
        // 分组操作
        // ============================================================
        private void RemoveKeyEverywhere(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            foreach (AppGroup g in _settings.Groups)
                if (g != null) g.Remove(key);
        }

        private void RemoveFromGroup(GroupView gv, AppEntry en)
        {
            if (gv == null || en == null) return;
            gv.Group.Remove(en.Key);
            SaveGroups();
        }

        private void MoveToGroup(AppGroup target, AppEntry en)
        {
            if (target == null || en == null) return;
            RemoveKeyEverywhere(en.Key);
            target.Add(en.Key);
            SaveGroups();
        }

        private void MoveToNewGroup(AppEntry en)
        {
            if (en == null) return;
            string name = AskText("新建分组", "新分组", "新建");
            if (string.IsNullOrEmpty(name)) return;
            name = name.Trim();
            if (name.Length == 0) return;
            if (GroupNameExists(name)) name = UniqueGroupName(name);

            AppGroup g = new AppGroup(name);
            _settings.Groups.Add(g);
            RemoveKeyEverywhere(en.Key);
            g.Add(en.Key);
            SaveGroups();
        }

        private void MoveGroup(GroupView gv, int delta)
        {
            int i = GroupIndexOf(gv);
            if (i < 0) return;
            int j = i + delta;
            if (j < 0 || j >= _settings.Groups.Count) return;
            AppGroup tmp = _settings.Groups[i];
            _settings.Groups[i] = _settings.Groups[j];
            _settings.Groups[j] = tmp;
            SaveGroups();
        }

        /// <summary>拖动落点：把第 from 个分组挪到第 to 个位置（其余顺延），并落盘。</summary>
        private void MoveGroupTo(int from, int to)
        {
            if (from < 0 || to < 0) return;
            if (from >= _settings.Groups.Count || to >= _settings.Groups.Count) return;
            if (from == to) return;
            AppGroup g = _settings.Groups[from];
            _settings.Groups.RemoveAt(from);
            _settings.Groups.Insert(to, g);
            SaveGroups();
        }

        /// <summary>文件夹大小（百分比）：85 小 / 100 标准 / 120 大。</summary>
        private void SetFolderScale(int percent)
        {
            if (percent < 60) percent = 60;
            if (percent > 200) percent = 200;
            if (_settings.FolderScale == percent) return;
            _settings.FolderScale = percent;
            PersistSettings();
            RefreshContent();
        }

        private int GroupIndexOf(GroupView gv)
        {
            if (gv == null) return -1;
            for (int i = 0; i < _settings.Groups.Count; i++)
                if (object.ReferenceEquals(_settings.Groups[i], gv.Group)) return i;
            return -1;
        }

        private bool InAnyGroup(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            foreach (AppGroup g in _settings.Groups)
                if (g != null && g.Contains(key)) return true;
            return false;
        }

        private void SaveGroups()
        {
            PersistSettings();
            RefreshContent();
        }

        private bool GroupNameExists(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            foreach (AppGroup g in _settings.Groups)
                if (g != null && string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase)) return true;
            return false;
        }

        private string UniqueGroupName(string baseName)
        {
            if (GroupNameExists(baseName) == false) return baseName;
            for (int i = 2; i < 1000; i++)
            {
                string cand = baseName + " " + i;
                if (GroupNameExists(cand) == false) return cand;
            }
            return baseName + " " + DateTime.Now.Ticks;
        }

        private GroupView FindGroupView(AppGroup g)
        {
            foreach (GroupView gv in _groups)
                if (object.ReferenceEquals(gv.Group, g)) return gv;
            return null;
        }

        /// <summary>弹一个文本输入框（模态）；取消返回 null。</summary>
        private string AskText(string title, string initial, string okText)
        {
            string result = null;
            _modalOpen = true;
            try { result = TextPromptForm.Ask(this, title, initial, okText); }
            finally { _modalOpen = false; }
            return result;
        }

        private void NewGroup()
        {
            string name = AskText("新建分组", "新分组", "新建");
            if (string.IsNullOrEmpty(name)) return;
            name = name.Trim();
            if (name.Length == 0) return;
            if (GroupNameExists(name)) name = UniqueGroupName(name);

            AppGroup g = new AppGroup(name);
            _settings.Groups.Add(g);
            PersistSettings();
            RefreshContent();

            GroupView gv = FindGroupView(g);
            if (gv != null) OpenPicker(gv);
        }

        private void RenameGroup(GroupView gv)
        {
            if (gv == null) return;
            string name = AskText("重命名分组", gv.Name, "保存");
            if (string.IsNullOrEmpty(name)) return;
            name = name.Trim();
            if (name.Length == 0) return;
            if (string.Equals(name, gv.Name, StringComparison.CurrentCultureIgnoreCase)) return;
            if (GroupNameExists(name))
            {
                _modalOpen = true;
                try
                {
                    MessageBox.Show(this, "已经有一个叫「" + name + "」的分组了，换个名字吧。", "BreadLauncher",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                finally { _modalOpen = false; }
                return;
            }
            gv.Group.Name = name;
            SaveGroups();
        }

        private void DeleteGroup(GroupView gv)
        {
            if (gv == null) return;
            DialogResult r;
            _modalOpen = true;
            try
            {
                r = MessageBox.Show(this,
                    "删除分组「" + gv.Name + "」？\n\n组里的 " + gv.Apps.Count + " 个应用会回到「未分组」，不会卸载任何东西。",
                    "BreadLauncher", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            }
            finally { _modalOpen = false; }

            if (r == DialogResult.OK)
            {
                _settings.Groups.Remove(gv.Group);
                SaveGroups();
            }
        }

        /// <summary>还没进任何分组的应用，按名字排好序（给选择器用）。</summary>
        /// <summary>自定义扫描目录的快照：扫描可能在后台线程跑，直接传 `_settings.ScanDirs` 会
        /// 撞上用户在界面里加/删目录（「集合已被修改」）。拷一份最省心。</summary>
        private List<string> ScanDirsSnapshot()
        {
            try
            {
                if (_settings == null || _settings.ScanDirs == null || _settings.ScanDirs.Count == 0)
                    return null;
                return new List<string>(_settings.ScanDirs);
            }
            catch (Exception) { return null; }
        }

        /// <summary>选择器里改完「自定义扫描文件夹」之后调用：落盘 + 重扫（含新目录），并把新的候选列表交回去。
        /// ★必须重扫：目录是刚加的，`_all` 里还没有那些条目；重扫顺带也刷新了面板。</summary>
        internal List<AppEntry> RescanForPicker()
        {
            RescanAll(true, "自定义目录改了之后");
            return BuildCandidates();
        }

        /// <summary>
        /// 全量重扫（忽略缓存），**同步跑在界面线程**上（`LoadData(true)` → `AppsFolderScanner.Scan`）：
        /// 本地目录两百多毫秒（实测整进程跑一次 `--scan` 约 470ms，含 .NET 启动），
        /// 目录多了 / 目录很大就会更久。慢的时候至少让用户看到「在忙」，别以为面板死了。
        /// （真正的后台化是笔更大的改动：要处理回调、换代、失败回滚 —— 先给可见反馈 + 兜住异常。）
        /// </summary>
        /// <param name="persist">扫描前先把设置落盘（自定义目录刚改过时必须；其余场合会被
        /// 「内容没变就跳过」挡住，无害）。</param>
        /// <param name="why">只进日志：出问题时能看出这次重扫是谁触发的。</param>
        private void RescanAll(bool persist, string why)
        {
            if (persist) PersistSettings();
            Cursor prev = Cursor;
            try
            {
                Cursor = Cursors.WaitCursor;
                Application.DoEvents();
                LoadData(true);          // forceScan：忽略缓存，重新扫一遍（含自定义目录）
            }
            catch (Exception ex)
            {
                ConfigStore.Log(Program.AppDir, "重扫失败（" + why + "）：" + ex.Message);
            }
            finally
            {
                Cursor = prev;
            }
        }

        /// <summary>打开「添加应用」之前该不该先扫一遍：缓存旧了 / 压根读不出来 → 扫。
        /// ★抽成方法是为了**能被探针直接断言**（窗口里那一段很难稳定复现）。</summary>
        internal bool ShouldRescanBeforePicker()
        {
            try
            {
                AppCache c = ConfigStore.LoadCache(Program.AppDir);
                return ConfigStore.IsCacheStale(c, PickerRescanMinutes);
            }
            catch (Exception) { return true; }   // 读不出来 = 不敢信这份名单，扫
        }

        private List<AppEntry> BuildCandidates()
        {
            List<AppEntry> r = new List<AppEntry>();
            foreach (AppEntry en in _all)
            {
                if (string.IsNullOrEmpty(en.Key)) continue;
                if (InAnyGroup(en.Key)) continue;
                r.Add(en);
            }
            r.Sort(delegate(AppEntry a, AppEntry b)
            {
                // 桌面上有的排前面：用户更可能在找自己桌面上的那些
                if (a.OnDesktop != b.OnDesktop) return a.OnDesktop ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return r;
        }

        private void OpenPicker(GroupView gv)
        {
            if (gv == null) return;

            // ★★开窗之前先补扫一遍（2026-10-01，用户报「刚装的软件在「添加应用」里找不到」）：
            //   这个窗口是**模态**的（ShowDialog），开着它的时候主面板点不动、右键菜单根本弹不出来，
            //   所以「名单旧了」这件事必须在**开窗之前**解决；否则用户只能：关窗 → 右键 → 刷新 →
            //   重新点进那个分组 → 再开窗（四步，而且「刷新」现在已经从右键撤掉了）。
            //   阈值 PickerRescanMinutes：同一个会话里连着开几次窗口不会反复扫。
            //   顺带治好「候选为 0 时那句『都已经分组了』是假话」——名单旧了会被误报成"都加过了"。
            if (ShouldRescanBeforePicker()) RescanAll(false, "打开添加应用前");

            List<AppEntry> candidates = BuildCandidates();
            if (candidates.Count == 0)
            {
                _modalOpen = true;
                try
                {
                    MessageBox.Show(this, "扫描到的应用都已经分组了。\n刚装了新软件的话，试试「设置 → 刷新应用列表」。",
                        "BreadLauncher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                finally { _modalOpen = false; }
                return;
            }

            // 一次打开可以连着加多个：每加一个就地落盘 + 刷新面板，窗口不关（用户抱怨过「加一个就关、不知道加了几个」）
            AppGroup group = gv.Group;
            _modalOpen = true;
            try
            {
                AppPickerForm.PickMany(this, _icons, gv.Name, gv.Apps.Count, candidates,
                    delegate(AppEntry pick)
                    {
                        RemoveKeyEverywhere(pick.Key);
                        group.Add(pick.Key);
                        SaveGroups();
                    },
                    _settings,
                    delegate { return RescanForPicker(); },
                    delegate { PersistSettings(); });
            }
            finally { _modalOpen = false; }
        }

        /// <summary>
        /// 列出整组应用（比面板上那 9 格更完整的清单；配合组内分页用）。
        /// 点谁就启动谁；启动后照常关面板 = 退出进程。
        /// </summary>
        private void ShowAllApps(GroupView gv)
        {
            if (gv == null) return;

            AppEntry pick = null;
            _modalOpen = true;
            try { pick = GroupAppsForm.Show(this, _icons, gv.Group, gv.Apps, SaveGroups, _settings); }
            finally { _modalOpen = false; }

            if (pick != null) LaunchEntry(pick);
        }
        // ============================================================
        // 启动 / 右键菜单
        // ============================================================
        /// <summary>启动成功之后要不要关面板。★默认**不关**：用户经常要连着启动好几个应用。
        /// 抽成独立方法是为了能被自检直接断言（真启动一个程序会有窗口/副作用，不适合放进自检）。</summary>
        internal bool ShouldCloseAfterLaunch()
        {
            return _settings != null && _settings.CloseAfterLaunch;
        }

        /// <summary>设置菜单里那个开关。</summary>
        private void ToggleCloseAfterLaunch()
        {
            if (_settings == null) return;
            _settings.CloseAfterLaunch = !_settings.CloseAfterLaunch;
            PersistSettings();
        }

        private void LaunchEntry(AppEntry en)
        {
            if (en == null) return;
            _suppressDeactivate = true;
            string err;
            if (Launcher.Launch(en, out err))
            {
                // ★启动成功：默认**留在面板上**，方便连着启动下一个；想恢复「启动就关」在设置里打开。
                if (ShouldCloseAfterLaunch())
                {
                    Close();
                    return;
                }
                _suppressDeactivate = false;
                return;
            }
            _suppressDeactivate = false;
            _modalOpen = true;
            try
            {
                MessageBox.Show(this, "启动失败：\n" + en.DisplayName + "\n\n" + err, "BreadLauncher",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { _modalOpen = false; }
        }

        /// <summary>自检用：和右键弹出的**同源**菜单副本（不用真的弹窗）。en = null 表示点的是文件夹背景。</summary>
        internal ContextMenuStrip BuildTileMenu(GroupView gv, AppEntry en)
        {
            ContextMenuStrip menu = NewMenu(false);
            FillTileMenu(menu, gv, en);
            return menu;
        }

        /// <summary>
        /// 菜单里的「小节标题」：**不可点、灰字、加粗**，用来把菜单分成一整块一整块。
        /// ★用户 2026-09-28 明确要求：块与块之间**不要横线**（线多了容易看串哪一条属于哪一块），
        ///   靠标题本身分块 —— 所以标题**上面**要多留一点空隙，视觉上它才属于**下面**那一块。
        /// ★★这里踩过一次坑（子智能体 F 量出来的）：**别用 `Padding`** —— 标题文字是贴着 item 顶边画的，
        ///   `Padding.Top` 加出来的空白会落在文字**下面**，结果变成「标题紧贴上一块、离自己那块远」，
        ///   正好是反的（实测：普通项间距 12px，标题下方 19px）。改用 `Margin`：它在 item **外面**，
        ///   加出来的才是标题上方的空隙。改这段必须出图再量一次（见 `build\menu-tile-app.png`）。
        /// </summary>
        private ToolStripMenuItem HeaderItem(string text, bool gapAbove)
        {
            // '&' 在菜单里是助记符（AT&amp;T 会显示成 ATT），标题里如实显示要写成 '&&'
            ToolStripMenuItem it = new ToolStripMenuItem((text ?? "").Replace("&", "&&"));
            it.Enabled = false;                                        // 点不动；渲染器把不可点的画成 Theme.TextDim
            it.Font = Theme.Ui(9.75f, FontStyle.Bold);                 // Theme.Ui 有缓存，不会漏字体
            it.Margin = new Padding(0, Theme.Px(this, gapAbove ? 8 : 3), 0, 0);
            return it;
        }

        /// <summary>
        /// 跳出一个文件夹格子上的右键菜单。**应用和文件夹共用这一个**（用户 2026-09-28 要求整合）：
        /// 以前是「点中小图标 → 应用菜单 / 差几像素没点中 → 文件夹菜单」两套，而小图标才二十来像素，
        /// 差一点点就换一套菜单，用户抱怨「文件夹和应用不太好分别点」。
        /// 现在：点中图标 → 菜单里多一段「那个应用」；没点中 → 只有文件夹那段。
        /// **两种情况下文件夹操作都拿得到**，不用再瞄准。
        /// </summary>
        private void ShowTileMenu(GroupView gv, AppEntry en)
        {
            if (gv == null) return;
            _suppressDeactivate = true;
            try
            {
                ContextMenuStrip menu = NewMenu(false);
                FillTileMenu(menu, gv, en);
                menu.Closed += delegate { _suppressDeactivate = false; };
                KeepMenu(menu);
                menu.Show(this, PointToClient(Cursor.Position));
            }
            catch (Exception)
            {
                _suppressDeactivate = false;
            }
        }

        /// <summary>
        /// 菜单内容（抽出来给自检断言；用户看到的就是这些）。**一整块一整块，块内不插横线**：
        ///   ① 应用块 —— 只有点中小图标才有，标题就是那个应用的名字；
        ///   ② 文件夹块 —— 永远都有，标题是「组名」文件夹。
        /// ★2026-10-01：「刷新应用列表」原来在 ③「面板」块里。用户提出**它不该待在右键** ——
        ///   真正需要刷新的时刻是「刚装的软件在「添加应用」里找不到」，而那个窗口是**模态**的，
        ///   开着它的时候主面板点不动、右键菜单根本弹不出来 → 于是整块撤掉，改成
        ///   「打开添加应用前按需自动重扫」+「窗口里一个『重新扫描』按钮」。
        ///   ⚠ 设置菜单里那一份**保留**（不打扰的兜底入口），别顺手一起删了。
        /// </summary>
        private void FillTileMenu(ContextMenuStrip menu, GroupView gv, AppEntry en)
        {
            if (gv == null) return;

            // ---------- ① 应用块 ----------
            if (en != null)
            {
                GroupView owner = gv;
                AppEntry entry = en;
                menu.Items.Add(HeaderItem(entry.DisplayName, false));

                menu.Items.Add("从「" + gv.Name + "」移除", null, delegate { RemoveFromGroup(owner, entry); });

                ToolStripMenuItem move = new ToolStripMenuItem("移到其他分组");
                // ★「新建分组…」放**最前面**（子智能体 H 指出）：子菜单是扁平列表、长了会被顶到屏幕外，
                //   垫底的那个最先看不见 —— 而它是「这个应用不属于任何一个已有分组」时唯一的出路。
                move.DropDownItems.Add("新建分组…", null, delegate { MoveToNewGroup(entry); });
                bool anyOther = false;
                foreach (GroupView other in _groups)
                {
                    if (object.ReferenceEquals(other.Group, owner.Group)) continue;
                    GroupView target = other;
                    move.DropDownItems.Add(other.Name, null, delegate { MoveToGroup(target.Group, entry); });
                    anyOther = true;
                }
                // 这条分隔线在**子菜单里面**（把「新建分组…」和「现有分组」分开），跟主菜单的「分块不画线」无关
                if (anyOther) move.DropDownItems.Add(new ToolStripSeparator());
                menu.Items.Add(move);

                // ★文字里要写清**当前状态**：这个菜单没有勾选框的边距（`NewMenu(false)` → `ShowImageMargin=false`），
                //   `Checked` 属性其实**画不出来**（子智能体 H 逐像素验过：一个像素都不画）。
                //   所以按项目一贯的规矩「开关 = 文字写明已开启/已关闭」，别让人靠猜。
                ToolStripMenuItem labelItem = new ToolStripMenuItem(
                    "名字常驻显示（画在图标下）：" + (entry.ShowName ? "已开启" : "已关闭"));
                labelItem.Checked = entry.ShowName;
                labelItem.Click += delegate { SetNameLabel(entry, !entry.ShowName); };
                menu.Items.Add(labelItem);

                menu.Items.Add("重命名…", null, delegate { RenameEntry(entry); });
                if (string.IsNullOrEmpty(entry.CustomName) == false)
                    menu.Items.Add("恢复原名（" + entry.Name + "）", null, delegate { SetDisplayName(entry, null); });
                if (entry.IsRealFile)
                    menu.Items.Add("打开文件所在位置", null, delegate { OpenFileLocation(entry); });
                menu.Items.Add("复制显示名", null, delegate { CopyText(entry.DisplayName); });
            }

            // ---------- ② 文件夹块 ----------
            menu.Items.Add(HeaderItem("「" + gv.Name + "」文件夹", en != null));
            GroupView g = gv;
            menu.Items.Add("添加应用…", null, delegate { OpenPicker(g); });
            if (gv.Apps.Count > 0)
                menu.Items.Add("查看全部（" + gv.Apps.Count + "）…", null, delegate { ShowAllApps(g); });
            menu.Items.Add("重命名分组…", null, delegate { RenameGroup(g); });
            menu.Items.Add("左移一位", null, delegate { MoveGroup(g, -1); });
            menu.Items.Add("右移一位", null, delegate { MoveGroup(g, 1); });
            menu.Items.Add("删除分组", null, delegate { DeleteGroup(g); });   // 里面自带确认框

            // ---------- ③ 原来的「面板」块：2026-10-01 整块撤掉 ----------
            // 里面只有「刷新应用列表」一项，撤掉之后这块就空了 —— 空标题不能留（见方法头的原因）。
            // 手动刷新现在在「设置」菜单里有一份，「添加应用」窗口里也有一份。
        }

        /// <summary>
        /// 自定义显示名的**核心**（不弹窗，自检直接断言这条）：写进 `Settings.Renames` → 落盘 → 重画。
        /// 名字设成空 = 恢复原名。★只改显示名，`Name`（扫描到的原名）和 `Key` 都不动 —— 去重、缓存、启动都靠它们。
        /// </summary>
        internal void SetDisplayName(AppEntry en, string name)
        {
            if (en == null) return;
            AppNames.Set(_settings, en, name);
            PersistSettings();
            Invalidate();
        }

        /// <summary>
        /// 开/关「名字常驻显示」（不弹窗，自检直接断言这条）：写 `Settings.NameLabels` → 落盘 → 重画。
        /// </summary>
        internal void SetNameLabel(AppEntry en, bool on)
        {
            if (en == null) return;
            AppNames.SetLabel(_settings, en, on);
            PersistSettings();
            Invalidate();
        }

        /// <summary>右键 →「重命名…」：弹输入框，预填当前显示名；留空 = 恢复原名。</summary>
        private void RenameEntry(AppEntry en)
        {
            if (en == null) return;
            string now = en.DisplayName;
            string name = AskText("重命名（留空恢复原名）", now, "保存");
            if (name == null) return;                 // 取消
            name = name.Trim();
            if (string.Equals(name, now, StringComparison.Ordinal)) return;   // 没变
            SetDisplayName(en, string.Equals(name, en.Name, StringComparison.Ordinal) ? null : name);
        }

        /// <summary>
        /// 建一个统一风格的右键菜单。★**只管建、不管 Dispose** —— 收尾交给 <see cref="KeepMenu"/>。
        /// `ContextMenuStrip` 是 `Component`，不 Dispose 就一直挂在窗体的组件表里（还有它的
        /// `DarkMenuRenderer` 和每一项的 `Font` 引用）；而这些菜单是**每次右键都新建一个**的，
        /// 用一天下来就是几百个。
        /// </summary>
        private ContextMenuStrip NewMenu(bool showChecks)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.ShowImageMargin = showChecks;
            menu.Font = Theme.Ui(9.75f);
            return menu;
        }

        private ContextMenuStrip _lastMenu;

        /// <summary>
        /// 记下「这一个」是当前菜单，并把**上一个**收掉。
        /// ★为什么不是「在 Closed 回调里 Dispose」：`menu.Show(...)` 会一直阻塞到菜单关闭，
        ///   如果 Closed 里就 Dispose，`Show` 返回时对象已经没了 —— 历史事故（AGENTS.md 坑 13）。
        ///   换成「下一个菜单弹出来时再收上一个」：那时上一个早已关闭、`Show` 也早已返回，安全。
        /// </summary>
        private void KeepMenu(ContextMenuStrip menu)
        {
            ContextMenuStrip old = _lastMenu;
            _lastMenu = menu;
            if (old != null && object.ReferenceEquals(old, menu) == false)
            {
                try { old.Dispose(); }
                catch (Exception) { }
            }
        }

        /// <summary>自检用：和右键空白处弹出的**同源**菜单副本（不用真的弹窗）。</summary>
        internal ContextMenuStrip BuildEmptyMenu()
        {
            ContextMenuStrip menu = NewMenu(false);
            menu.Items.Add("新建分组…", null, delegate { NewGroup(); });
            // ★2026-10-01：这里原来还有一条「刷新应用列表」，撤了（同 FillTileMenu 头部的理由）。
            //   想手动刷：设置齿轮里有一份，「添加应用」窗口里也有一份；多数情况下系统自己已经扫过了。
            return menu;
        }

        private void ShowEmptyMenu()
        {
            _suppressDeactivate = true;
            try
            {
                ContextMenuStrip menu = BuildEmptyMenu();

                menu.Closed += delegate { _suppressDeactivate = false; };
                KeepMenu(menu);
                menu.Show(this, PointToClient(Cursor.Position));
            }
            catch (Exception)
            {
                _suppressDeactivate = false;
            }
        }

        private void ShowSettingsMenu()
        {
            _suppressDeactivate = true;
            try
            {
                ContextMenuStrip menu = NewMenu(true);

                FillSettingsMenu(menu);

                menu.Closed += delegate { _suppressDeactivate = false; };
                KeepMenu(menu);
                menu.Show(_settingsBtn, new Point(0, 0), ToolStripDropDownDirection.AboveLeft);
            }
            catch (Exception)
            {
                _suppressDeactivate = false;
            }
        }

        /// <summary>
        /// 设置菜单里的开关项：**打勾 = 当前状态**，文字也把状态写清楚（「已开启 / 已关闭」）。
        /// ★以前写的是「亚克力半透明：关」这种「点一下会变成什么」，用户反馈分不清状态还是动作。
        /// </summary>
        private ToolStripMenuItem ToggleItem(string name, bool on, EventHandler onClick)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(name + "：" + (on ? "已开启" : "已关闭"));
            it.Checked = on;
            it.Click += onClick;
            return it;
        }

        /// <summary>设置菜单的内容（抽出来给自检断言；用户看到的就是这些）。</summary>
        private void FillSettingsMenu(ContextMenuStrip menu)
        {
            menu.Items.Add("刷新应用列表", null, delegate { RefreshAppList(); });
            // ★这三条是开关：**打勾 = 当前状态**，文字也写清「已开启 / 已关闭」。
            //   以前写的是「亚克力半透明：关」=「点一下会关掉」——用户反馈分不清是状态还是动作（容易弄混）。
            // ★亚克力那条的文字还要**如实**：系统不支持时面板其实是不透明的（`_acrylicActive=false`），
            //   那时候还写「空白处会穿透鼠标」就是假话 —— 换成「这台系统不支持」。
            //   这种情况以前是弹一个说明框，用户 2026-09-28 要求：**不弹窗**，写进菜单文字里就行。
            string acrylicLabel = (_settings.Acrylic && _acrylicActive == false)
                ? "亚克力半透明（这台系统不支持，面板保持不透明）"
                : "亚克力半透明（空白处会穿透鼠标）";
            menu.Items.Add(ToggleItem(acrylicLabel, _settings.Acrylic, delegate { ToggleAcrylic(); }));
            menu.Items.Add(ToggleItem("鼠标悬停显示名字", _settings.HoverNames, delegate { ToggleHoverNames(); }));
            menu.Items.Add(ToggleItem("启动应用后关闭面板", _settings.CloseAfterLaunch, delegate { ToggleCloseAfterLaunch(); }));


            ToolStripMenuItem sizeMenu = new ToolStripMenuItem("文件夹大小");
            int curScale = _settings.FolderScale <= 0 ? 100 : _settings.FolderScale;
            string[] scaleNames = new string[] { "小", "标准", "大" };
            int[] scaleVals = new int[] { 85, 100, 120 };
            for (int si = 0; si < scaleVals.Length; si++)
            {
                int sv = scaleVals[si];
                sizeMenu.DropDownItems.Add(scaleNames[si] + (sv == curScale ? "（当前）" : ""), null,
                    delegate { SetFolderScale(sv); });
            }
            menu.Items.Add(sizeMenu);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("清除图标缓存", null, delegate { ClearIcons(); });
            menu.Items.Add("打开软件所在文件夹", null, delegate { OpenAppFolder(); });
            menu.Items.Add("关于 BreadLauncher", null, delegate { ShowAbout(); });
        }

        /// <summary>自检用：和齿轮里弹出的**同源**设置菜单副本（不用真的弹窗）。</summary>
        internal ContextMenuStrip BuildSettingsMenu()
        {
            ContextMenuStrip menu = NewMenu(true);   // 留出打勾那一列：开关项的「当前状态」要能一眼看见
            FillSettingsMenu(menu);
            return menu;
        }

        private void OpenFileLocation(AppEntry en)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "/select,\"" + en.Path + "\"");
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception) { }
            Close();
        }

        private void CopyText(string text)
        {
            try { Clipboard.SetText(text); }
            catch (Exception) { }
        }

        private void RefreshAppList()
        {
            _requested.Clear();
            lock (this) { _iconGen++; _pendingIcons = 0; } // 换代：上一轮在途的回调作废，别把计数减成负数
            _scanGen++;
            LoadData(true);
        }



        /// <summary>鼠标悬停是否浮出应用名（设置菜单里切换，落盘后重画）。</summary>
        private void ToggleHoverNames()
        {
            if (_settings == null) return;
            _settings.HoverNames = !_settings.HoverNames;
            PersistSettings();
            Invalidate();
        }

        private void ToggleAcrylic()
        {
            _settings.Acrylic = !_settings.Acrylic;
            PersistSettings();
            _acrylicActive = Theme.ApplyBackdrop(this, _settings.Acrylic);
            Invalidate(true);
            // ★★用户 2026-09-28 明确要求：**亚克力相关的提示框全部删掉** —— 启动时不弹、手动开关时也不弹
            //   （「用户用的时候自行接纳就是了」）。代价写在设置菜单那一行字里：
            //   「亚克力半透明（空白处会穿透鼠标）：已开启」；
            //   「这台系统不支持」也不再弹窗，改成**菜单文字如实显示**（见 FillSettingsMenu 里那段）。
            //   别再加回任何 MessageBox。
        }

        private void ClearIcons()
        {
            lock (this) { _iconGen++; _pendingIcons = 0; }   // 换代：在途的取图回调作废（和 RefreshAppList 一样）
            _icons.ClearDiskCache();
            _requested.Clear();
            RefreshContent();
        }

        private void OpenAppFolder()
        {
            try { Process.Start("explorer.exe", "\"" + Program.AppDir + "\""); }
            catch (Exception) { }
            Close();
        }

        /// <summary>「关于」里那段「配置保存状态」的文案。**单独一个方法是为了能被探针直接断言**
        /// （用户在 2026-09-27 选的通知方式：不弹窗、不打扰，但出过事要能在「关于」里查到）。</summary>
        private string AboutSaveStatusText(string failText)
        {
            if (string.IsNullOrEmpty(failText)) return "配置保存：正常。";
            return "⚠ 上次配置没保存成功，改动可能没留下：\n" + failText + "\n"
                 + "（分组和设置都还在，重新改一次并正常关闭面板即可重试保存；\n"
                 + " 常见原因是那个文件夹被设成了只读、或者磁盘满了）";
        }

        /// <summary>「关于」。</summary>
        private void ShowAbout()
        {
            _suppressDeactivate = true;
            _modalOpen = true;
            try
            {
                MessageBox.Show(this,
                    "BreadLauncher 1.10\n\n" +
                    "仿 Windows 11 开始菜单的便携启动面板。\n" +
                    "分组就是「大文件夹」：不用点进去，点里面的小图标直接启动。\n" +
                    "应用列表来自系统 shell:AppsFolder（含商店应用）。\n" +
                    "关闭窗口即退出，没有后台进程、没有开机自启。\n\n" +
                    AboutSaveStatusText(_lastWriteFailText) + "\n\n" +
                    "数据目录：" + Program.AppDir,
                    "关于 BreadLauncher", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                _modalOpen = false;
                _suppressDeactivate = false;
            }
        }
    }
}
