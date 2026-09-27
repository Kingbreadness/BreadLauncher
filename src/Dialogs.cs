// Dialogs.cs —— 编辑分组时用的两个深色小对话框
//
//   TextPromptForm：新建 / 重命名分组时输入名字
//   AppPickerForm ：往分组里挑应用（自带筛选框 + 复用 AllAppsList）
//
// 都是模态窗口，关掉即销毁，不留任何后台状态。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace BreadLauncher
{
    // ============================================================
    // 输入一行文字：新建分组 / 重命名分组
    // ============================================================
    public class TextPromptForm : Form
    {
        private TextBox _input;
        private FlatButton _ok;
        private FlatButton _cancel;
        private string _title = string.Empty;
        private Rectangle _boxRect;

        private TextPromptForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BgBottom;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Theme.ApplyRoundedCorners(this);
        }

        /// <summary>返回去空白后的文本；取消 / 关闭返回 null。</summary>
        public static string Ask(IWin32Window owner, string title, string initial, string okText)
        {
            TextPromptForm f = new TextPromptForm();
            try
            {
                f.Init(title, initial, okText);
                DialogResult r = owner == null ? f.ShowDialog() : f.ShowDialog(owner);
                if (r != DialogResult.OK) return null;
                return (f._input.Text ?? string.Empty).Trim();
            }
            finally
            {
                f.Dispose();
            }
        }

        private void Init(string title, string initial, string okText)
        {
            _title = title ?? string.Empty;
            // ClientSize 交给 ApplyLayout() 统一算（这里再写一遍只是死数字，会被它覆盖）

            _input = new TextBox();
            _input.BorderStyle = BorderStyle.None;
            _input.BackColor = Theme.Surface;
            _input.ForeColor = Theme.TextPrimary;
            _input.Font = Theme.Ui(10.5f);
            _input.Text = initial ?? string.Empty;
            Controls.Add(_input);

            _cancel = new FlatButton();
            _cancel.Style = FlatButton.Look.IconText;
            _cancel.Glyph = "\uE8BB";
            _cancel.Text = "取消";
            _cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(_cancel);

            _ok = new FlatButton();
            _ok.Style = FlatButton.Look.IconText;
            _ok.Glyph = "\uE8FB";
            _ok.Text = string.IsNullOrEmpty(okText) ? "确定" : okText;
            _ok.Accent = true;
            _ok.Click += delegate { DialogResult = DialogResult.OK; };
            Controls.Add(_ok);

            ApplyLayout();
        }

        /// <summary>
        /// 按**当前** DPI 重算窗口大小与所有子控件位置。
        /// ★为什么要单独一个方法、并在 OnLoad 里再调一次：
        ///   构造期窗体**还没有窗口句柄**，`Theme.Px` 里读的 `Control.DeviceDpi` 这时还是缺省 96；
        ///   等高 DPI 显示器上句柄建好，真实 DPI 才生效 —— 于是「构造期按 96 算的 ClientSize」
        ///   在 125% 下会整体偏小约 20%。OnLoad 时句柄已存在，这时算的才是对的。
        ///   （本机是 100%，两种算法结果相同，所以看不出差别；这条是按 .NET 的 DPI 时序推的。）
        /// </summary>
        private void ApplyLayout()
        {
            ClientSize = new Size(Theme.Px(this, 380), Theme.Px(this, 168));

            int pad = Theme.Px(this, 24);
            int w = ClientSize.Width;
            int boxH = Theme.Px(this, 40);
            _boxRect = new Rectangle(pad, Theme.Px(this, 54), w - pad * 2, boxH);
            _input.Bounds = new Rectangle(_boxRect.X + Theme.Px(this, 12), _boxRect.Y + Theme.Px(this, 9),
                Math.Max(Theme.Px(this, 40), _boxRect.Width - Theme.Px(this, 24)), Theme.Px(this, 22));

            int bh = Theme.Px(this, 36);
            int by = ClientSize.Height - Theme.Px(this, 18) - bh;
            int bw = Theme.Px(this, 104);
            _ok.Bounds = new Rectangle(w - pad - bw, by, bw, bh);
            _cancel.Bounds = new Rectangle(w - pad - bw * 2 - Theme.Px(this, 6), by, bw, bh);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try { ApplyLayout(); }   // 句柄已建：这时 DeviceDpi 才是真实值（高 DPI 下才看得出差别）
            catch (Exception) { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _input.Focus();
            _input.SelectAll();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            if (keyData == Keys.Enter) { DialogResult = DialogResult.OK; return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Theme.BgBottom))
                    g.FillRectangle(b, ClientRectangle);
                Theme.DrawText(g, _title, Theme.Ui(11f, FontStyle.Bold),
                    new Rectangle(Theme.Px(this, 24), Theme.Px(this, 16), ClientSize.Width - Theme.Px(this, 48), Theme.Px(this, 26)),
                    Theme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                Theme.FillRound(g, _boxRect, Theme.Px(this, 8), Theme.Surface);
                Theme.DrawRound(g, _boxRect, Theme.Px(this, 8), _input.Focused ? Theme.Accent : Theme.Border, 1f);
                using (Pen p = new Pen(Theme.Border))
                    g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
            catch (Exception ex) { Theme.PaintCatch(this, e, "重命名对话框", ex); }
        }
    }

    // ============================================================
    // 添加应用：筛选 + 列表 + 单击选中 / 再点一次直接添加
    // ============================================================
    public class AppPickerForm : Form
    {
        private const int IconSize = 24;

        private IconService _icons;
        private List<AppEntry> _all = new List<AppEntry>();
        private List<AppEntry> _filtered = new List<AppEntry>();
        private AllAppsList _list;
        private TextBox _filter;
        private FlatButton _ok;
        private FlatButton _cancel;
        private System.Windows.Forms.Timer _timer;
        private DateTime _shownAt = DateTime.Now;
        private readonly HashSet<string> _requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Rectangle _filterRect;
        private string _title = string.Empty;
        private string _groupName = string.Empty;
        private string _hint = "点一下要加的应用（会打钩）";
        private int _already;          // 这个组现在有几个
        private int _added;            // 本次一共加了几个

        // 候选项的「来源」筛选：全部（含开始菜单）/ 桌面 / 自定义文件夹。
        // ★以前这里是个「只看桌面」的开关；用户要的是「一个按钮点开能选来源，自定义还能自己加目录」。
        private const int SrcAll = 0;
        private const int SrcDesktop = 1;
        private const int SrcCustom = 2;
        private int _source = SrcDesktop;      // 默认「桌面」（和以前的行为一致）
        private FlatButton _sourceBtn;
        private ContextMenuStrip _srcMenu;     // 来源菜单（复用同一个实例，OnFormClosed 里 Dispose）
        private Settings _settings;            // 自定义目录存在这里（改完由调用方落盘 + 重扫）
        private Func<List<AppEntry>> _onSourcesChanged;
        private Action _onPersist;          // 改名之后要落盘（选择器自己不碰配置文件）
        private string _titleInfo = string.Empty;   // 标题右侧那半句「已有 N 个 · 已选 M 个」
        private FlatButton _allBtn;     // 全选（当前列出来的那些）
        private FlatButton _noneBtn;    // 全不选
        private Action<AppEntry> _onAdd;
        private bool _suppressDeactivate;   // 右键菜单 / 子对话框弹出来时压住「失去焦点自动关窗」
        private ContextMenuStrip _ctx;      // 复用的右键菜单（★在 OnFormClosed 里 Dispose，不在自己的 Closed 里）

        private AppPickerForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BgBottom;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Theme.ApplyRoundedCorners(this);
        }

        /// <summary>
        /// 往一个分组里加应用：**一次打开可以连着加多个**（原来是加一个就关窗，用户抱怨
        /// 「每次只显示一个应用，也不知道加了几个」）。每加一个回调一次 onAdd（调用方负责落盘 + 刷新面板），
        /// 加过的条目立刻从列表里消失，标题上的「已有 N 个」实时更新。关窗 / Esc = 结束。
        /// 返回本次一共加了几个。
        /// </summary>
        public static int PickMany(IWin32Window owner, IconService icons, string groupName, int alreadyCount,
                                   List<AppEntry> candidates, Action<AppEntry> onAdd,
                                   Settings settings, Func<List<AppEntry>> onSourcesChanged, Action onPersist)
        {
            AppPickerForm f = new AppPickerForm();
            try
            {
                f.Init(icons, groupName, alreadyCount, candidates, onAdd, settings, onSourcesChanged, onPersist);
                if (owner == null) f.ShowDialog();
                else f.ShowDialog(owner);
                return f._added;
            }
            finally
            {
                f.Dispose();
            }
        }

        private void Init(IconService icons, string groupName, int alreadyCount, List<AppEntry> candidates,
                          Action<AppEntry> onAdd, Settings settings, Func<List<AppEntry>> onSourcesChanged, Action onPersist)
        {
            _icons = icons;
            _groupName = groupName == null ? string.Empty : groupName;
            _already = alreadyCount;
            _onAdd = onAdd;
            _settings = settings;
            _onSourcesChanged = onSourcesChanged;
            _onPersist = onPersist;
            _all = candidates == null ? new List<AppEntry>() : candidates;
            UpdateTitle();
            // 540 宽：多放「来源 ▾ / 全选 / 全不选」三个按钮，筛选框还留得下
            // ClientSize 交给 ApplyLayout() 统一算（这里再写一遍只是死数字，会被它覆盖）
            _shownAt = DateTime.Now;

            _filter = new TextBox();
            _filter.BorderStyle = BorderStyle.None;
            _filter.BackColor = Theme.Surface;
            _filter.ForeColor = Theme.TextPrimary;
            _filter.Font = Theme.Ui(10.5f);
            _filter.TextChanged += delegate { RestartTimer(); };
            Controls.Add(_filter);

            _list = new AllAppsList();
            _list.Icons = icons;
            _list.ShowChecks = true;                                  // 勾选式：点一下打钩，选好一起加
            _list.CheckedChanged += delegate { UpdateChecks(); };
            _list.ContextRequested += OnEntryContext;                 // 右键一个候选：打钩 / 找文件 / 复制名字
            Controls.Add(_list);

            // 「来源 ▾」：点开选 全部 / 桌面 / 自定义文件夹，最后一项能管理自定义目录
            _sourceBtn = new FlatButton();
            _sourceBtn.Style = FlatButton.Look.IconText;
            _sourceBtn.Glyph = "\uE70D";     // MDL2 ChevronDown：一眼看出「点开有菜单」
            _sourceBtn.Click += delegate { ShowSourceMenu(); };
            Controls.Add(_sourceBtn);
            UpdateSourceButton();

            // 全选 / 全不选：选的是**当前列出来的**那些（配合筛选框用：先筛「chrome」→ 全选 → 一次加完）
            _allBtn = new FlatButton();
            _allBtn.Style = FlatButton.Look.Text;
            _allBtn.Text = "全选";
            _allBtn.Click += delegate { _list.CheckAll(_filtered); };
            Controls.Add(_allBtn);

            _noneBtn = new FlatButton();
            _noneBtn.Style = FlatButton.Look.Text;
            _noneBtn.Text = "全不选";
            _noneBtn.Click += delegate { _list.UncheckAll(_filtered); };   // 口径与「全选」对称：只清当前列出来的
            Controls.Add(_noneBtn);

            _cancel = new FlatButton();
            _cancel.Style = FlatButton.Look.IconText;
            _cancel.Glyph = "\uE8BB";
            _cancel.Text = "完成";
            _cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(_cancel);

            _ok = new FlatButton();
            _ok.Style = FlatButton.Look.IconText;
            _ok.Glyph = "\uE8FB";
            _ok.Text = "添加";
            _ok.Accent = true;
            _ok.Enabled = false;
            _ok.Click += delegate { Accept(); };
            Controls.Add(_ok);

            ApplyLayout();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 160;
            _timer.Tick += delegate { _timer.Stop(); ApplyFilter(); };

            ApplyFilter();
        }

        /// <summary>
        /// 按**当前** DPI 重算窗口大小与所有子控件位置（和 TextPromptForm 同一个理由：
        /// 构造期还没有窗口句柄，`Theme.Px` 读到的 `DeviceDpi` 还是缺省 96，高 DPI 下会整体偏小）。
        /// </summary>
        private void ApplyLayout()
        {
            ClientSize = new Size(Theme.Px(this, 540), Theme.Px(this, 520));

            int pad = Theme.Px(this, 24);
            int w = ClientSize.Width;
            int onlyW = Theme.Px(this, 140);     // 「来源：桌面」+ 箭头（118 时文字会被省略号截成「来源：…」）
            int allW = Theme.Px(this, 62);
            int noneW = Theme.Px(this, 78);
            int gap = Theme.Px(this, 8);
            int rightW = onlyW + allW + noneW + gap * 2;
            _filterRect = new Rectangle(pad, Theme.Px(this, 58), w - pad * 2 - rightW - gap, Theme.Px(this, 40));
            _filter.Bounds = new Rectangle(_filterRect.X + Theme.Px(this, 12), _filterRect.Y + Theme.Px(this, 9),
                Math.Max(Theme.Px(this, 40), _filterRect.Width - Theme.Px(this, 24)), Theme.Px(this, 22));
            int bx = _filterRect.Right + gap;
            _sourceBtn.Bounds = new Rectangle(bx, _filterRect.Y, onlyW, Theme.Px(this, 40));
            _allBtn.Bounds = new Rectangle(bx + onlyW + gap, _filterRect.Y, allW, Theme.Px(this, 40));
            _noneBtn.Bounds = new Rectangle(bx + onlyW + allW + gap * 2, _filterRect.Y, noneW, Theme.Px(this, 40));

            int bh = Theme.Px(this, 36);
            int by = ClientSize.Height - Theme.Px(this, 18) - bh;
            int bw = Theme.Px(this, 104);
            _ok.Bounds = new Rectangle(w - pad - bw, by, bw, bh);
            _cancel.Bounds = new Rectangle(w - pad - bw * 2 - Theme.Px(this, 6), by, bw, bh);

            int listY = _filterRect.Bottom + Theme.Px(this, 10);
            _list.Bounds = new Rectangle(pad, listY, w - pad * 2, Math.Max(Theme.Px(this, 80), by - Theme.Px(this, 40) - listY));
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try { ApplyLayout(); }   // 句柄已建：这时 DeviceDpi 才是真实值（高 DPI 下才看得出差别）
            catch (Exception) { }
        }

        private void RestartTimer()
        {
            _timer.Stop();
            _timer.Start();
        }

        // ============================================================
        // 「来源」菜单：全部 / 桌面 / 自定义文件夹（+ 管理自定义文件夹…）
        // ============================================================

        private static string SourceName(int mode)
        {
            if (mode == SrcDesktop) return "桌面";
            if (mode == SrcCustom) return "自定义";
            return "全部";
        }

        /// <summary>按钮文字永远显示「当前是哪个来源」，一眼看得出状态。</summary>
        private void UpdateSourceButton()
        {
            if (_sourceBtn == null) return;
            _sourceBtn.Text = "来源：" + SourceName(_source);
            _sourceBtn.Accent = _source != SrcAll;
        }

        /// <summary>自定义来源的补充说明：目录不存在 / 是网络位置 / 被上限截断，都在这儿说清楚 ——
        /// 否则用户只会看到「我加了目录怎么什么都没有」，而这三种情况以前一个字都不显示（只在 log.txt 里）。</summary>
        private string CustomNote()
        {
            string s = string.Empty;
            if (AppsFolderScanner.LastCustomMissing > 0)
                s += "；" + AppsFolderScanner.LastCustomMissing + " 个目录当前不存在（拔掉的盘？）已跳过";
            if (AppsFolderScanner.LastCustomSkippedNet > 0)
                s += "；" + AppsFolderScanner.LastCustomSkippedNet + " 个是网络位置，不扫（会卡界面）";
            if (AppsFolderScanner.LastCustomTruncated > 0)
                s += "；有目录太大，每个最多取前 " + 1000 + " 条";
            return s;
        }

        private int CustomDirCount()
        {
            if (_settings == null || _settings.ScanDirs == null) return 0;
            return _settings.ScanDirs.Count;
        }

        /// <summary>菜单内容抽一层：自检可以直接断言有哪些项、当前选中哪个（不用真的弹窗）。</summary>
        internal ContextMenuStrip BuildSourceMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.ShowImageMargin = true;                 // 左边留出打勾那一列（当前选中的打勾）
            menu.Font = Theme.Ui(9.75f);
            AddSourceItem(menu, "全部（含开始菜单）", SrcAll);
            AddSourceItem(menu, "桌面", SrcDesktop);
            AddSourceItem(menu, "自定义文件夹", SrcCustom);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("管理自定义文件夹…", null, delegate { ManageFolders(); });
            return menu;
        }

        private void AddSourceItem(ContextMenuStrip menu, string text, int mode)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Checked = (_source == mode);
            it.Click += delegate { SetSource(mode); };
            menu.Items.Add(it);
        }

        /// <summary>
        /// 弹出「来源」菜单。★和右键菜单同一套规矩：复用实例（在 OnFormClosed 里 Dispose）、
        /// 弹出前压住「失去焦点自动关窗」（菜单是另一个顶层窗口）。
        /// </summary>
        private void ShowSourceMenu()
        {
            _suppressDeactivate = true;
            try
            {
                if (_srcMenu == null)
                {
                    _srcMenu = new ContextMenuStrip();
                    _srcMenu.Renderer = new DarkMenuRenderer();
                    _srcMenu.ShowImageMargin = true;
                    _srcMenu.Font = Theme.Ui(9.75f);
                    _srcMenu.Closed += delegate { _suppressDeactivate = false; };
                }
                _srcMenu.Items.Clear();
                AddSourceItem(_srcMenu, "全部（含开始菜单）", SrcAll);
                AddSourceItem(_srcMenu, "桌面", SrcDesktop);
                AddSourceItem(_srcMenu, "自定义文件夹", SrcCustom);
                _srcMenu.Items.Add(new ToolStripSeparator());
                _srcMenu.Items.Add("管理自定义文件夹…", null, delegate { ManageFolders(); });
                _srcMenu.Show(_sourceBtn, new Point(0, _sourceBtn.Height));
            }
            catch (Exception)
            {
                _suppressDeactivate = false;
            }
        }

        private void SetSource(int mode)
        {
            if (_source == mode) return;
            _source = mode;
            ApplyFilter();      // 里面会 UpdateSourceButton + 重算提示
        }

        /// <summary>
        /// 「管理自定义文件夹…」：加/删目录 → 让调用方落盘并重扫 → 拿回新的候选列表。
        /// ★子对话框会让本窗口 Deactivate，所以整段压住自动关窗（不然一开文件夹选择框，选择器就没了）。
        /// </summary>
        private void ManageFolders()
        {
            if (_settings == null) return;
            List<string> now = _settings.ScanDirs == null ? new List<string>() : new List<string>(_settings.ScanDirs);
            _suppressDeactivate = true;
            List<string> res = null;
            try
            {
                res = ScanFoldersForm.Show(this, now);
            }
            finally
            {
                _suppressDeactivate = false;
            }
            if (res == null) return;                    // 取消：什么都不动

            _settings.ScanDirs = res;
            if (_onSourcesChanged != null)
            {
                try
                {
                    List<AppEntry> fresh = _onSourcesChanged();     // 调用方负责落盘 + 重扫
                    if (fresh != null)
                    {
                        _all = fresh;
                        // ★重扫回来的是**新的一批 AppEntry 实例**：清掉「已经请求过图标」的记录，
                        //   否则 IconSource 变过的条目（比如刚从空补成 .ico）不会再入队，绘制时会现场
                        //   同步取图（shell 调用 + 串行锁），候选一多就一条一条卡。
                        _requested.Clear();
                    }
                }
                catch (Exception) { }
            }
            _source = SrcCustom;        // 加完文件夹直接把视图切过去，用户立刻能看到结果
            _filter.Text = string.Empty;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string q = (_filter.Text ?? string.Empty).Trim();
            _filtered = new List<AppEntry>();
            for (int i = 0; i < _all.Count; i++)
            {
                AppEntry en = _all[i];
                if (en == null) continue;
                if (_source == SrcDesktop && en.OnDesktop == false) continue;   // 「来源 = 桌面」
                if (_source == SrcCustom && en.FromCustom == false) continue;   // 「来源 = 自定义文件夹」
                if (q.Length == 0 || en.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    _filtered.Add(en);
            }
            UpdateSourceButton();
            // ★M1：清掉「幽灵勾」—— 已经不在候选里的 Key（Accept 之后条目会被移出 _all）如果留在 Checked 里，
            //   就会出现「屏幕上没有任何打钩的行，按钮却写着添加 3」。
            List<string> ghosts = null;
            foreach (string k in _list.Checked)
            {
                bool inAll = false;
                for (int i = 0; i < _all.Count; i++)
                {
                    AppEntry a = _all[i];
                    if (a != null && string.Equals(a.Key, k, StringComparison.OrdinalIgnoreCase)) { inAll = true; break; }
                }
                if (inAll == false) { if (ghosts == null) ghosts = new List<string>(); ghosts.Add(k); }
            }
            if (ghosts != null) for (int i = 0; i < ghosts.Count; i++) _list.Checked.Remove(ghosts[i]);
            _ok.Enabled = _list.Checked.Count > 0;
            _hint = _filtered.Count == 0
                ? (q.Length == 0
                    ? (_all.Count == 0 ? "候选都加完了，点「完成」"
                        : (_source == SrcCustom && CustomDirCount() == 0
                            ? "还没有自定义文件夹 —— 点「来源」→「管理自定义文件夹…」加一个"
                            : (_source == SrcDesktop ? "桌面上没有可添加的了（点「来源」看全部）" : "没有可添加的应用")))
                    : (_source == SrcAll ? "没有匹配的应用" : "没有匹配的（点「来源」换个来源试试）"))
                : (_list.Checked.Count > 0 ? ("已选 " + _list.Checked.Count + " 个，点「添加」加进来")
                    : (_source == SrcCustom
                        ? ("自定义文件夹里有的应用 " + _filtered.Count + " 个（共加了 " + CustomDirCount() + " 个文件夹）" + CustomNote())
                        : (_source == SrcDesktop
                            ? ("桌面上有的应用 " + _filtered.Count + " 个（点「来源」可看全部 " + _all.Count + " 个）")
                            : ("全部应用 " + _filtered.Count + " 个（点「全选」可一次勾完）"))));
            _list.EmptyText = _all.Count == 0 ? "都加进来了 —— 点「完成」关闭" : "没有找到匹配的应用";
            _list.SetEntries(_filtered, false, false);
            RequestIcons();
            Invalidate();
        }

        /// <summary>打钩数变了：按钮文字和提示跟着变。</summary>
        private void UpdateChecks()
        {
            int n = _list.Checked.Count;
            // ★H3：已勾的条目可能被筛选挡在屏幕外（比如先框了 100 个、再筛 "chrome" 只剩 2 行）。
            //   按钮太窄放不下说明，就把「有多少是当前看不见的」写进左下角提示，别让用户蒙着点「添加 100」。
            int vis = 0;
            if (_filtered != null)
            {
                for (int i = 0; i < _filtered.Count; i++)
                {
                    AppEntry e2 = _filtered[i];
                    if (e2 != null && e2.Key != null && _list.Checked.Contains(e2.Key)) vis++;
                }
            }
            _ok.Text = n > 0 ? ("添加 " + n) : "添加";   // 别写成「添加（N）」：按钮只有 104px，括号会被省略号吃掉
            _ok.Enabled = n > 0;
            if (n > 0)
                _hint = (vis < n)
                    ? ("已选 " + n + " 个（其中 " + (n - vis) + " 个被当前筛选挡住了）")
                    : ("已选 " + n + " 个，点「添加」加进来");
            UpdateTitle();
            Invalidate();
        }

        /// <summary>标题永远显示「这个组现在有几个 / 现在选了几个」。
        /// ★分两段存：左边是「添加应用到「组名」」，右边是「已有 N 个 · 已选 M 个」。
        /// 以前拼成一整条字符串，组名一长就被省略号吃掉后半截（「已选 N 个」直接看不见）。</summary>
        private void UpdateTitle()
        {
            _title = "添加应用到「" + _groupName + "」";
            int sel = _list == null ? 0 : _list.Checked.Count;
            _titleInfo = sel > 0
                ? ("已有 " + _already + " 个 · 已选 " + sel + " 个")
                : ("已有 " + _already + " 个");
            if (_added > 0) _titleInfo += " · 本次 +" + _added;
        }

        private void RequestIcons()
        {
            if (_icons == null) return;
            int size = Theme.Px(this, IconSize);
            for (int i = 0; i < _filtered.Count; i++)
            {
                string k = _filtered[i].Key + "|" + size;
                if (!_requested.Add(k)) continue;
                _icons.BeginGet(_filtered[i], size, OnIconReady);
            }
        }

        private void OnIconReady(AppEntry en, Bitmap bmp)
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                BeginInvoke(new Action(delegate
                {
                    if (IsDisposed) return;
                    _list.NotifyIconArrived();
                }));
            }
            catch { }
        }

        /// <summary>把打钩的都加进来：从候选里摘掉、回调给主面板落盘、清空钩，**窗口不关**，可以接着挑。</summary>
        private void Accept()
        {
            List<AppEntry> picks = new List<AppEntry>();
            for (int i = 0; i < _all.Count; i++)
            {
                AppEntry en = _all[i];
                if (en != null && !string.IsNullOrEmpty(en.Key) && _list.Checked.Contains(en.Key)) picks.Add(en);
            }
            if (picks.Count == 0)
            {
                _hint = "先点一下要加的应用（会打钩）";
                Invalidate();
                return;
            }

            for (int i = 0; i < picks.Count; i++)
            {
                AppEntry en = picks[i];
                _all.Remove(en);
                _added++;
                _already++;
                if (_onAdd != null)
                {
                    try { _onAdd(en); }
                    catch (Exception ex) { try { ConfigStore.Log(Program.AppDir, "「添加应用」落盘回调抛异常（界面显示加好了，其实可能没写进去）：" + ex.GetType().Name + " " + ex.Message); } catch (Exception) { } }
                }
            }
            ApplyFilter();
            _list.UncheckAll();
            _hint = "已加入 " + picks.Count + " 个，可以接着挑";
            _ok.Text = "添加";
            _ok.Enabled = false;
            UpdateTitle();
            Invalidate();
        }

        /// <summary>
        /// 右键一个候选：弹出小菜单（打钩 / 取消打钩、打开文件所在位置、复制应用名）。
        /// ★和「查看全部」窗口的菜单**故意不同**：这里没有「启动」—— 选择器的职责是挑应用，
        ///   右键顺手启动一个应用既不合直觉、也容易误触。
        /// ★必须自己压住「失去焦点自动关窗」（_suppressDeactivate）：菜单是另一个窗口，
        ///   弹出来的瞬间主窗口就 Deactivate 了，不压住就等于「右键一下窗口直接没了」。
        /// ★菜单**复用同一个实例**（`_ctx`），在 OnFormClosed 里 Dispose：
        ///   不在自己的 Closed 回调里 Dispose —— 那个时机 WinForms 的菜单过滤器还在收尾，
        ///   先销毁会留下「偶发 ObjectDisposedException / 菜单模式残留」的窗口。
        /// </summary>
        private void OnEntryContext(AppEntry en, Point at)
        {
            if (en == null) return;
            _suppressDeactivate = true;
            try
            {
                if (_ctx == null)
                {
                    _ctx = new ContextMenuStrip();
                    _ctx.Renderer = new DarkMenuRenderer();
                    _ctx.ShowImageMargin = false;
                    _ctx.Font = Theme.Ui(9.75f);
                    _ctx.Closed += delegate { _suppressDeactivate = false; };
                }
                FillEntryMenu(_ctx, en);
                _ctx.Show(_list, at);
            }
            catch (Exception)
            {
                _suppressDeactivate = false;
            }
        }

        /// <summary>把菜单项填进给定菜单（先清空：菜单是复用的，条目状态每次都要按当前打钩情况重算）。</summary>
        private void FillEntryMenu(ContextMenuStrip menu, AppEntry en)
        {
            menu.Items.Clear();
            AppEntry entry = en;
            menu.Items.Add(_list.IsChecked(en) ? "取消打钩" : "打钩", null,
                delegate { _list.ToggleCheck(entry); });
            menu.Items.Add(new ToolStripSeparator());
            if (en != null && en.IsRealFile)
                menu.Items.Add("打开文件所在位置", null, delegate { OpenFileLocation(entry); });
            ToolStripMenuItem labelItem = new ToolStripMenuItem("名字常驻显示（画在图标下）");
            labelItem.Checked = entry != null && entry.ShowName;
            labelItem.Click += delegate { SetLabel(entry, entry == null ? false : !entry.ShowName); };
            menu.Items.Add(labelItem);
            menu.Items.Add("重命名…", null, delegate { RenameEntry(entry); });
            if (entry != null && string.IsNullOrEmpty(entry.CustomName) == false)
                menu.Items.Add("恢复原名（" + entry.Name + "）", null, delegate { SetName(entry, null); });
            menu.Items.Add("复制显示名", null, delegate { CopyText(entry.DisplayName); });
        }

        /// <summary>新建一份菜单内容（自检用：真的弹窗在无人值守时会挂住，测不了）。
        /// 走的是和界面上同一个 FillEntryMenu，所以断言到的就是用户看到的。</summary>
        internal ContextMenuStrip BuildEntryMenu(AppEntry en)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.ShowImageMargin = false;
            menu.Font = Theme.Ui(9.75f);
            FillEntryMenu(menu, en);
            return menu;
        }

        /// <summary>在资源管理器里定位到这个应用。
        /// ★这里**不关窗**（「查看全部」窗口那条会关）：选择器里可能已经勾了好几个，
        ///   关掉会把勾选全丢掉 —— 与「加过的从候选消失、可以接着挑」的多选模型直接打架。
        ///   资源管理器抢走焦点会让本窗口 Deactivate，所以先压住自动关（用户回到选择器时 OnActivated 解除）。</summary>
        private void OpenFileLocation(AppEntry en)
        {
            _suppressDeactivate = true;
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + en.Path + "\"");
                psi.UseShellExecute = false;
                System.Diagnostics.Process.Start(psi);
                _hint = "已经在资源管理器里定位到「" + en.Name + "」";
                Invalidate();
            }
            catch (Exception)
            {
                _suppressDeactivate = false;
                _hint = "打不开资源管理器（可能是系统限制）";
                Invalidate();
            }
        }

        /// <summary>开/关「名字常驻显示」：写 Settings.NameLabels → 让调用方落盘 → 重算列表。</summary>
        private void SetLabel(AppEntry en, bool on)
        {
            AppNames.SetLabel(_settings, en, on);
            if (_onPersist != null) { try { _onPersist(); } catch (Exception) { } }
            ApplyFilter();
        }

        /// <summary>设/清自定义显示名（空 = 恢复原名）：写 Settings.Renames → 让调用方落盘 → 重算列表。</summary>
        private void SetName(AppEntry en, string name)
        {
            AppNames.Set(_settings, en, name);
            if (_onPersist != null) { try { _onPersist(); } catch (Exception) { } }
            ApplyFilter();
        }

        private void RenameEntry(AppEntry en)
        {
            if (en == null) return;
            string now = en.DisplayName;
            _suppressDeactivate = true;      // 弹输入框会让本窗口 Deactivate，先压住自动关
            string name;
            try { name = TextPromptForm.Ask(this, "重命名（留空恢复原名）", now, "保存"); }
            finally { _suppressDeactivate = false; }
            if (name == null) return;
            name = name.Trim();
            if (string.Equals(name, now, StringComparison.Ordinal)) return;
            SetName(en, string.Equals(name, en.Name, StringComparison.Ordinal) ? null : name);
        }

        private void CopyText(string text)
        {
            try
            {
                Clipboard.SetText(text);
                _hint = "已经复制「" + text + "」";
                Invalidate();
            }
            catch (Exception)
            {
                // 剪贴板被别的程序占着时 SetText 会抛 —— 别静默失败，让用户知道为什么没复制上
                _hint = "复制失败：剪贴板被其他程序占用，稍后再试";
                Invalidate();
            }
        }

        /// <summary>回到这个窗口 = 用户回来了：解除「别自动关」的压制。</summary>
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            _suppressDeactivate = false;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            if (keyData == Keys.Enter) { Accept(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _filter.Focus();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_suppressDeactivate) return;   // 右键菜单正开着：那不是「用户离开这个窗口」
            if ((DateTime.Now - _shownAt).TotalMilliseconds < 600) return;
            // ★M5：已经勾了东西就别自作主张关窗。用户很可能只是切出去查一下这个软件叫什么，
            //   关掉会把勾好的全部作废 —— 这与「加过的从候选消失、可以接着挑」的多选模型直接打架。
            if (_list != null && _list.Checked.Count > 0) return;
            if (DialogResult == DialogResult.None) DialogResult = DialogResult.Cancel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Theme.BgBottom))
                    g.FillRectangle(b, ClientRectangle);
                // 标题：左边「添加应用到「组名」」+ 右边「已有 N 个 · 已选 M 个」分开画。
                // 合成一条的话，组名一长右边那半句就被省略号吃掉（用户反馈「挤得看不见」）。
                int padL = Theme.Px(this, 24);
                Font infoFont = Theme.Ui(9.5f);
                int infoW = TextRenderer.MeasureText(g, _titleInfo, infoFont,
                    new Size(int.MaxValue, Theme.Px(this, 28)), TextFormatFlags.NoPrefix).Width + Theme.Px(this, 6);
                int titleW = Math.Max(Theme.Px(this, 90), ClientSize.Width - padL * 2 - infoW - Theme.Px(this, 12));
                Theme.DrawText(g, _title, Theme.Ui(11f, FontStyle.Bold),
                    new Rectangle(padL, Theme.Px(this, 18), titleW, Theme.Px(this, 28)),
                    Theme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                Theme.DrawText(g, _titleInfo, infoFont,
                    new Rectangle(padL + titleW + Theme.Px(this, 12), Theme.Px(this, 18), infoW, Theme.Px(this, 28)),
                    Theme.TextDim, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                // 提示文字比较长、位置又窄，这里单独用 9pt 且允许省略，别挤到按钮上
                Theme.FillRound(g, _filterRect, Theme.Px(this, 8), Theme.Surface);
                Theme.DrawRound(g, _filterRect, Theme.Px(this, 8), _filter.Focused ? Theme.Accent : Theme.Border, 1f);
                if (string.IsNullOrEmpty(_filter.Text))
                {
                    Theme.DrawText(g, "输入名字筛选…", Theme.Ui(10f),
                        new Rectangle(_filterRect.X + Theme.Px(this, 12), _filterRect.Y, _filterRect.Width - Theme.Px(this, 24), _filterRect.Height),
                        Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
                // 提示：右边一直顶到「完成」按钮左边，别只给 180px（长提示会被省略号吃掉）
                int hintW = Math.Max(Theme.Px(this, 120), _cancel.Left - padL - Theme.Px(this, 10));
                Theme.DrawText(g, _hint, Theme.Ui(9f),
                    new Rectangle(padL, _cancel.Top, hintW, Theme.Px(this, 36)),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                using (Pen p = new Pen(Theme.Border))
                    g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
            catch (Exception ex) { Theme.PaintCatch(this, e, "添加应用选择器", ex); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { if (_timer != null) _timer.Dispose(); } catch { }
            // 菜单在这里销毁（不是在自己的 Closed 回调里）：三路复位里的最后一路，
            // 保证「窗口没了但 _suppressDeactivate 还停在 true」这种事不会发生。
            _suppressDeactivate = false;
            try { if (_ctx != null) { _ctx.Dispose(); _ctx = null; } } catch { }
            try { if (_srcMenu != null) { _srcMenu.Dispose(); _srcMenu = null; } } catch { }
            base.OnFormClosed(e);
        }
    }

    // ============================================================
    // 管理「自定义扫描文件夹」
    // ============================================================
    /// <summary>
    /// 加 / 删「额外扫描目录」。规则和桌面一致：每个文件夹扫**它自己 + 一级子文件夹**，
    /// 只认 .lnk / .exe / .url（不递归到底 —— 有的目录递归下去上万项，又慢又脏）。
    /// </summary>
    public class ScanFoldersForm : Form
    {
        private ListBox _list;
        private FlatButton _add;
        private FlatButton _typeBtn;
        private FlatButton _del;
        private FlatButton _ok;
        private readonly List<string> _dirs = new List<string>();
        private string _hint1 = string.Empty;
        private string _hint2 = string.Empty;
        private string _warn = string.Empty;      // 「网络位置不扫 / 路径不存在」这类即时提醒（红字）
        private Rectangle _headRect;
        private Rectangle _hintRect;
        private Rectangle _warnRect;

        private ScanFoldersForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BgBottom;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Theme.ApplyRoundedCorners(this);
        }

        /// <summary>自检用：建好但不走模态，由调用方自己 Show / DrawToBitmap（--previewscan 就是干这个的）。
        /// ★一定要真的 Show 过子控件才画得出来（没 Show 的表单 DrawToBitmap 只有背景）。</summary>
        public static ScanFoldersForm CreateForPreview(List<string> current)
        {
            ScanFoldersForm f = new ScanFoldersForm();
            f.Init(current);
            return f;
        }

        /// <summary>返回改完之后的目录列表；按 Esc / 关窗返回 null（表示「什么都没改」）。</summary>
        public static List<string> Show(IWin32Window owner, List<string> current)
        {
            ScanFoldersForm f = new ScanFoldersForm();
            try
            {
                f.Init(current);
                DialogResult r = owner == null ? f.ShowDialog() : f.ShowDialog(owner);
                return r == DialogResult.OK ? f._dirs : null;
            }
            finally
            {
                f.Dispose();
            }
        }

        private void Init(List<string> current)
        {
            if (current != null) _dirs.AddRange(current);
            // ClientSize 交给 ApplyLayout() 统一算（这里再写一遍只是死数字，会被它覆盖）

            _list = new ListBox();
            _list.BorderStyle = BorderStyle.None;
            _list.BackColor = Theme.Surface;
            _list.ForeColor = Theme.TextPrimary;
            _list.Font = Theme.Ui(10f);
            _list.IntegralHeight = false;
            Controls.Add(_list);

            _typeBtn = new FlatButton();
            _typeBtn.Style = FlatButton.Look.IconText;
            _typeBtn.Glyph = "\uE8A5";      // MDL2 Keyboard（手打/粘贴路径）
            _typeBtn.Text = "输入路径…";
            _typeBtn.Click += delegate { AddFolderByPath(); };
            Controls.Add(_typeBtn);

            _del = new FlatButton();
            _del.Style = FlatButton.Look.IconText;
            _del.Glyph = "\uE74D";      // MDL2 Delete
            _del.Text = "移除";
            _del.Click += delegate { RemoveSelected(); };
            Controls.Add(_del);

            _add = new FlatButton();
            _add.Style = FlatButton.Look.IconText;
            _add.Glyph = "\uE8B7";      // MDL2 FolderOpen（「添加文件夹」用文件夹图标更直观）
            _add.Text = "添加文件夹…";
            _add.Accent = true;
            _add.Click += delegate { AddFolder(); };
            Controls.Add(_add);

            _ok = new FlatButton();
            _ok.Style = FlatButton.Look.IconText;
            _ok.Glyph = "\uE8FB";
            _ok.Text = "完成";
            _ok.Click += delegate { DialogResult = DialogResult.OK; };
            Controls.Add(_ok);

            ApplyLayout();
            RefreshList();
        }

        /// <summary>
        /// 按**当前** DPI 重算窗口大小与所有子控件位置（和另外两个对话框同一个理由：
        /// 构造期还没有窗口句柄，`Theme.Px` 读到的 `DeviceDpi` 还是缺省 96，高 DPI 下会整体偏小）。
        /// </summary>
        private void ApplyLayout()
        {
            ClientSize = new Size(Theme.Px(this, 620), Theme.Px(this, 440));

            int pad = Theme.Px(this, 24);
            _headRect = new Rectangle(pad, Theme.Px(this, 18), ClientSize.Width - pad * 2, Theme.Px(this, 26));
            // 提示分两行：合成一行在 520 宽里会被省略号吃掉后半句（实测就是这样）
            _hintRect = new Rectangle(pad, Theme.Px(this, 44), ClientSize.Width - pad * 2, Theme.Px(this, 40));

            int bh = Theme.Px(this, 36);
            int by = ClientSize.Height - Theme.Px(this, 18) - bh;
            int okW = Theme.Px(this, 104);
            int delW = Theme.Px(this, 104);      // 「移除」+ 垃圾桶图标：88 时文字会被截成「移…」
            int addW = Theme.Px(this, 168);      // 「添加文件夹…」+ 文件夹图标：148 时会被截成「添加文件…」
            int typeW = Theme.Px(this, 152);     // 「输入路径…」：原生文件夹框选网络位置时不好用，给个手打/粘贴入口
            _ok.Bounds = new Rectangle(ClientSize.Width - pad - okW, by, okW, bh);
            _del.Bounds = new Rectangle(ClientSize.Width - pad - okW - Theme.Px(this, 8) - delW, by, delW, bh);
            _add.Bounds = new Rectangle(pad, by, addW, bh);
            _typeBtn.Bounds = new Rectangle(pad + addW + Theme.Px(this, 8), by, typeW, bh);
            _warnRect = new Rectangle(pad, Theme.Px(this, 84), ClientSize.Width - pad * 2, Theme.Px(this, 22));

            int listY = Theme.Px(this, 110);     // 标题 18/44 + 两行提示 + 提醒行（84）之后再开始列表
            _list.Bounds = new Rectangle(pad, listY, Math.Max(Theme.Px(this, 80), ClientSize.Width - pad * 2),
                                         Math.Max(Theme.Px(this, 60), by - Theme.Px(this, 14) - listY));
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try { ApplyLayout(); }   // 句柄已建：这时 DeviceDpi 才是真实值（高 DPI 下才看得出差别）
            catch (Exception) { }
        }

        private void RefreshList()
        {
            int keep = _list.SelectedIndex;
            _list.Items.Clear();
            for (int i = 0; i < _dirs.Count; i++)
            {
                // 标出「当前不存在」的目录（拔掉的 U 盘 / 网络盘 / 手打错的路径）——
                // 不自动删掉：盘插回来它又能用了。
                bool exists = false;
                try { exists = Directory.Exists(_dirs[i]); }
                catch (Exception) { }
                _list.Items.Add(exists ? _dirs[i] : (_dirs[i] + "    （当前不存在，先留着）"));
            }
            if (keep >= 0 && keep < _list.Items.Count) _list.SelectedIndex = keep;
            _hint1 = _dirs.Count == 0 ? "还没加文件夹 —— 点「添加文件夹…」选一个" : ("已加 " + _dirs.Count + " 个文件夹" + MissingNote());
            _hint2 = "每个文件夹扫它自己 + 一级子文件夹；只认 .lnk / .exe / .url";
            Invalidate();
        }

        /// <summary>有几个目录当前不存在（拔掉的盘 / 手打错的路径）—— 直接写在提示里，别让用户猜。</summary>
        private string MissingNote()
        {
            int missing = 0;
            for (int i = 0; i < _dirs.Count; i++)
            {
                try { if (!Directory.Exists(_dirs[i])) missing++; }
                catch (Exception) { }
            }
            return missing == 0 ? string.Empty : ("（其中 " + missing + " 个当前不存在，已跳过）");
        }

        private void AddFolder()
        {
            try
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "选择要扫描的文件夹（里面的快捷方式和 exe 会出现在「添加应用」列表里）";
                    dlg.ShowNewFolderButton = true;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    string dir = dlg.SelectedPath;
                    if (string.IsNullOrEmpty(dir)) return;
                    string norm = NormDir(dir);
                    for (int i = 0; i < _dirs.Count; i++)
                        if (string.Equals(NormDir(_dirs[i]), norm, StringComparison.OrdinalIgnoreCase)) return;   // 已经有了（大小写/末尾斜杠不同也算同一个）
                    _dirs.Add(norm);
                    RefreshList();
                    _list.SelectedIndex = _list.Items.Count - 1;
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 手动输入 / 粘贴一个路径。为什么要有这个入口：原生 `FolderBrowserDialog` 在「网络」邻居或
        /// 离线的映射盘上自己会转圈，而且粘 `\\NAS\share` 这种路径它也不好选。这里直接收字符串：
        /// 归一化 → 去重 → 提示（网络位置**不扫**、路径不存在也先留着）。
        /// </summary>
        private void AddFolderByPath()
        {
            string input = TextPromptForm.Ask(this, "输入或粘贴文件夹路径（例如 D:\\Games）", string.Empty, "添加");
            if (string.IsNullOrEmpty(input)) return;
            string dir = NormDir(input);
            if (dir.Length == 0) return;

            for (int i = 0; i < _dirs.Count; i++)
                if (string.Equals(NormDir(_dirs[i]), dir, StringComparison.OrdinalIgnoreCase))
                {
                    _warn = "这个文件夹已经在列表里了";
                    RefreshList();
                    return;
                }

            bool exists = false;
            try { exists = Directory.Exists(dir); }
            catch (Exception) { }
            if (AppsFolderScanner.IsNetworkDir(dir))
                _warn = "这是网络位置：BreadLauncher 只扫本地目录（扫描是同步的，网络盘会卡住界面），加进来也不会扫";
            else if (!exists)
                _warn = "这个路径现在不存在（先留着，盘插回来就能用）";
            else
                _warn = string.Empty;

            _dirs.Add(dir);
            RefreshList();
            _list.SelectedIndex = _list.Items.Count - 1;
        }

        private void RemoveSelected()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _dirs.Count) return;
            _dirs.RemoveAt(i);
            RefreshList();
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(i, _list.Items.Count - 1);
        }

        /// <summary>比较目录时用：去掉末尾的斜杠（`D:\a` 与 `D:\a\` 是同一个地方），但别把 `C:\` 弄成 `C:`。</summary>
        private static string NormDir(string d)
        {
            if (string.IsNullOrEmpty(d)) return string.Empty;
            string s = d.Trim();
            while (s.Length > 3 && (s.EndsWith("\\", StringComparison.Ordinal) || s.EndsWith("/", StringComparison.Ordinal)))
                s = s.Substring(0, s.Length - 1);
            return s;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            if (keyData == Keys.Enter) { DialogResult = DialogResult.OK; return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Theme.BgBottom))
                    g.FillRectangle(b, ClientRectangle);
                Theme.DrawText(g, "自定义扫描文件夹", Theme.Ui(11f, FontStyle.Bold), _headRect, Theme.TextPrimary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                Theme.DrawText(g, _hint1, Theme.Ui(9f),
                    new Rectangle(_hintRect.X, _hintRect.Y, _hintRect.Width, Theme.Px(this, 20)), Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                Theme.DrawText(g, _hint2, Theme.Ui(9f),
                    new Rectangle(_hintRect.X, _hintRect.Y + Theme.Px(this, 20), _hintRect.Width, Theme.Px(this, 20)), Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                if (!string.IsNullOrEmpty(_warn))
                    Theme.DrawText(g, _warn, Theme.Ui(9f), _warnRect, Theme.Accent,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                using (Pen p = new Pen(Theme.Border))
                    g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
            catch (Exception ex) { Theme.PaintCatch(this, e, "自定义扫描文件夹", ex); }
        }
    }
}
