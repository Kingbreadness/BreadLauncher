// GroupAppsForm.cs —— 「查看全部」：把一个分组里的应用整份列出来
//
// 为什么需要它：大文件夹最多铺 3×3 九格。超过 9 个时第 9 格显示「+N」，
// 第 9 个及以后的应用**曾经**既点不到、也没法单独移除（那是第二轮之前的唯一硬缺口，已由本窗口补上）。
// 这个窗口给出完整清单：点谁启动谁（和面板里点小图标一致，一下就走），
// 右键单项可以「从分组移除 / 打开文件所在位置 / 复制应用名」，Esc 或「关闭」返回。
//
// 复用 AllAppsList（和「添加应用」选择器同一个控件），所以滚动条、图标、
// 悬停高亮的手感和选择器完全一致，不引入第二套列表。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BreadLauncher
{
    public class GroupAppsForm : Form
    {
        private const int IconSize = 24;

        private IconService _icons;
        private AppGroup _group;
        private List<AppEntry> _apps = new List<AppEntry>();
        private AllAppsList _list;
        private FlatButton _close;

        private AppEntry _result;
        private Settings _settings;      // 改名要写进 Settings.Renames（自定义显示名）
        private Action _changed;
        private string _title = string.Empty;
        private string _hint = string.Empty;
        private Rectangle _headRect;
        private DateTime _shownAt = DateTime.Now;
        private bool _suppressDeactivate;

        private int _pendingIcons;
        private readonly HashSet<string> _requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private GroupAppsForm()
        {
            Text = "BreadLauncher 查看全部"; // 无边框窗口看不到标题，留着是给自检 / 自动化认窗口用的
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BgBottom;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Theme.ApplyRoundedCorners(this);
        }

        /// <summary>还有多少个图标在后台取（自检模式用它等加载完成）。</summary>
        public int PendingIcons
        {
            get { lock (this) { return _pendingIcons; } }
        }

        /// <summary>
        /// 列出整组应用。用户点了某个应用要启动就返回它，直接关掉返回 null。
        /// changed 在「从分组移除」之后回调，用来落盘并刷新后面板。
        /// </summary>
        public static AppEntry Show(IWin32Window owner, IconService icons, AppGroup group, List<AppEntry> apps, Action changed, Settings settings)
        {
            GroupAppsForm f = Create(icons, group, apps, changed, settings);
            try
            {
                DialogResult r = owner == null ? f.ShowDialog() : f.ShowDialog(owner);
                return r == DialogResult.OK ? f._result : null;
            }
            finally
            {
                f.Dispose();
            }
        }

        /// <summary>自检用：建好但不 ShowDialog，由调用方自己 Show / DrawToBitmap。</summary>
        public static GroupAppsForm Create(IconService icons, AppGroup group, List<AppEntry> apps, Action changed, Settings settings)
        {
            GroupAppsForm f = new GroupAppsForm();
            f.Init(icons, group, apps, changed, settings);
            return f;
        }

        private void Init(IconService icons, AppGroup group, List<AppEntry> apps, Action changed, Settings settings)
        {
            _icons = icons;
            _settings = settings;
            _group = group;
            _changed = changed;
            if (apps != null) _apps = new List<AppEntry>(apps);
            _title = (group == null || string.IsNullOrEmpty(group.Name)) ? "分组" : group.Name;
            _shownAt = DateTime.Now;

            _list = new AllAppsList();
            _list.Icons = icons;
            _list.EmptyText = "这个分组还没有应用";
            _list.Activated += OnEntryActivated;
            _list.ContextRequested += OnEntryContext;
            _list.AllowReorder = true;          // 按住一行上下拖 = 换组内顺序（不移动仍是「点一下启动」）
            _list.Reordered += OnReordered;
            Controls.Add(_list);

            _close = new FlatButton();
            _close.Style = FlatButton.Look.IconText;
            _close.Glyph = "\uE8BB";
            _close.Text = "关闭";
            _close.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(_close);

            ApplyLayout();
            Rebuild();
        }

        /// <summary>
        /// 尺寸按条目数长高（最多先显示 8 行，再多了就滚动）。
        /// Init 和 OnLoad 各调一次：OnLoad 时窗口句柄已建好，DeviceDpi 才是准的，
        /// 这样 125% / 150% 缩放下不会偏小。
        /// </summary>
        private void ApplyLayout()
        {
            int pad = Theme.Px(this, 24);
            int rowH = Theme.Px(this, Theme.Scroll.ListRow);   // 和列表控件同一份行高（别再各写 42）
            int visible = Math.Max(1, Math.Min(_apps.Count, 8));

            int w = Theme.Px(this, 440);
            int h = Theme.Px(this, 104) + visible * rowH + Theme.Px(this, 56);
            int cap = Theme.Px(this, 620);
            if (h > cap) h = cap;
            if (ClientSize.Width != w || ClientSize.Height != h) ClientSize = new Size(w, h);

            int bw = Theme.Px(this, 104);
            int bh = Theme.Px(this, 36);
            int by = ClientSize.Height - Theme.Px(this, 18) - bh;
            _close.Bounds = new Rectangle(ClientSize.Width - pad - bw, by, bw, bh);

            _headRect = new Rectangle(pad, Theme.Px(this, 20), Math.Max(Theme.Px(this, 40), ClientSize.Width - pad * 2), Theme.Px(this, 26));
            int listY = _headRect.Bottom + Theme.Px(this, 8);
            // 列表最小高度（注意：这个 42 是**最小高度**，不是行高 —— 行高在 Theme.Scroll.ListRow）
            _list.Bounds = new Rectangle(pad, listY, Math.Max(Theme.Px(this, 80), ClientSize.Width - pad * 2),
                                         Math.Max(Theme.Px(this, 42), by - Theme.Px(this, 26) - listY));
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyLayout();
            Rebuild();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // ★★和 AppPickerForm 同一条规矩：「600ms 防误关」的计时必须**从窗口真的出现算起**，
            //   不能只靠构造函数那一次赋值 —— 构造到显示之间可能隔着几百毫秒（排图标更久），
            //   那时守卫早就过期了，排队里的一条失焦消息就能把用户还没看见的窗口当场关掉。
            //   （2026-09-28 查探针「添加应用」断言整段丢失时发现的同族问题，两处一起修。）
            _shownAt = DateTime.Now;
        }

        private void Rebuild()
        {
            _list.SetEntries(_apps, false, false, true);   // keepOffset：拖完顺序 / 移除一条之后别跳回顶部
            _hint = _apps.Count == 0
                ? "这个分组还没有应用"
                : (_apps.Count + " 个应用 · 点一下直接启动");
            RequestIcons();
            Invalidate(true);
        }

        private void RequestIcons()
        {
            if (_icons == null) return;
            int size = Theme.Px(this, IconSize);
            for (int i = 0; i < _apps.Count; i++)
            {
                string k = _apps[i].Key + "|" + size;
                if (!_requested.Add(k)) continue;
                lock (this) { _pendingIcons++; }
                _icons.BeginGet(_apps[i], size, OnIconReady);
            }
        }

        private void OnIconReady(AppEntry en, Bitmap bmp)
        {
            lock (this) { _pendingIcons--; }
            try
            {
                if (IsDisposed || Disposing || IsHandleCreated == false) return;
                BeginInvoke(new Action(delegate
                {
                    if (IsDisposed) return;
                    _list.NotifyIconArrived();
                }));
            }
            catch (Exception) { }
        }

        private void OnEntryActivated(AppEntry en)
        {
            if (en == null) return;
            _result = en;
            DialogResult = DialogResult.OK;
        }

        /// <summary>点一下就走：启动交给 MainForm 的启动链路，这里只把选中的条目交出去。</summary>
        private void OnEntryContext(AppEntry en, Point at)
        {
            if (en == null) return;
            _suppressDeactivate = true;
            try
            {
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Renderer = new DarkMenuRenderer();
                menu.ShowImageMargin = false;
                menu.Font = Theme.Ui(9.75f);

                AppEntry entry = en;
                menu.Items.Add("启动", null, delegate { OnEntryActivated(entry); });
                if (_group != null)
                    menu.Items.Add("从「" + _group.Name + "」移除", null, delegate { RemoveEntry(entry); });
                menu.Items.Add(new ToolStripSeparator());
                if (en.IsRealFile)
                    menu.Items.Add("打开文件所在位置", null, delegate { OpenFileLocation(entry); });
                ToolStripMenuItem labelItem = new ToolStripMenuItem("名字常驻显示（画在图标下）");
                labelItem.Checked = entry.ShowName;
                labelItem.Click += delegate { SetLabel(entry, !entry.ShowName); };
                menu.Items.Add(labelItem);
                menu.Items.Add("重命名…", null, delegate { RenameEntry(entry); });
                if (string.IsNullOrEmpty(entry.CustomName) == false)
                    menu.Items.Add("恢复原名（" + entry.Name + "）", null, delegate { SetName(entry, null); });
                menu.Items.Add("复制显示名", null, delegate { CopyText(entry.DisplayName); });

                menu.Closed += delegate
                {
                    _suppressDeactivate = false;
                    // ★延后 Dispose：在自己的 Closed 回调里直接销毁，WinForms 的菜单过滤器还在收尾，
                    //   会留下「偶发 ObjectDisposedException / 菜单模式残留」的窗口。
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            try { menu.Dispose(); } catch (Exception) { }
                        }));
                    }
                    catch (Exception)
                    {
                        try { menu.Dispose(); } catch (Exception) { }   // 窗口已没了：只能就地销毁
                    }
                };
                menu.Show(_list, at);
            }
            catch (Exception)
            {
                _suppressDeactivate = false;
            }
        }

        /// <summary>
        /// 组内拖拽排序：把第 from 行搬到第 to 位（都是**当前可见顺序**的下标）。
        /// ★真正要动的是配置里的 `AppGroup.Keys`：用「锚点」做法 —— 把被拖的 key 摘出来，
        ///   插到「它新位置后面那一项」的 key 前面。这样**扫描不到、不显示的 Key**（配置里留着等它回来的）
        ///   不会被搅乱顺序，也不会被顺手删掉。
        /// </summary>
        private void OnReordered(int from, int to)
        {
            if (_group == null || _apps == null) return;
            if (from < 0 || from >= _apps.Count) return;
            if (to < 0) to = 0;
            if (to > _apps.Count - 1) to = _apps.Count - 1;
            if (to == from) return;

            List<AppEntry> order = new List<AppEntry>(_apps);
            AppEntry moved = order[from];
            order.RemoveAt(from);
            order.Insert(to, moved);

            string anchorKey = (to + 1 < order.Count) ? order[to + 1].Key : null;
            List<string> keys = new List<string>(_group.Keys);
            int at = IndexOfKey(keys, moved.Key);
            if (at < 0) return;
            keys.RemoveAt(at);
            if (anchorKey == null) keys.Add(moved.Key);
            else
            {
                int anchor = IndexOfKey(keys, anchorKey);
                if (anchor < 0) keys.Add(moved.Key);
                else keys.Insert(anchor, moved.Key);
            }
            _group.Keys = keys;

            _apps = order;
            if (_changed != null) { try { _changed(); } catch (Exception ex) { try { ConfigStore.Log(Program.AppDir, "「查看全部」的改动落盘回调抛异常（界面显示改好了，其实可能没写进去）：" + ex.GetType().Name + " " + ex.Message); } catch (Exception) { } } }   // 调用方落盘 + 刷新面板
            Rebuild();
        }

        private static int IndexOfKey(List<string> keys, string key)
        {
            if (keys == null || string.IsNullOrEmpty(key)) return -1;
            for (int i = 0; i < keys.Count; i++)
                if (string.Equals(keys[i], key, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private void RemoveEntry(AppEntry en)
        {
            if (_group == null || en == null) return;
            _group.Remove(en.Key);

            List<AppEntry> keep = new List<AppEntry>();
            for (int i = 0; i < _apps.Count; i++)
                if (_group.Contains(_apps[i].Key)) keep.Add(_apps[i]);
            _apps = keep;

            if (_changed != null)
            {
                try { _changed(); }
                // ★同上：回调里是「把改动写进配置」。异常被吞掉 = 界面说改好了、磁盘上没有、日志里也没线索。
                catch (Exception ex) { try { ConfigStore.Log(Program.AppDir, "「查看全部」的改动落盘回调抛异常（界面显示改好了，其实可能没写进去）：" + ex.GetType().Name + " " + ex.Message); } catch (Exception) { } }
            }
            ApplyLayout();
            Rebuild();
        }

        /// <summary>开/关「名字常驻显示」（写 Settings.NameLabels），写完回调落盘 + 重画。</summary>
        private void SetLabel(AppEntry en, bool on)
        {
            AppNames.SetLabel(_settings, en, on);
            if (_changed != null) { try { _changed(); } catch (Exception ex) { try { ConfigStore.Log(Program.AppDir, "「查看全部」的改动落盘回调抛异常（界面显示改好了，其实可能没写进去）：" + ex.GetType().Name + " " + ex.Message); } catch (Exception) { } } }
            Rebuild();
        }

        /// <summary>设/清自定义显示名（空 = 恢复原名），写完回调落盘 + 重画列表。</summary>
        private void SetName(AppEntry en, string name)
        {
            AppNames.Set(_settings, en, name);
            if (_changed != null) { try { _changed(); } catch (Exception ex) { try { ConfigStore.Log(Program.AppDir, "「查看全部」的改动落盘回调抛异常（界面显示改好了，其实可能没写进去）：" + ex.GetType().Name + " " + ex.Message); } catch (Exception) { } } }
            Rebuild();
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

        private void OpenFileLocation(AppEntry en)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + en.Path + "\"");
                psi.UseShellExecute = false;
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception) { }
            DialogResult = DialogResult.Cancel;
        }

        private void CopyText(string text)
        {
            try { Clipboard.SetText(text); }
            catch (Exception) { }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_suppressDeactivate) return;
            if ((DateTime.Now - _shownAt).TotalMilliseconds < 600) return; // 刚弹出别被自己关掉
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
    
                Theme.DrawText(g, _title, Theme.Ui(11f, FontStyle.Bold), _headRect, Theme.TextPrimary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    
                Theme.DrawText(g, _hint, Theme.Ui(9f),
                    new Rectangle(_headRect.X, _close.Top, Math.Max(Theme.Px(this, 40), _close.Left - _headRect.X - Theme.Px(this, 12)), _close.Height),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    
                using (Pen p = new Pen(Theme.Border))
                    g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
            catch (Exception ex) { Theme.PaintCatch(this, e, "查看全部窗口", ex); }
        }
    }
}
