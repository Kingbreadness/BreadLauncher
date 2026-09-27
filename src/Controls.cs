// Controls.cs —— 面板里所有自绘控件
//
// 为什么全部自绘：WinForms 自带控件是浅色系统风格，改颜色改不干净，
// 而「像开始菜单」这件事 90% 靠的是圆角、留白、悬停高亮和字体。
// 这里所有控件都只画，不用系统主题。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BreadLauncher
{
    // ============================================================
    // 扁平按钮 / 链接
    // ============================================================
    public class FlatButton : Control
    {
        public enum Look { Text, IconText, Link, RoundIcon }

        private bool _hover;
        private bool _down;

        public Look Style = Look.Text;
        public string Glyph = null;
        public bool Accent = false;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = Theme.Ui(9.75f);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
    
                Color fore = Accent ? Theme.Accent : Theme.TextPrimary;
                if (!Enabled) fore = Theme.TextDim;
    
                if (Style == Look.Text || Style == Look.IconText)
                {
                    if (_hover || _down)
                    {
                        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
                        Theme.FillRound(g, r, Theme.Px(this, 6), _down ? Theme.HoverStrong : Theme.Surface);
                    }
                }
                else if (Style == Look.RoundIcon)
                {
                    if (_hover) Theme.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Theme.Px(this, 6), Theme.Surface);
                }
                else if (Style == Look.Link)
                {
                    if (_hover) fore = Color.FromArgb(255, 255, 255);
                }
    
                string text = Text ?? string.Empty;
                int pad = Theme.Px(this, Style == Look.Link ? 2 : 8);
    
                if (!string.IsNullOrEmpty(Glyph))
                {
                    // 字形盒子要按字形实际占宽来给：固定用 Height 当宽度时，MDL2 字形的步进宽度
                    // 只要比它宽一点点，GDI 就会在图标后面再画一个「…」（用户看到的「设置/新建分组/关闭
                    // 图标旁边都有点」就是这么来的 —— Theme.DrawText 强制 EndEllipsis 是罪魁）。
                    Font glyphFont = Theme.Glyph(Height * 0.40f);
                    Size gsz = TextRenderer.MeasureText(Glyph, glyphFont, new Size(int.MaxValue, Height), TextFormatFlags.NoPrefix);
                    int minW = Theme.Px(this, 20);
                    int glyphW = gsz.Width + Theme.Px(this, 4);
                    if (glyphW < minW) glyphW = minW;
                    int cap = Math.Max(minW, Width / 2);
                    if (glyphW > cap) glyphW = cap;
    
                    Rectangle gr = new Rectangle(pad, 0, glyphW, Height);
                    // 字形不用 Theme.DrawText（它带 EndEllipsis，单字形用不上）；但要走 DrawTextRaw ——
                    // 它内部照样夹裁剪（GDI 不认 GDI+ 的 SetClip，别让字形画到裁剪区外面去）
                    Theme.DrawTextRaw(g, Glyph, glyphFont, gr, fore,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    if (Style == Look.IconText && !string.IsNullOrEmpty(text))
                    {
                        Rectangle tr = new Rectangle(pad + glyphW, 0, Math.Max(1, Width - pad * 2 - glyphW), Height);
                        Theme.DrawText(g, text, Font, tr, fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    }
                }
                else if (!string.IsNullOrEmpty(text))
                {
                    Rectangle tr = new Rectangle(pad, 0, Math.Max(1, Width - pad * 2), Height);
                    TextFormatFlags align = Style == Look.Link ? TextFormatFlags.Right : TextFormatFlags.Left;
                    Theme.DrawText(g, text, Font, tr, fore, align | TextFormatFlags.VerticalCenter);
                }
            }
            catch (Exception ex) { Theme.PaintCatch(this, e, "底栏按钮", ex); }
        }
    }

    // ============================================================
    // 应用列表（自绘 + 虚拟化，200 个条目也能秒开）
    // ============================================================
    public class AllAppsList : Control
    {
        private class Row
        {
            public bool IsHeader;
            public string HeaderText;
            public AppEntry Entry;
            public Rectangle Bounds;
        }

        private readonly List<Row> _rows = new List<Row>();
        private List<AppEntry> _lastEntries = new List<AppEntry>();
        private bool _lastShowGroups = true;
        private int _contentH;
        private int _offset;
        private int _hover = -1;
        private int _lastLayoutWidth = -1;
        private bool _dragging;
        private int _dragGrab;
        private bool _dirty;
        private readonly Timer _repaint = new Timer();
        // 框选时贴边自动滚动
        private readonly Timer _autoScroll = new Timer();
        private int _autoDir;

        public IconService Icons;
        public bool GridMode;
        /// <summary>列表为空时显示的一句话（「查看全部」窗口要用自己的说法）。</summary>
        public string EmptyText = "没有找到匹配的应用";

        /// <summary>勾选模式（「添加应用」选择器用）：左键点一行 = 打钩/取消钩，不再直接激活。</summary>
        public bool ShowChecks;
        /// <summary>已打钩的条目 Key（大小写不敏感）。</summary>
        public readonly HashSet<string> Checked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>打钩状态变了（调用方用来刷新「已选 N 个」）。</summary>
        public event Action CheckedChanged;

        public void UncheckAll()
        {
            CancelBand();
            Checked.Clear();
            if (CheckedChanged != null) CheckedChanged();
            Invalidate();
        }

        /// <summary>把给定的一批条目全部取消打钩（「全不选」按钮用：和「全选」口径对称，只清当前列出来的那些）。</summary>
        public void UncheckAll(List<AppEntry> items)
        {
            CancelBand();
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    AppEntry en = items[i];
                    if (en == null || string.IsNullOrEmpty(en.Key)) continue;
                    Checked.Remove(en.Key);
                }
            }
            if (CheckedChanged != null) CheckedChanged();
            Invalidate();
        }

        /// <summary>把给定的一批条目全部打钩（「全选」按钮用：选的是**当前筛选出来**的那些，不是全部候选）。</summary>
        public void CheckAll(List<AppEntry> items)
        {
            CancelBand();
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                AppEntry en = items[i];
                if (en == null || string.IsNullOrEmpty(en.Key)) continue;
                Checked.Add(en.Key);
            }
            if (CheckedChanged != null) CheckedChanged();
            Invalidate();
        }

        public event Action<AppEntry> Activated;
        public event Action<AppEntry, Point> ContextRequested;

        /// <summary>
        /// 允许「按住一行上下拖 = 换组内顺序」（只有「查看全部」窗口打开它）。
        /// ★选择器**必须保持 false** —— 那边的左键拖动是框选，两者互斥（也从不允许同时为 true）。
        /// </summary>
        public bool AllowReorder;
        /// <summary>一行被拖到了新位置：from/to 都是「当前可见顺序」里的下标（to 是落下后的目标下标）。
        /// 调用方负责改数据 + 落盘。只有真的换了位置才会触发。</summary>
        public event Action<int, int> Reordered;

        // 拖拽排序的进行时状态（和框选那套完全分开：AllowReorder 与 ShowChecks 不会同时为 true）
        private bool _roOn;        // 左键按在一行上，还没判断是「点一下」还是「拖」
        private bool _roActive;    // 已经拖起来了
        private int _roFrom = -1;
        private int _roTo = -1;    // 目标下标（0..行数，等于行数表示放到最后）
        private Point _roStart;
        private Point _roNow;

        /// <summary>取消进行中的拖排序（失去鼠标捕获 / 数据集变化都要调）。</summary>
        public void CancelReorder()
        {
            if (_roOn == false && _roActive == false) return;
            _roOn = false;
            _roActive = false;
            _roFrom = -1;
            _roTo = -1;
            StopAutoScroll();
            Invalidate();
        }

        public AllAppsList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            _repaint.Interval = 120;
            _repaint.Tick += delegate { _repaint.Stop(); if (_dirty) { _dirty = false; Invalidate(); } };
            // 框选贴边自动滚动：30ms 一拍、每拍滚 6 逻辑像素（约 200px/秒，够快又不飘）—— 数字在 Theme.Scroll
            _autoScroll.Interval = Theme.Scroll.AutoScrollInterval;
            _autoScroll.Tick += delegate
            {
                if (_autoDir == 0 || _bandActive == false) { _autoScroll.Stop(); return; }
                ScrollBy(_autoDir * Theme.Px(this, Theme.Scroll.AutoScrollStep));
                Invalidate();
            };
        }

        /// <summary>框选时贴边自动滚动：鼠标在上/下边缘（或拖出控件）就匀速滚。
        /// 方框自己不动、行从方框下面走过去 —— 松手时按当时的 `_offset` 结算，
        /// 所以「框到哪几行」始终等于「屏幕上看见被框住哪几行」。长列表才能一次框到看不见的行。</summary>
        private void UpdateAutoScroll()
        {
            int edge = Theme.Px(this, Theme.Scroll.AutoScrollEdge);
            int dir = 0;
            int y = -1;
            if (_bandActive) y = _bandNow.Y;            // 框选：方框跟着鼠标
            else if (_roActive) y = _roNow.Y;           // 拖排序：被拖的行跟着鼠标
            if (y >= 0)
            {
                if (y < edge) dir = -1;
                else if (y > Height - edge) dir = 1;
            }
            if (dir == _autoDir) return;
            _autoDir = dir;
            if (dir == 0) _autoScroll.Stop();
            else if (_autoScroll.Enabled == false) _autoScroll.Start();
        }

        private void StopAutoScroll()
        {
            _autoDir = 0;
            try { _autoScroll.Stop(); } catch (Exception) { }
        }

        /// <summary>换一批数据（应用选择器的筛选结果刷新走这里）。</summary>
        public void SetEntries(List<AppEntry> entries, bool gridMode, bool showGroups)
        {
            SetEntries(entries, gridMode, showGroups, false);
        }

        /// <summary>keepOffset = true 时保留当前滚动位置（「查看全部」里拖完顺序 / 移除一条之后，
        /// 列表不该跳回顶部）。</summary>
        public void SetEntries(List<AppEntry> entries, bool gridMode, bool showGroups, bool keepOffset)
        {
            CancelBand();          // ★数据集换了：进行中的框选必须取消，否则「新行 + 旧坐标」会勾错
            CancelReorder();       // 拖排序同理：行都换了，之前记的下标就没意义了
            int keep = _offset;
            _lastEntries = entries != null ? entries : new List<AppEntry>();
            _lastShowGroups = showGroups;
            GridMode = gridMode;
            _offset = 0;
            _hover = -1;
            BuildRows();
            if (keepOffset) _offset = Math.Min(keep, Math.Max(0, _contentH - Height));
            Invalidate();
        }

        /// <summary>后台取到图标了：不要每个图标都重画，攒 120ms 一起刷，避免闪烁。</summary>
        public void NotifyIconArrived()
        {
            try
            {
                _dirty = true;
                if (!_repaint.Enabled) _repaint.Start();
            }
            catch (Exception) { } // 控件已经 Dispose 掉了：回调是异步来的，忽略即可
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _repaint.Stop(); _repaint.Dispose(); }
                catch (Exception) { }
                // ★_autoScroll 以前**从来不 Dispose**（只 Stop）：Timer 是 Component，
                //   挂在控件的组件表上，谁都不会替它收 —— 每次建列表就多一个（Leak 一族）。
                try { _autoScroll.Stop(); _autoScroll.Dispose(); }
                catch (Exception) { }
            }
            base.Dispose(disposing);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (ClientSize.Width != _lastLayoutWidth && _lastEntries != null && _lastEntries.Count > 0)
            {
                int keep = _offset;
                BuildRows();
                _offset = Math.Min(keep, Math.Max(0, _contentH - Height));
            }
        }

        private void BuildRows()
        {
            _rows.Clear();
            _lastLayoutWidth = ClientSize.Width;
            // 右侧给滚动条留的位置（统一度量）。留出的这几像素里，行右缘到墨水左缘之间还有
            // 3px 的间隙 —— 点在那里既不打钩也不滚（和系统滚动条的「槽」一样），是有意留白。
            int barW = Theme.Scroll.ReservePx(this);
            int width = Math.Max(80, ClientSize.Width - barW - Theme.Px(this, 2));
            int headerH = Theme.Px(this, 36);
            int y = Theme.Px(this, 4);
            string lastLetter = null;

            if (GridMode)
            {
                int cols = 5;
                int cellW = Math.Max(40, width / cols);
                int cellH = Theme.Px(this, Theme.Scroll.GridCell);
                int i = 0;
                while (i < _lastEntries.Count)
                {
                    AppEntry first = _lastEntries[i];
                    string letter = first.Letter;
                    if (_lastShowGroups && letter != lastLetter)
                    {
                        Row h = new Row();
                        h.IsHeader = true;
                        h.HeaderText = letter;
                        h.Bounds = new Rectangle(Theme.Px(this, 4), y, width, headerH);
                        _rows.Add(h);
                        y += headerH;
                        lastLetter = letter;
                    }
                    int n = 0;
                    while (i < _lastEntries.Count && (!_lastShowGroups || _lastEntries[i].Letter == letter))
                    {
                        Row r = new Row();
                        r.Entry = _lastEntries[i];
                        int col = n % cols;
                        int row = n / cols;
                        r.Bounds = new Rectangle(col * cellW + Theme.Px(this, 2), y + row * cellH,
                                                 cellW - Theme.Px(this, 4), cellH - Theme.Px(this, 6));
                        _rows.Add(r);
                        n++;
                        i++;
                    }
                    y += ((n + cols - 1) / cols) * cellH;
                }
            }
            else
            {
                int rowH = Theme.Px(this, Theme.Scroll.ListRow);
                for (int i = 0; i < _lastEntries.Count; i++)
                {
                    AppEntry en = _lastEntries[i];
                    if (_lastShowGroups && en.Letter != lastLetter)
                    {
                        Row h = new Row();
                        h.IsHeader = true;
                        h.HeaderText = en.Letter;
                        h.Bounds = new Rectangle(Theme.Px(this, 4), y, width, headerH);
                        _rows.Add(h);
                        y += headerH;
                        lastLetter = en.Letter;
                    }
                    Row r = new Row();
                    r.Entry = en;
                    r.Bounds = new Rectangle(Theme.Px(this, 4), y, width - Theme.Px(this, 6), rowH - Theme.Px(this, 4));
                    _rows.Add(r);
                    y += rowH;
                }
            }

            _contentH = y + Theme.Px(this, 6);
        }

        /// <summary>列表内容总高（选择器面板照它决定列表区高度）。</summary>
        public int ContentHeight { get { return _contentH; } }

        /// <summary>一行的「行距」（滚轮按它整行整行地走；★只有**整格 120** 才保证停在整行上，
        /// 碎 Delta（触控板）按比例缩会停在半行 —— 这是为了让触控板跟手，和 Theme.Scroll 的口径一致）。</summary>
        private int RowPitch
        {
            get { return Theme.Px(this, GridMode ? Theme.Scroll.GridCell : Theme.Scroll.ListRow); }
        }

        private int BarTrackTop { get { return Theme.Scroll.TrackPadPx(this); } }
        private int BarTrackH { get { return Math.Max(1, Height - Theme.Scroll.TrackPadPx(this) * 2); } }

        /// <summary>点滚动条空白处翻多少：一屏减去一行（Windows 的老规矩），
        /// 留那一行是为了让用户还能看见「上一屏的最后一行」，不至于翻过界。以前是全屏整翻。</summary>
        private int PageStep
        {
            get
            {
                int s = Height - RowPitch;
                return Math.Max(RowPitch, s);
            }
        }

        /// <summary>切换一个条目的打钩状态（单击、右键菜单「打钩/取消打钩」都走这里，行为只有一份）。</summary>
        public void ToggleCheck(AppEntry en)
        {
            if (en == null || string.IsNullOrEmpty(en.Key)) return;
            if (Checked.Contains(en.Key)) Checked.Remove(en.Key);
            else Checked.Add(en.Key);
            if (CheckedChanged != null) CheckedChanged();
            Invalidate();
        }

        /// <summary>某个条目当前是否打钩（列表自己判断，调用方不用重复 Contains）。</summary>
        public bool IsChecked(AppEntry en)
        {
            return en != null && !string.IsNullOrEmpty(en.Key) && Checked.Contains(en.Key);
        }

        public void ScrollBy(int delta)
        {
            int max = Math.Max(0, _contentH - Height);
            int v = _offset + delta;
            if (v < 0) v = 0;
            if (v > max) v = max;
            if (v != _offset) { _offset = v; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // ★M3：框选期间不许滚 —— 方框存的是客户区坐标，滚动后 _offset 变了，
            //   松手时「勾中的行」和「屏幕上被方框盖住的行」就不是同一批了。
            if (_bandOn || _bandActive) return;
            // 一格滚轮 = ListRowsPerNotch 行（120 的整格），碎 Delta（高精度滚轮/触控板）按比例缩 ——
            // 以前直接 `ScrollBy(-e.Delta)`：整格时一行走多少全看系统给的像素数，碎 Delta 时几乎不动。
            ScrollBy(Theme.Scroll.WheelPixels(-e.Delta, RowPitch, Theme.Scroll.ListRowsPerNotch));
            base.OnMouseWheel(e);
        }

        private int IndexAt(Point p)
        {
            Point abs = new Point(p.X, p.Y + _offset);
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].IsHeader) continue;
                if (_rows[i].Bounds.Contains(abs)) return i;
            }
            return -1;
        }

        // 框选（「添加应用」选择器用）：按住左键拖出一个方框，框到的行全部打钩。
        // 【改这段代码前先读完这几条约定】
        // C1 一次左键手势只能有一个结论：要么「框选提交」、要么「单击切换」，绝不允许两者都发生。
        // C2 三个标记必须在**每一条**退出路径上复位：左键松开、非左键松开、失去鼠标捕获、
        //    数据集变化（SetEntries/UncheckAll/CheckAll）。漏一条 → 方框常驻 + 下次点击变成意外框选。
        // C3 行 Bounds 是**内容坐标**（未减 _offset），比较前一律 Offset(0, -_offset)。
        // C4 「相交即选中」（和资源管理器/桌面一致）；退化成 1px 的方框也算相交，这是有意行为。
        // C5 位移 ≤ Theme.Px(5) 不算框选（保持单击）；所有几何量一律走 Theme.Px（DPI 非 100% 时物理≠逻辑像素）。
        // C6 框选只做「加」，不做取消；框到的行本来就已勾选时不触发 CheckedChanged（n==0 不报变更）。
        // C7 预览高亮与提交必须用同一个矩形、同一套坐标口径。
        // C8 分组标题行永远不参与选中。
        // C9 只有 ShowChecks == true 才进框选（「查看全部」窗口的单击=启动不能被吃掉）。
        private bool _bandOn;       // 左键已按下，准备框选
        private bool _bandActive;   // 真的拖出了方框
        private Point _bandStart;
        private Point _bandNow;

        /// <summary>★丢失鼠标捕获 = 这次手势作废（Alt+Tab、UAC 安全桌面、截图工具抢焦点…都会导致收不到 MouseUp）。
        /// 注意判 `Capture == false`：**主动**设置捕获时也会触发本事件，那时不能取消。
        /// 另外刻意不用 `Control.MouseButtons` 来判断按键（那是全局物理鼠标状态，合成事件里永远是「没按键」，
        /// 会把自检打挂）。
        /// ★拖滑块也要一起复位：拖动分支不看按键，一旦丢了 MouseUp，滑块会「黏」在鼠标上（拖完还在拖）。</summary>
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture == false)
            {
                _dragging = false;
                CancelBand();
                CancelReorder();
            }
        }

        /// <summary>取消进行中的框选（复位标记）。数据集变化 / 失去捕获 / 非左键松手都要调。</summary>
        public void CancelBand()
        {
            StopAutoScroll();
            if (_bandOn == false && _bandActive == false) return;
            _bandOn = false;
            _bandActive = false;
            Invalidate();
        }

        /// <summary>方框矩形（客户区坐标，已规整成左上-右下）。</summary>
        private Rectangle BandRect()
        {
            int x1 = Math.Min(_bandStart.X, _bandNow.X);
            int y1 = Math.Min(_bandStart.Y, _bandNow.Y);
            int x2 = Math.Max(_bandStart.X, _bandNow.X);
            int y2 = Math.Max(_bandStart.Y, _bandNow.Y);
            return new Rectangle(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
        }

        /// <summary>把方框框到的行全部打钩（分组标题行跳过）。「相交即选中」，和资源管理器/桌面一致。</summary>
        private void ApplyBand()
        {
            if (ShowChecks == false) return;
            Rectangle band = BandRect();
            // ★H1：只认**看得见的那一片**。鼠标能拖到控件外（有捕获），方框可以拖到很远，
            //   不夹的话「视觉上盖住 9 行、实际整批 175 行全勾」——屏幕上被截掉的部分不该算数。
            band.Intersect(ClientRectangle);
            if (band.Width <= 0 || band.Height <= 0) return;
            int n = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                if (r.IsHeader || r.Entry == null || string.IsNullOrEmpty(r.Entry.Key)) continue;
                Rectangle b = r.Bounds;
                b.Offset(0, -_offset);          // 行 Bounds 是内容坐标，要减滚动偏移才是客户区坐标
                if (b.IntersectsWith(band) == false) continue;
                if (Checked.Add(r.Entry.Key)) n++;
            }
            if (n > 0 && CheckedChanged != null) CheckedChanged();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                int trackTop = BarTrackTop;
                int trackH = BarTrackH;
                int thumbH = Theme.Scroll.ThumbHeight(this, trackH, _contentH, Height);
                int max = Math.Max(1, _contentH - Height);
                int travel = Math.Max(1, trackH - thumbH);
                int y = Math.Max(trackTop, Math.Min(trackTop + trackH - thumbH, e.Y - _dragGrab));
                _offset = (int)((long)(y - trackTop) * max / travel);
                Invalidate();
                return;
            }
            if (_roOn)
            {
                // ★逐轴判定：只有**纵向**移动才算「拖排序」（横向抖一下不该把行搬走）
                if (_roActive == false && Math.Abs(e.Y - _roStart.Y) > Theme.Px(this, 6))
                    _roActive = true;
                if (_roActive)
                {
                    _roNow = e.Location;
                    _roTo = InsertIndexAt(e.Location.Y);
                    UpdateAutoScroll();          // 拖到上下边缘自动滚（长列表才能一次拖到底）
                    Invalidate();
                    return;                      // 拖排序期间不更新 hover
                }
            }
            if (_bandOn)
            {
                if (_bandActive == false &&
                    (Math.Abs(e.X - _bandStart.X) > Theme.Px(this, 5) || Math.Abs(e.Y - _bandStart.Y) > Theme.Px(this, 5)))
                    _bandActive = true;   // ★逐轴判定（以前用 |dx|+|dy|，3+3 的抖动就升级成框选，会勾错相邻行）
                if (_bandActive)
                {
                    _bandNow = e.Location;
                    UpdateAutoScroll();          // 贴边就自动滚（长列表要能一次框到底）
                    Invalidate();
                    return;                      // 框选期间不更新 hover
                }
            }
            int idx = IndexAt(e.Location);
            if (idx != _hover) { _hover = idx; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1; Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.X >= Theme.Scroll.HitLeft(this, ClientSize.Width) && _contentH > Height)
            {
                // ★H2：命中区只到「墨水 + 1px 容错」（Theme.Scroll.HitLeft）。以前写死 Px(12)，
                //   比墨水宽一大截，会抢走行右端勾选框的地盘：点勾选变成翻页。
                int trackTop = BarTrackTop;
                int trackH = BarTrackH;
                int thumbH = Theme.Scroll.ThumbHeight(this, trackH, _contentH, Height);
                int thumbY = ThumbTop(trackTop, trackH, thumbH);
                if (e.Y >= thumbY && e.Y <= thumbY + thumbH)
                {
                    _dragging = true;
                    _dragGrab = e.Y - thumbY;
                }
                else if (e.Y < thumbY)
                {
                    ScrollBy(-PageStep);
                }
                else
                {
                    ScrollBy(PageStep);
                }
                return;
            }
            if (e.Button == MouseButtons.Left && AllowReorder)
            {
                // 「查看全部」窗口：按住一行上下拖 = 换组内顺序；不移动就还是原来的「点一下启动」
                int ri = IndexAt(e.Location);
                if (ri >= 0)
                {
                    _roOn = true;
                    _roActive = false;
                    _roFrom = ri;
                    _roTo = ri;
                    _roStart = e.Location;
                    _roNow = e.Location;
                    try { Capture = true; } catch (Exception) { }
                }
            }
            else if (e.Button == MouseButtons.Left && ShowChecks)
            {
                _bandOn = true;
                _bandActive = false;
                _bandStart = e.Location;
                _bandNow = e.Location;
                try { Capture = true; } catch (Exception) { }   // 抓住鼠标：拖到控件外也收得到 MouseUp
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            // 框选判断放在 _dragging 之前：万一 _dragging 卡住（丢了 MouseUp），也不能让框选分支进不来
            if (_bandActive)
            {
                bool commit = (e.Button == MouseButtons.Left);
                StopAutoScroll();
                _bandActive = false;
                _bandOn = false;
                try { Capture = false; } catch (Exception) { }
                if (commit) ApplyBand();
                else Invalidate();
                return;
            }
            if (_roOn)
            {
                bool wasActive = _roActive;
                int from = _roFrom, to = _roTo;
                _roOn = false;
                _roActive = false;
                _roFrom = -1;
                _roTo = -1;
                StopAutoScroll();
                try { Capture = false; } catch (Exception) { }
                Invalidate();
                if (wasActive && e.Button == MouseButtons.Left)
                {
                    // ★拖过就**绝不**当作「点一下启动」（和框选那条 C1 规矩一致）
                    if (from >= 0 && to >= 0 && Reordered != null)
                    {
                        int target = to > from ? to - 1 : to;   // to 是「插到第几行之前」，换算成最终下标
                        if (target != from) Reordered(from, target);
                    }
                    return;
                }
                // 没真的拖动 → 继续往下走「点一下启动」
            }
            if (_dragging) { _dragging = false; return; }

            int idx = IndexAt(e.Location);
            if (idx >= 0)
            {
                AppEntry en = _rows[idx].Entry;
                if (e.Button == MouseButtons.Left)
                {
                    if (ShowChecks)
                    {
                        // 勾选模式：点一下打钩 / 再点一下取消，不动「激活」那条路
                        // （右键菜单的「打钩 / 取消打钩」走的是同一个 ToggleCheck，行为只有一份）
                        ToggleCheck(en);
                    }
                    else if (Activated != null) Activated(en);
                }
                else if (e.Button == MouseButtons.Right && ContextRequested != null) ContextRequested(en, e.Location);
            }
            base.OnMouseUp(e);
        }

        /// <summary>拖排序用：鼠标在 y 处应该插到第几行之前（0..可见行数）。只看可见行（跳过分组标题）。</summary>
        private int InsertIndexAt(int y)
        {
            int abs = y + _offset;                       // 行 Bounds 是内容坐标
            int n = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                if (r.IsHeader) continue;
                if (abs < r.Bounds.Top + r.Bounds.Height / 2) return n;
                n++;
            }
            return n;
        }

        /// <summary>可见行数（不含分组标题）—— 调用方拿它夹 Reordered 的下标。</summary>
        public int VisibleRowCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _rows.Count; i++) if (_rows[i].IsHeader == false) n++;
                return n;
            }
        }

        /// <summary>插入位置对应的横线 y（客户区坐标）：插在那一行的上边缘；插到最后就用最后一行的下边缘。</summary>
        private int DropLineY(int insertAt)
        {
            int n = 0, lastBottom = -1;
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                if (r.IsHeader) continue;
                if (n == insertAt) return r.Bounds.Top - _offset;
                lastBottom = r.Bounds.Bottom - _offset;
                n++;
            }
            return lastBottom < 0 ? Theme.Px(this, 4) : lastBottom;
        }
        /// <summary>滑块顶部位置：几何只有一份（Theme.Scroll），两处滚动共用。
        /// （原来还有个 ThumbHeight 包装，重构后没人调了，已删 —— 留着只会让人改错那一个。）</summary>
        private int ThumbTop(int trackTop, int trackH, int thumbH)
        {
            int max = Math.Max(1, _contentH - Height);
            int travel = Math.Max(0, trackH - thumbH);
            int v = _offset;
            if (v < 0) v = 0;
            if (v > max) v = max;
            return trackTop + (int)((long)travel * v / max);
        }

        /// <summary>同 MainForm：绘制异常会被 WinForms 画成「白底红叉」，这里兜住并记日志。</summary>
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
                    if (Program.AppDir != null)
                        ConfigStore.Log(Program.AppDir, "★列表绘制异常：" + ex.GetType().Name + "："
                            + ex.Message + "  @  " + ex.StackTrace);
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
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            if (_lastEntries.Count == 0)
            {
                Theme.DrawText(g, EmptyText, Theme.Ui(10f),
                    new Rectangle(0, Theme.Px(this, 24), ClientSize.Width, Theme.Px(this, 40)),
                    Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            int top = _offset - Theme.Px(this, 40);
            int bottom = _offset + Height + Theme.Px(this, 40);
            Rectangle bandRect = _bandActive ? BandRect() : Rectangle.Empty;

            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                if (r.Bounds.Bottom < top || r.Bounds.Top > bottom) continue;
                Rectangle b = r.Bounds;
                b.Offset(0, -_offset);
                // 框选预览：★不要复用 hover 底色 —— 用户分不清「只是被框到」和「已经打勾」。
                // 框到的行改成画一圈强调色描边（勾选框是另一种视觉），一眼能区分。
                bool inBand = _bandActive && r.IsHeader == false && b.IntersectsWith(bandRect);
                bool roDrag = _roActive && r.IsHeader == false && i == _roFrom;   // 正在被拖的那一行
                bool hot = (i == _hover) || inBand;

                if (r.IsHeader)
                {
                    Rectangle hr = new Rectangle(b.X + Theme.Px(this, 8), b.Y, b.Width, b.Height);
                    Theme.DrawText(g, r.HeaderText, Theme.Ui(10.5f, FontStyle.Bold), hr, Theme.TextSecondary,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
                else if (GridMode)
                {
                    DrawTile(g, r.Entry, b, hot);
                }
                else
                {
                    DrawRow(g, r.Entry, b, hot);
                }

                if (inBand)
                {
                    Rectangle ob = new Rectangle(b.X + 1, b.Y + 1, Math.Max(1, b.Width - 2), Math.Max(1, b.Height - 2));
                    Theme.DrawRound(g, ob, Theme.Px(this, 6), Theme.Accent, 1f);
                }
                if (roDrag)
                {
                    // 被拖的行：底色加深 + 强调色描边（和「被框到」区分开：那个只有描边）
                    Theme.FillRound(g, b, Theme.Px(this, 6), Theme.HoverStrong);
                    Rectangle ob2 = new Rectangle(b.X + 1, b.Y + 1, Math.Max(1, b.Width - 2), Math.Max(1, b.Height - 2));
                    Theme.DrawRound(g, ob2, Theme.Px(this, 6), Theme.Accent, 1f);
                }
            }

            // 拖排序：在插入位置画一条强调色横线（「会插到这儿」）
            if (_roActive)
            {
                int dy = DropLineY(_roTo);
                using (Pen dp = new Pen(Theme.Accent, 2f))
                    g.DrawLine(dp, Theme.Px(this, 8), dy, ClientSize.Width - Theme.Px(this, 16), dy);
            }

            // 框选方框（画的时候和客户区求交：拖到控件外时方框不会画到别人身上，
            // 但**提交仍用未裁剪的矩形** —— 拖出再拖回来照样选中沿途的行）
            if (_bandActive)
            {
                Rectangle vis = Rectangle.Intersect(BandRect(), ClientRectangle);
                if (vis.Width > 0 && vis.Height > 0)
                {
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(38, Theme.Accent)))
                        g.FillRectangle(sb, vis);
                    using (Pen p = new Pen(Theme.Accent, 1f))
                        g.DrawRectangle(p, vis.X, vis.Y, vis.Width - 1, vis.Height - 1);
                }
            }

            // 细滚动条：几何与颜色统一走 Theme.Scroll（主面板分组区那根也是同一份代码画的）
            if (_contentH > Height)
                Theme.Scroll.PaintBar(g,
                    Theme.Scroll.BarRect(this, BarTrackTop, BarTrackH, _contentH, Height, _offset));
        }

        private void DrawRow(Graphics g, AppEntry en, Rectangle b, bool hover)
        {
            if (hover) Theme.FillRound(g, b, Theme.Px(this, 6), Theme.Hover);

            int iconSize = Theme.Px(this, 24);
            Bitmap icon = Icons != null ? Icons.Get(en, iconSize) : null;
            int iconX = b.X + Theme.Px(this, 12);
            int iconY = b.Y + (b.Height - iconSize) / 2;
            if (icon != null) g.DrawImage(icon, new Rectangle(iconX, iconY, iconSize, iconSize));
            else Theme.DrawAppTile(g, new Rectangle(iconX, iconY, iconSize, iconSize), en.DisplayName, Theme.Px(this, 5));

            int textX = iconX + iconSize + Theme.Px(this, 16);
            int reserve = ShowChecks ? Theme.Px(this, 30) : Theme.Px(this, 8);
            Rectangle tr = new Rectangle(textX, b.Y, Math.Max(10, b.Right - textX - reserve), b.Height);
            Font f = Theme.Ui(10f);
            Theme.DrawText(g, en.DisplayName, f, tr, Theme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            if (ShowChecks)
            {
                bool on = en.Key != null && Checked.Contains(en.Key);
                int gs = Theme.Px(this, 15);
                Rectangle cr = new Rectangle(b.Right - Theme.Px(this, 14) - gs, b.Y + (b.Height - gs) / 2, gs, gs);
                if (on)
                {
                    Theme.FillRound(g, cr, Theme.Px(this, 4), Theme.Accent);
                    Theme.DrawTextRaw(g, "\uE73E", Theme.Glyph(Theme.Px(this, 9)), cr, Theme.BgBottom,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else
                {
                    Theme.DrawRound(g, cr, Theme.Px(this, 4), Theme.Border, 1f);
                }
            }

            if (en.IsNew || en.OnDesktop)
            {
                int nameW = Math.Min(TextRenderer.MeasureText(g, en.DisplayName, f, new Size(int.MaxValue, b.Height), TextFormatFlags.NoPrefix).Width, tr.Width);
                // ★把标签位置夹住：名字很长时别让「桌面 / 新增」顶到右边的勾选框上
                int tagX = Math.Min(textX + nameW + Theme.Px(this, 10), b.Right - reserve - Theme.Px(this, 46));
                if (en.OnDesktop)
                {
                    Rectangle dr = new Rectangle(tagX, b.Y, Theme.Px(this, 40), b.Height);
                    Theme.DrawText(g, "桌面", Theme.Ui(8.5f), dr, Theme.TextDim,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    tagX += Theme.Px(this, 34);
                }
                if (en.IsNew)
                {
                    Rectangle nr = new Rectangle(tagX, b.Y, Theme.Px(this, 40), b.Height);
                    Theme.DrawText(g, "新增", Theme.Ui(8.5f), nr, Theme.Accent,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
            }
        }

        private void DrawTile(Graphics g, AppEntry en, Rectangle b, bool hover)
        {
            if (hover) Theme.FillRound(g, b, Theme.Px(this, 6), Theme.Hover);

            int iconSize = Theme.Px(this, 32);
            Bitmap icon = Icons != null ? Icons.Get(en, iconSize) : null;
            if (icon != null)
                g.DrawImage(icon, new Rectangle(b.X + (b.Width - iconSize) / 2, b.Y + Theme.Px(this, 12), iconSize, iconSize));

            Rectangle tr = new Rectangle(b.X + Theme.Px(this, 4), b.Y + Theme.Px(this, 12) + iconSize + Theme.Px(this, 8),
                                         Math.Max(10, b.Width - Theme.Px(this, 8)), Theme.Px(this, 30));
            Theme.DrawText(g, en.DisplayName, Theme.Ui(9.5f), tr, Theme.TextPrimary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);
        }
    }

    // ============================================================
    // 右键菜单的深色皮肤
    // ============================================================
    public class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.HoverStrong; } }
        public override Color MenuItemBorder { get { return Theme.HoverStrong; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(44, 44, 44); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(44, 44, 44); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(44, 44, 44); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(44, 44, 44); } }
        public override Color SeparatorDark { get { return Theme.Separator; } }
        public override Color SeparatorLight { get { return Theme.Separator; } }
    }

    public class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.TextPrimary : Theme.TextDim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(44, 44, 44)))
                e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Theme.Border))
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (Pen p = new Pen(Theme.Separator))
                e.Graphics.DrawLine(p, e.Item.Width / 6, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2);
        }
    }
}
