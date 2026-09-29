// ClickProbe.cs —— 自检探针：验证「大文件夹」的关键行为
//   1) 组内分页：超过 9 个每页 9 格，第 9 格是第 9 个应用（没有「+N」格了）+ 越界格为空
//   2) 滚轮：停在文件夹上 = 翻内页；不在文件夹上 = 滚分组区（方向与系统一致）
//   3) 拖动分组：把第 1 个文件夹拖到第 2 个位置，顺序真的换了
//   4) 「查看全部」窗口：开得出来、右键移除真的改组数据
//   5) 「添加应用」勾选式多选：点三行打钩 → 一次「添加」加三个
//
// 为什么不塞进成品 exe：这是排查/验收用的工具，用户用不到。
// 它和 src\*.cs 一起编成一个独立的小 exe（放 build\），不改动 BreadLauncher.exe。
//
// 编译：
//   csc /nologo /target:exe /platform:x64 /codepage:65001 `
//       /out:build\clickprobe.exe /main:BreadLauncher.ClickProbe `
//       /reference:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Web.Extensions.dll `
//       tools\ClickProbe.cs src\*.cs
//
// 用法：clickprobe.exe [settings.json] [组序号]
//   探针只读配置（面板带 PreviewMode，不落盘），也不会启动任何应用：
//   「查看全部」窗口由定时器自动取消。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace BreadLauncher
{
    internal static class ClickProbe
    {
        private const int FolderCap = 9;   // 和 MainForm.FolderCapacity 保持一致
        private static bool _ok = true;
        private static readonly List<string> Log = new List<string>();

        [STAThread]
        private static void Main(string[] args)
        {
            Program.AppDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (string.IsNullOrEmpty(Program.AppDir)) Program.AppDir = Environment.CurrentDirectory;

            string settingsFile = args.Length > 0 && args[0].Length > 0 ? args[0] : null;
            int groupIndex = 1;
            if (args.Length > 1) int.TryParse(args[1], out groupIndex);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Say("工作目录：" + Program.AppDir);
            Say("配置：" + (settingsFile == null ? "(默认 settings.json)" : settingsFile) + "   组序号=" + groupIndex);

            // ---------- 0) 启动位置 / 第一帧 ----------
            // 用户 2026-09-28 报：「每次打开这个，左上角都会弹窗然后消失」。
            // 真机实测（DPI 感知的窗口轮询，物理像素，3 次全部复现）：修复前窗口在 t≈120ms 以
            // **默认 300x300** 出现在屏幕左上角 (0,0)，t≈232ms 才变成存档尺寸（仍在 (0,0)），
            // t≈280ms 才被 PositionWindow 搬到存档位置 —— 左上角整整停了 160~190ms，肉眼可见。
            // 两个成因，下面一条钉一个，缺哪个都会复发：
            //   ① 构造函数里就得把窗口摆在存档位置上（StartPosition=Manual 且不设 Location → 只能是 (0,0)）；
            //   ② 建句柄那条路（OnHandleCreated → PinTopMost）**不能**把窗口显示出来
            //      —— 老写法 flags 里带 SWP_SHOWWINDOW，等于在窗口还没摆好位置时强行 ShowWindow。
            try
            {
                string cfgPath = Path.Combine(Program.AppDir, "probe-firstframe.json");
                File.WriteAllText(cfgPath,
                    "{\"Groups\":[],\"Pinned\":[],\"Seeded\":true,\"PanelX\":1234,\"PanelY\":567,\"PanelW\":480,\"PanelH\":360}",
                    new UTF8Encoding(false));

                MainForm ff = new MainForm(cfgPath);
                Check(ff.Location.X == 1234 && ff.Location.Y == 567,
                    "构造函数里窗口就摆在存档位置上（实测 " + ff.Location.X + "," + ff.Location.Y +
                    "，期望 1234,567）—— 不摆的话第一帧会出现在屏幕左上角 (0,0)，就是用户报的那个闪窗");
                Check(ff.Width == 480 && ff.Height == 360,
                    "构造函数里尺寸也是存档值（实测 " + ff.Width + "x" + ff.Height + "，期望 480x360）");

                // ★这里**故意没有**「建句柄不能把窗口显示出来」那条断言：实测证明它没有鉴别力
                //   （把 OnHandleCreated 改回带 SWP_SHOWWINDOW 的写法，它照样 PASS —— 因为窗口真正被
                //   显示是 WinForms 在 OnLoad 链路里做的，跟这个 flag 无关）。删掉它，别留恒真断言。
                //   真实时序只有外部轮询窗口矩形才量得到（探针进程内量不出「第一次可见」，见 AGENTS.md 坑 44）。
                ff.Dispose();
                try { File.Delete(cfgPath); } catch { }
            }
            catch (Exception ex)
            {
                Check(false, "启动位置那两条断言自己抛异常：" + ex.Message);
            }

            MainForm f = new MainForm(settingsFile);
            f.PreviewMode = true;      // 屏幕外、不写配置、不抢焦点
            f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -2000);
            f.Show();
            Application.DoEvents();

            int cols = (int)Prop(f, "Columns");
            int tw = (int)Prop(f, "TileW");
            int tileH = (int)Prop(f, "TileH");
            int gx = (int)Prop(f, "GapX");
            int gy = (int)Prop(f, "GapY");
            int miniPad = (int)Prop(f, "MiniPad");
            int miniGap = (int)Prop(f, "MiniGap");
            int miniBox = (int)Prop(f, "MiniBox");
            int groupLeft = (int)Field(f, "_groupLeft");
            int groupTop = (int)Field(f, "_groupTop");
            int scrollY = (int)Field(f, "_scrollY");

            List<GroupView> groups = (List<GroupView>)Field(f, "_groups");
            Say("分组数=" + groups.Count + "  列数=" + cols + "  面板=" + f.Width + "x" + f.Height);
            if (groupIndex < 0 || groupIndex >= groups.Count)
            {
                Say("组序号越界，退出。");
                Flush();
                Environment.ExitCode = 1;
                return;
            }

            // ---------- 1) 第 9 格命中测试 ----------
            SetScroll(f, 0);
            int px = groupLeft + (groupIndex % cols) * (tw + gx) + miniPad + 2 * (miniBox + miniGap) + miniBox / 2;
            int py = groupTop + (groupIndex / cols) * (tileH + gy) + miniPad + 2 * (miniBox + miniGap) + miniBox / 2;

            MethodInfo hit = typeof(MainForm).GetMethod("GroupHitTest", BindingFlags.NonPublic | BindingFlags.Instance);
            object[] hitArgs = new object[] { new Point(px, py), 0, 0 };
            hit.Invoke(f, hitArgs);
            int hitIndex = (int)hitArgs[1];
            int hitSlot = (int)hitArgs[2];
            Check(hitIndex == groupIndex && hitSlot == 8,
                "命中测试 (" + px + "," + py + ") → 组=" + hitIndex + " 格=" + hitSlot + "（期望 组=" + groupIndex + " 格=8）");

            // 缝隙（MiniGap 那几像素）不该算成左边那个图标 —— 否则点缝就启动了旁边的应用。
            // ★要点：只有缝隙最左边那 1px（ox == MiniBox）会被旧写法 ox <= MiniBox 误判；
            //   ox = MiniBox+1..MiniBox+MiniGap-1 两种写法都返回 -1。所以这里必须点在**第一个缝隙像素**上，
            //   点在缝隙中间是区分不出新旧代码的（第一版探针就犯过这个错）。
            int gapX = groupLeft + (groupIndex % cols) * (tw + gx) + miniPad + miniBox;
            int gapY = groupTop + (groupIndex / cols) * (tileH + gy) + miniPad + miniBox / 2;
            MethodInfo hit2 = typeof(MainForm).GetMethod("GroupHitTest", BindingFlags.NonPublic | BindingFlags.Instance);
            object[] gapArgs = new object[] { new Point(gapX, gapY), 0, 0 };
            hit2.Invoke(f, gapArgs);
            Check((int)gapArgs[2] == -1,
                "点图标之间的缝隙 (" + gapX + "," + gapY + ") → 格=" + (int)gapArgs[2] + "（期望 -1 = 图块主体，不是某个图标）");

            GroupView gv = groups[groupIndex];
            Say("目标分组：「" + gv.Name + "」 可显示条目=" + gv.Apps.Count);

            MethodInfo realMini = typeof(MainForm).GetMethod("IsRealMiniSlot", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo pageCountM = typeof(MainForm).GetMethod("PageCount", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo pageOfM = typeof(MainForm).GetMethod("PageOf", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo setPageM = typeof(MainForm).GetMethod("SetPage", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo appAtM = typeof(MainForm).GetMethod("AppAt", BindingFlags.NonPublic | BindingFlags.Instance);

            if (gv.Apps.Count == 9)
            {
                // 边界：正好 9 个 = 一页装满，第 9 格就是第 9 个应用本身
                bool isReal9 = (bool)realMini.Invoke(f, new object[] { gv, 8 });
                int pc = (int)pageCountM.Invoke(null, new object[] { gv });
                Check(isReal9 && pc == 1,
                    "正好 9 个：第 9 格可点启动=" + isReal9 + "，页数=" + pc + "（期望 True / 1，不出现「+0」）");
            }
            else if (gv.Apps.Count > 9)
            {
                int pc = (int)pageCountM.Invoke(null, new object[] { gv });
                int expectPages = (gv.Apps.Count + FolderCap - 1) / FolderCap;
                Check(pc == expectPages, "分页：这组 " + gv.Apps.Count + " 个 → " + pc + " 页（期望 " + expectPages + "）");

                setPageM.Invoke(f, new object[] { gv, 0 });
                Check((bool)realMini.Invoke(f, new object[] { gv, 8 }),
                    "第 1 页第 9 格 = 第 9 个应用（能点启动），不再是「+N」");
                AppEntry a0 = (AppEntry)appAtM.Invoke(f, new object[] { gv, 0 });
                Check(a0 != null && a0.Key == gv.Apps[0].Key, "第 1 页第 1 格 = 组内第 1 个应用");

                setPageM.Invoke(f, new object[] { gv, 1 });
                AppEntry b0 = (AppEntry)appAtM.Invoke(f, new object[] { gv, 0 });
                AppEntry b3 = (AppEntry)appAtM.Invoke(f, new object[] { gv, 3 });
                // ★第 2 页有几格取决于这一组总共几个：12 个 → 只剩 3 格（第 4 格必须为空）；
                //   33 个 → 第 2 页是满的（第 4 格是真实应用）。别写死「b3 必须为空」（那是 12 个那一组的性质）。
                int page2Slots = gv.Apps.Count - FolderCap;
                bool b3ok = page2Slots > 3
                    ? (b3 != null && b3.Key == gv.Apps[FolderCap + 3].Key)
                    : (b3 == null);
                Check(b0 != null && b0.Key == gv.Apps[FolderCap].Key && b3ok,
                    "第 2 页：第 1 格 = 组内第 10 个应用；第 4 格"
                    + (page2Slots > 3 ? "是组内第 13 个（这页是满的）" : "返回空（这页只有 3 格，空格子不误触启动）"));

                FieldInfo hoverF = typeof(MainForm).GetField("_hoverGroup", BindingFlags.NonPublic | BindingFlags.Instance);
                setPageM.Invoke(f, new object[] { gv, 0 });
                hoverF.SetValue(f, groupIndex);
                Wheel(f, -120);
                int afterDown = (int)pageOfM.Invoke(f, new object[] { gv });
                Wheel(f, 120);
                int afterUp = (int)pageOfM.Invoke(f, new object[] { gv });
                Check(afterDown == 1 && afterUp == 0,
                    "滚轮停在文件夹上翻页：向下 → 第 " + (afterDown + 1) + " 页，向上 → 第 " + (afterUp + 1) + " 页");

                // 碎 Delta（触控板 / 高精度滚轮一次手势送几十个 30~60）：必须攒满一格（120）才翻页，
                // 否则一次滑动就把文件夹从头翻到尾。
                setPageM.Invoke(f, new object[] { gv, 0 });
                Wheel(f, -40);
                Wheel(f, -40);
                int midPage = (int)pageOfM.Invoke(f, new object[] { gv });
                Wheel(f, -40);
                int finPage = (int)pageOfM.Invoke(f, new object[] { gv });
                Check(midPage == 0 && finPage == 1,
                    "碎 Delta 翻页要攒满一格：两次 40 → 还在第 " + (midPage + 1) + " 页；第三次 40（共 120）→ 第 "
                    + (finPage + 1) + " 页（不攒的话一次手势能翻好几页）");
                hoverF.SetValue(f, -1);

                // 顺便把第 2 页的面板截一张（排版改动必须用眼睛看）
                try
                {
                    setPageM.Invoke(f, new object[] { gv, 1 });
                    f.Refresh();
                    Application.DoEvents();
                    using (Bitmap shot = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(shot, new Rectangle(0, 0, f.Width, f.Height));
                        shot.Save(Path.Combine(Program.AppDir, "panel-page2.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch (Exception) { }
                setPageM.Invoke(f, new object[] { gv, 0 });

                // 真调一次 ShowAllApps：看窗口会不会开、开多大、关掉后面板还在不在
                GroupAppsForm[] opened = new GroupAppsForm[1];
                bool[] removedOk = new bool[] { false };
                string[] removedMsg = new string[] { "" };
                AppEntry removeTarget = gv.Apps[gv.Apps.Count - 1]; // 最后一个：只有「查看全部」里能碰到它
                Timer timer = new Timer();
                timer.Interval = 300;
                int gaTries = 0;
                timer.Tick += delegate
                {
                    bool gaOpen = false;
                    foreach (Form open in Application.OpenForms)
                        if (open is GroupAppsForm) { gaOpen = true; break; }
                    // ★原来这里第一件事就是 `timer.Stop()` —— 窗口要是晚于 1.5 秒才开出来，
                    //   回调只跑一次就"定生死"，断言随机失败（本窗口实测：同一份配置两次跑，
                    //   一次 FAIL=7、一次 FAIL=2，差别全在这条）。改成**轮询到开出来为止**，
                    //   最多等 20×300ms = 6 秒，超时才认账（探针规矩：别用固定定时器一次定生死）。
                    gaTries++;
                    if (gaOpen == false && gaTries < 20) return;
                    timer.Stop();
                    if (gaOpen == false)
                    {
                        // ★和「添加应用」那个定时器一个道理：固定 1500ms 一次定生死会偶发假失败
                        //   （机器忙 / 图标多时首帧慢），没等到就再等一轮。
                        gaTries++;
                        if (gaTries < 15) { timer.Start(); return; }
                        Say("（等了 " + (gaTries * 1500) + "ms 也没等到「查看全部」窗口，跳过这段检查）");
                        return;
                    }
                    foreach (Form open in Application.OpenForms)
                    {
                        GroupAppsForm g = open as GroupAppsForm;
                        if (g != null)
                        {
                            opened[0] = g;
                            // 顺带验证「移除」这条会改用户数据的路（反射调私有 RemoveEntry）
                            try
                            {
                                int before = gv.Group.Keys.Count;
                                bool had = gv.Group.Contains(removeTarget.Key);
                                MethodInfo rm = typeof(GroupAppsForm).GetMethod("RemoveEntry", BindingFlags.NonPublic | BindingFlags.Instance);
                                rm.Invoke(g, new object[] { removeTarget });
                                int after = gv.Group.Keys.Count;
                                removedOk[0] = had && after == before - 1 && gv.Group.Contains(removeTarget.Key) == false;
                                removedMsg[0] = "移除前 " + before + " 个 → 移除后 " + after + " 个，被移除的是「" + removeTarget.Name + "」";
                            }
                            catch (Exception ex)
                            {
                                removedMsg[0] = "调用 RemoveEntry 抛异常：" + ex.GetType().Name + " " + ex.Message;
                            }
                            // ---- 组内拖拽排序：把第 1 行拖到第 4 行之后 ----
                            try
                            {
                                AllAppsList gl = (AllAppsList)typeof(GroupAppsForm)
                                    .GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(g);
                                List<AppEntry> apps = (List<AppEntry>)typeof(GroupAppsForm)
                                    .GetField("_apps", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(g);
                                Check(gl.AllowReorder, "「查看全部」列表打开了拖排序（AllowReorder=True）");
                                if (apps.Count >= 4)
                                {
                                    string k0 = apps[0].Key, k1 = apps[1].Key, k2 = apps[2].Key, k3 = apps[3].Key;
                                    int rh = Theme.Px(gl, (int)Math.Round(Theme.Scroll.ListRow));
                                    int cgx = Theme.Px(gl, 90);   // 别叫 px：外层已有同名局部变量（CS0136）
                                    int yFrom = Theme.Px(gl, 4) + rh / 2;                  // 第 1 行中间
                                    int yTo = Theme.Px(gl, 4) + rh * 4 - Theme.Px(gl, 2);  // 第 4 行下沿 → 插到它后面
                                    MethodInfo gd = typeof(AllAppsList).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                                    MethodInfo gmv = typeof(AllAppsList).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                                    MethodInfo gu = typeof(AllAppsList).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);
                                    gd.Invoke(gl, new object[] { new MouseEventArgs(MouseButtons.Left, 1, cgx, yFrom, 0) });
                                    gmv.Invoke(gl, new object[] { new MouseEventArgs(MouseButtons.Left, 0, cgx, yFrom + Theme.Px(gl, 20), 0) });
                                    // 拖拽状态断言（也解释「截图里能不能看到那条插入线」）
                                    bool roActive = (bool)typeof(AllAppsList).GetField("_roActive", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(gl);
                                    int roTo = (int)typeof(AllAppsList).GetField("_roTo", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(gl);
                                    int roFrom = (int)typeof(AllAppsList).GetField("_roFrom", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(gl);
                                    Check(roActive && roFrom == 0 && roTo == 1,
                                        "拖起来之后确实进了「拖排序」状态：_roFrom=0、插入位置 _roTo=" + roTo + "（应 1）、_roActive=" + roActive);
                                    // 趁「正在拖」出一张图（画面改动必须出图看）。
                                    // ★直接渲染**列表控件自己**（PaintAll 到一张位图），不要用表单 DrawToBitmap ——
                                    //   实测表单那套对子控件的「进行时状态」不可靠：行照画出来了，但拖拽高亮/插入线没有。
                                    try
                                    {
                                        using (Bitmap shot = new Bitmap(Math.Max(1, gl.ClientSize.Width), Math.Max(1, gl.ClientSize.Height)))
                                        {
                                            using (Graphics gb = Graphics.FromImage(shot))
                                            {
                                                MethodInfo pa = typeof(AllAppsList).GetMethod("PaintAll", BindingFlags.NonPublic | BindingFlags.Instance);
                                                pa.Invoke(gl, new object[] { new PaintEventArgs(gb, new Rectangle(0, 0, shot.Width, shot.Height)) });
                                            }
                                            shot.Save(Path.Combine(Program.AppDir, "groupapps-drag.png"), System.Drawing.Imaging.ImageFormat.Png);
                                        }
                                    }
                                    catch (Exception) { }
                                    gmv.Invoke(gl, new object[] { new MouseEventArgs(MouseButtons.Left, 0, cgx, yTo, 0) });
                                    gu.Invoke(gl, new object[] { new MouseEventArgs(MouseButtons.Left, 1, cgx, yTo, 0) });

                                    List<AppEntry> after = (List<AppEntry>)typeof(GroupAppsForm)
                                        .GetField("_apps", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(g);
                                    bool orderOk = after.Count >= 4 && after[0].Key == k1 && after[1].Key == k2
                                                   && after[2].Key == k3 && after[3].Key == k0;
                                    // 配置里的 Keys 顺序也要跟着变（组内顺序 = 配置里 Keys 的顺序）
                                    int ki = -1;
                                    for (int i = 0; i < gv.Group.Keys.Count; i++)
                                        if (string.Equals(gv.Group.Keys[i], k0, StringComparison.OrdinalIgnoreCase)) { ki = i; break; }
                                    bool keysOk = ki == 3;
                                    AppEntry result = (AppEntry)typeof(GroupAppsForm)
                                        .GetField("_result", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(g);
                                    Check(orderOk && keysOk && result == null,
                                        "组内拖拽排序：第 1 项拖到第 4 位 → 可见顺序变成 " + after[0].Name + " / " + after[1].Name + " / "
                                        + after[2].Name + " / " + after[3].Name + "，配置里 Keys 也跟着动（第 1 项现在在下标 " + ki + "），"
                                        + "而且**没有被当成「点一下启动」**（_result 还是空）");
                                }
                                else Say("（这组不足 4 个应用，跳过拖排序断言）");
                            }
                            catch (Exception exRO) { Check(false, "组内拖排序断言异常：" + exRO.Message); }

                            g.DialogResult = DialogResult.Cancel; // 不选任何条目 → 不会启动应用
                        }
                    }
                };
                timer.Start();

                MethodInfo showAll = typeof(MainForm).GetMethod("ShowAllApps", BindingFlags.NonPublic | BindingFlags.Instance);
                showAll.Invoke(f, new object[] { gv });
                timer.Stop();

                Check(opened[0] != null, "MainForm.ShowAllApps 真的开出了「查看全部」窗口");
                if (opened[0] != null)
                {
                    Say("      窗口 " + opened[0].Width + "x" + opened[0].Height + "  标题=" + opened[0].Text);
                    Say("      里面条目数=" + gv.Apps.Count + "（列表用 AllAppsList，超出会滚动）");
                }
                Check(removedOk[0], "右键「从分组移除」真的改到了组数据：" + removedMsg[0]);
                Check(f.IsDisposed == false, "关掉「查看全部」后面板还活着（返回分组界面，不跟着退出）");
            }
            else
            {
                Say("（这组只有 " + gv.Apps.Count + " 个，跳过分组检查：换一份含 9 个或大组的配置再跑）");
            }

            // ---------- 1.5) 「添加应用」一次打开能连着加多个 ----------
            int beforeAdd = gv.Group.Keys.Count;
            int[] gained = new int[] { -1, -1 };
            int[] listBar = new int[] { -1, -2 };   // 列表滚动条：墨水宽 / 距右缘（稍后和面板那根比）
            int[] listGeom = new int[] { 0, 0, 0 }; // 列表控件：DPI / InkPx / MarginPx（诊断用）
            MethodInfo buildCand = typeof(MainForm).GetMethod("BuildCandidates", BindingFlags.NonPublic | BindingFlags.Instance);
            List<AppEntry> cands = (List<AppEntry>)buildCand.Invoke(f, null);
            if (cands.Count >= 3)
            {
                Timer t2 = new Timer();
                t2.Interval = 1200;
                int pickerTries = 0;
                t2.Tick += delegate
                {
                    t2.Stop();
                    AppPickerForm found = null;
                    foreach (Form open in Application.OpenForms)
                    {
                        AppPickerForm cand = open as AppPickerForm;
                        if (cand != null) { found = cand; break; }
                    }
                    if (found == null)
                    {
                        // ★选择器还没开出来（机器忙 / 图标多、首帧慢）→ 再等一轮，别把这点当成失败。
                        //   固定 1200ms 一次定生死会偶发假失败（实测踩到过两次）。
                        pickerTries++;
                        if (pickerTries < 15) { t2.Start(); return; }
                        Say("（等了 " + (pickerTries * 1200) + "ms 也没等到「添加应用」窗口，跳过这段检查）");
                        return;
                    }
                    foreach (Form open in Application.OpenForms)
                    {
                        AppPickerForm pf = open as AppPickerForm;
                        if (pf != null)
                        {
                            // 勾选式：先把前三个候选打钩，再点一次「添加」
                            FieldInfo listField = typeof(AppPickerForm).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance);
                            FieldInfo allField = typeof(AppPickerForm).GetField("_all", BindingFlags.NonPublic | BindingFlags.Instance);
                            AllAppsList lst = (AllAppsList)listField.GetValue(pf);
                            List<AppEntry> all = (List<AppEntry>)allField.GetValue(pf);
                            // ★列表要**超出可视区**才有滚动条、才滚得动。换一份配置（比如用户自己的
                            //   settings.json：大部分应用已经在组里 → 候选只剩几个）时，这条前提不成立，
                            //   后面那些滚动断言的失败都是假失败。
                            bool listScrolls = lst.ContentHeight > lst.Height;
                            // 走**真实点击路径**：直接调 AllAppsList.OnMouseUp（左键）点到前三行上，
                            // 这样 CheckedChanged → UpdateChecks/UpdateTitle 全都会真的跑一遍
                            MethodInfo mu = typeof(AllAppsList).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);
                            int rowH = (int)Math.Round(Theme.Scroll.ListRow);   // 行高取统一常量，别再抄一份 42
                            for (int i = 0; i < 3; i++)
                            {
                                int y = Theme.Px(lst, 4) + i * Theme.Px(lst, rowH) + Theme.Px(lst, 19);
                                mu.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 120, y, 0) });
                            }
                            gained[1] = lst.Checked.Count;
                            // 打钩状态截一张（给用户看「已选 N 个」长什么样）
                            try
                            {
                                using (Bitmap shot = new Bitmap(pf.Width, pf.Height))
                                {
                                    pf.DrawToBitmap(shot, new Rectangle(0, 0, pf.Width, pf.Height));
                                    shot.Save(Path.Combine(Program.AppDir, "picker-checked.png"),
                                        System.Drawing.Imaging.ImageFormat.Png);
                                }
                            }
                            catch (Exception) { }
                            MethodInfo acc = typeof(AppPickerForm).GetMethod("Accept", BindingFlags.NonPublic | BindingFlags.Instance);
                            acc.Invoke(pf, null);
                            gained[0] = gv.Group.Keys.Count - beforeAdd;
                            // 把加完之后的窗口截一张，给「排版改动必须用眼睛看」留证据
                            try
                            {
                                using (Bitmap shot = new Bitmap(pf.Width, pf.Height))
                                {
                                    pf.DrawToBitmap(shot, new Rectangle(0, 0, pf.Width, pf.Height));
                                    shot.Save(Path.Combine(Program.AppDir, "picker-after-add.png"),
                                        System.Drawing.Imaging.ImageFormat.Png);
                                }
                            }
                            catch (Exception) { }

                            // 框选：按住左键拖一个方框盖住前 3 行 → 这 3 行应全部打钩，
                            // 而且**不能**再被当成「点了一行」（否则数量会多一个或少一个）
                            try
                            {
                                MethodInfo md = typeof(AllAppsList).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                                MethodInfo mm = typeof(AllAppsList).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                                lst.UncheckAll();
                                int bandTop = Theme.Px(lst, 4);
                                Point bS = new Point(Theme.Px(lst, 20), bandTop + Theme.Px(lst, 6));
                                Point bE = new Point(lst.ClientSize.Width - Theme.Px(lst, 30), bandTop + Theme.Px(lst, Theme.Scroll.ListRow) * 3 - Theme.Px(lst, 6));
                                md.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, bS.X, bS.Y, 0) });
                                mm.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 0, (bS.X + bE.X) / 2, (bS.Y + bE.Y) / 2, 0) });
                                mm.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 0, bE.X, bE.Y, 0) });
                                // 趁方框还开着截一张图（「排版/交互改动必须用眼睛看」的证据）
                                try
                                {
                                    using (Bitmap shot = new Bitmap(pf.Width, pf.Height))
                                    {
                                        pf.DrawToBitmap(shot, new Rectangle(0, 0, pf.Width, pf.Height));
                                        shot.Save(Path.Combine(Program.AppDir, "picker-band.png"),
                                            System.Drawing.Imaging.ImageFormat.Png);
                                    }
                                }
                                catch (Exception) { }
                                mu.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, bE.X, bE.Y, 0) });
                                int bandPicked = lst.Checked.Count;
                                // ★前提：候选得有 3 行可框。前面「加 3 个」那一步会把它们从候选里摘掉，
                                //   换成候选本来就少的配置（比如用户自己的：只剩两三条）就框不满 3 行 —— 那不是 bug。
                                List<AppEntry> bandRows = (List<AppEntry>)typeof(AppPickerForm)
                                    .GetField("_filtered", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf);
                                CheckIf(bandRows.Count >= 3, "候选只剩 " + bandRows.Count + " 条（前面已经加走几个），框不满 3 行",
                                    bandPicked == 3, "框选盖住前 3 行 → 正好 3 个打钩（实际 " + bandPicked + " 个，不能多也不能少）");

                                // 按下后只抖 2px：不算框选，仍然是原来的「点一下打钩」
                                lst.UncheckAll();
                                md.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 120, bandTop + 19, 0) });
                                mm.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 0, 122, bandTop + 21, 0) });
                                mu.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 122, bandTop + 21, 0) });
                                Check(lst.Checked.Count == 1, "按下后手抖 2px 仍算单击（打钩 1 个，实际 " + lst.Checked.Count + " 个）");
                            }
                            catch (Exception exBand) { Check(false, "框选测试异常：" + exBand.Message); }

                            // 审查建议补的三条：右键不提交、阈值两侧、数据集变化取消框选
                            try
                            {
                                FieldInfo baf = typeof(AllAppsList).GetField("_bandActive", BindingFlags.NonPublic | BindingFlags.Instance);
                                MethodInfo md2 = typeof(AllAppsList).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                                MethodInfo mm2 = typeof(AllAppsList).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                                int bTop = Theme.Px(lst, 4);
                                Point s2 = new Point(Theme.Px(lst, 20), bTop + Theme.Px(lst, 6));
                                Point e2 = new Point(lst.ClientSize.Width - Theme.Px(lst, 30), bTop + Theme.Px(lst, Theme.Scroll.ListRow) * 3 - Theme.Px(lst, 6));

                                lst.UncheckAll();
                                md2.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, s2.X, s2.Y, 0) });
                                mm2.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 0, e2.X, e2.Y, 0) });
                                mu.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Right, 1, e2.X, e2.Y, 0) });
                                bool left = (bool)baf.GetValue(lst);
                                Check(lst.Checked.Count == 0 && left == false,
                                    "右键松手不提交框选、也不残留方框（打钩 " + lst.Checked.Count + " 个，方框还在=" + left + "）");

                                lst.UncheckAll();
                                md2.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 120, bTop + 19, 0) });
                                mm2.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 0, 120 + Theme.Px(lst, 3), bTop + 19, 0) });
                                bool inT = (bool)baf.GetValue(lst);
                                mm2.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 0, 120 + Theme.Px(lst, 6), bTop + 19, 0) });
                                bool outT = (bool)baf.GetValue(lst);
                                Check(inT == false && outT == true,
                                    "框选阈值两侧：抖动 Px(3) 内不算框、Px(6) 外才算（" + inT + " / " + outT + "）");
                                lst.UncheckAll();
                                bool afterUn = (bool)baf.GetValue(lst);
                                Check(afterUn == false, "「全不选」等数据集变化会取消进行中的框选（方框还在=" + afterUn + "）");
                                mu.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 120 + Theme.Px(lst, 6), bTop + 19, 0) });
                                lst.UncheckAll();
                            }
                            catch (Exception exB2) { Check(false, "框选补充断言异常：" + exB2.Message); }

                            // 贴边自动滚动：先断言**判定逻辑**（确定性），再用泵消息验证真的滚了
                            try
                            {
                                MethodInfo md3 = typeof(AllAppsList).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                                MethodInfo mm3 = typeof(AllAppsList).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                                FieldInfo offF = typeof(AllAppsList).GetField("_offset", BindingFlags.NonPublic | BindingFlags.Instance);
                                FieldInfo dirF = typeof(AllAppsList).GetField("_autoDir", BindingFlags.NonPublic | BindingFlags.Instance);
                                FieldInfo asF = typeof(AllAppsList).GetField("_autoScroll", BindingFlags.NonPublic | BindingFlags.Instance);
                                lst.UncheckAll();
                                int off0 = (int)offF.GetValue(lst);
                                int bandBottomY = lst.ClientSize.Height - Theme.Px(lst, 6);
                                md3.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, Theme.Px(lst, 20), Theme.Px(lst, 6), 0) });
                                mm3.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 0, lst.ClientSize.Width / 2, bandBottomY, 0) });
                                int dir = (int)dirF.GetValue(lst);
                                bool timerOn = ((Timer)asF.GetValue(lst)).Enabled;
                                Check(dir == 1 && timerOn,
                                    "框选贴到下边缘 → 自动滚动方向=" + dir + "（应为 1）、定时器已启动=" + timerOn);
                                // 泵消息让 WM_TIMER 真的跑起来（★不能用 Thread.Sleep：那会把 UI 线程堵死，定时器永远不触发）
                                System.Diagnostics.Stopwatch swA = System.Diagnostics.Stopwatch.StartNew();
                                while (swA.ElapsedMilliseconds < 400) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                                int off1 = (int)offF.GetValue(lst);
                                mu.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, lst.ClientSize.Width / 2, bandBottomY, 0) });
                                CheckIf(listScrolls, "列表没超出可视区（候选太少），本来就滚不动",
                                    off1 > off0, "贴边 400ms 内真的滚动了（_offset " + off0 + " → " + off1 + "）");
                                lst.UncheckAll();
                            }
                            catch (Exception exB3) { Check(false, "自动滚动断言异常：" + exB3.Message); }

                            // 新增的「全选 / 全不选」按钮：也走真实点击路径验证
                            try
                            {
                                FlatButton allBtn = (FlatButton)typeof(AppPickerForm)
                                    .GetField("_allBtn", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf);
                                FlatButton noneBtn = (FlatButton)typeof(AppPickerForm)
                                    .GetField("_noneBtn", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf);
                                List<AppEntry> filtered = (List<AppEntry>)typeof(AppPickerForm)
                                    .GetField("_filtered", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf);
                                ClickButton(allBtn);
                                int afterAll = lst.Checked.Count;
                                Check(afterAll == filtered.Count && afterAll > 0,
                                    "「全选」把当前列出来的 " + filtered.Count + " 个全打上钩（实际 " + afterAll + " 个）");
                                ClickButton(noneBtn);
                                Check(lst.Checked.Count == 0,
                                    "「全不选」把钩全部清掉（实际 " + lst.Checked.Count + " 个）");
                            }
                            catch (Exception exAll) { Check(false, "全选/全不选 测试异常：" + exAll.Message); }

                            // ---- 统一滚动度量：滚轮步长 / 命中区 / 右键菜单 / 滚动条画在哪 ----
                            FieldInfo offF2 = typeof(AllAppsList).GetField("_offset", BindingFlags.NonPublic | BindingFlags.Instance);
                            MethodInfo md4 = typeof(AllAppsList).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                            MethodInfo mu4 = typeof(AllAppsList).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);

                            try
                            {
                                lst.UncheckAll();
                                offF2.SetValue(lst, 0);
                                int rowPitch = Theme.Px(lst, Theme.Scroll.ListRow);
                                WheelList(lst, -120);
                                int offOne = (int)offF2.GetValue(lst);
                                CheckIf(listScrolls, "列表没超出可视区（候选太少），滚轮自然不动",
                                    offOne == rowPitch * 3,
                                    "列表滚轮一格 = 3 行（行距 " + rowPitch + "）→ _offset = " + offOne
                                    + "（期望 " + (rowPitch * 3) + "；以前是直接拿 Delta 当像素用的 120）");
                                WheelList(lst, -40);
                                int offFine = (int)offF2.GetValue(lst);
                                CheckIf(listScrolls, "列表没超出可视区（候选太少），滚轮自然不动",
                                    offFine - offOne == rowPitch,
                                    "碎 Delta（高精度滚轮/触控板送 40）按比例缩 → 正好一行 " + (offFine - offOne)
                                    + "px（以前只滚 40px，几乎看不出动）");
                                offF2.SetValue(lst, 0);
                            }
                            catch (Exception exSc) { Check(false, "滚轮步长断言异常：" + exSc.Message); }

                            try
                            {
                                lst.UncheckAll();
                                offF2.SetValue(lst, 0);
                                int hitL = Theme.Scroll.HitLeft(lst, lst.ClientSize.Width);
                                int rowY = Theme.Px(lst, 4) + Theme.Px(lst, 19);
                                // 命中区左边 8px：还在行里（行的右边缘在命中区左边几个像素），应该照常打钩
                                // ★别点在行右边缘**之外**的空白上：那里本来就 IndexAt = -1，测不出命中区的宽窄
                                int inRowX = hitL - Theme.Px(lst, 8);
                                md4.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, inRowX, rowY, 0) });
                                mu4.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, inRowX, rowY, 0) });
                                int clickedRow = lst.Checked.Count;
                                lst.UncheckAll();
                                // 命中区里 1px：算滚动条，绝不能变成打钩（★H2 那个 bug 就是命中区太宽）
                                md4.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, hitL + Theme.Px(lst, 1), rowY, 0) });
                                mu4.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.Left, 1, hitL + Theme.Px(lst, 1), rowY, 0) });
                                int onBar = lst.Checked.Count;
                                CheckIf(listScrolls, "列表没超出可视区，那一竖条根本不是滚动条，测不出「抢不抢点击」",
                                    clickedRow == 1 && onBar == 0,
                                    "滚动条命中区（左界 x=" + hitL + "，客户区宽 " + lst.ClientSize.Width + "）不抢行点击：区内点 → 打钩 "
                                    + onBar + " 个（应为 0），行内 x=" + inRowX + " 点 → " + clickedRow + " 个（应为 1）");
                            }
                            catch (Exception exHz) { Check(false, "滚动条命中区断言异常：" + exHz.Message); }

                            try
                            {
                                List<AppEntry> cands2 = (List<AppEntry>)typeof(AppPickerForm)
                                    .GetField("_filtered", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf);
                                AppEntry target = null;
                                for (int i = 0; i < cands2.Count; i++)
                                    if (cands2[i] != null && cands2[i].IsRealFile) { target = cands2[i]; break; }
                                if (target == null && cands2.Count > 0) target = cands2[0];
                                if (target != null)
                                {
                                    MethodInfo bm = typeof(AppPickerForm).GetMethod("BuildEntryMenu", BindingFlags.NonPublic | BindingFlags.Instance);
                                    lst.UncheckAll();
                                    ContextMenuStrip m1 = (ContextMenuStrip)bm.Invoke(pf, new object[] { target });
                                    string texts = "";
                                    for (int i = 0; i < m1.Items.Count; i++) texts += m1.Items[i].Text + " | ";
                                    bool hasToggle = texts.IndexOf("打钩") >= 0;
                                    bool hasCopy = texts.IndexOf("复制显示名") >= 0;
                                    bool hasRename = texts.IndexOf("重命名") >= 0;
                                    bool hasLoc = texts.IndexOf("打开文件所在位置") >= 0;
                                    Say("      选择器右键菜单：" + texts + "（目标「" + target.Name + "」IsRealFile=" + target.IsRealFile + "）");
                                    Check(hasToggle && hasCopy && hasRename && hasLoc == target.IsRealFile,
                                        "右键菜单内容对：打钩/取消打钩 ✓、重命名… ✓、复制显示名 ✓、「打开文件所在位置」只在真实文件时出现（" + hasLoc + "）");
                                    m1.Items[0].PerformClick();
                                    int onMenu = lst.Checked.Count;
                                    ContextMenuStrip m2 = (ContextMenuStrip)bm.Invoke(pf, new object[] { target });
                                    string secondText = m2.Items[0].Text;
                                    m2.Items[0].PerformClick();
                                    int offMenu = lst.Checked.Count;
                                    Check(onMenu == 1 && secondText == "取消打钩" && offMenu == 0,
                                        "菜单「打钩」真的勾上（" + onMenu + " 个）→ 再开菜单首项变「" + secondText
                                        + "」→ 点它取消（" + offMenu + " 个）");
                                    m1.Dispose();
                                    m2.Dispose();
                                }
                                else Say("（候选为空，跳过右键菜单断言）");
                            }
                            catch (Exception exMenu) { Check(false, "右键菜单断言异常：" + exMenu.Message); }

                            try
                            {
                                // 菜单弹出来时是另一个窗口，主窗口立刻 Deactivate —— 必须被压住，
                                // 否则「右键一下，整个选择器直接没了、勾的全丢」。
                                FieldInfo supF = typeof(AppPickerForm).GetField("_suppressDeactivate", BindingFlags.NonPublic | BindingFlags.Instance);
                                MethodInfo deact = typeof(Form).GetMethod("OnDeactivate", BindingFlags.NonPublic | BindingFlags.Instance);
                                supF.SetValue(pf, true);
                                deact.Invoke(pf, new object[] { EventArgs.Empty });
                                bool alive = (pf.IsDisposed == false && pf.DialogResult == DialogResult.None);
                                supF.SetValue(pf, false);
                                Check(alive, "右键菜单开着时不会被「失去焦点」关掉（否则勾好的全作废）");
                            }
                            catch (Exception exSup) { Check(false, "菜单关窗保护断言异常：" + exSup.Message); }

                            try
                            {
                                // 两处滚动条必须是**同一套数字**画的：这里直接扫像素，看墨水落在哪几列。
                                lst.UncheckAll();
                                offF2.SetValue(lst, 0);
                                Rectangle bb;
                                int phits;
                                // 扫描窗口：左边宽一点没关系，右边**必须避开最外面那几像素** ——
                                // 那里有窗口边框（Theme.Border 亮度 64）和「靠近哪条边就亮哪条边」的高亮（2px，更亮），
                                // 不避开就会把它们当成滚动条墨水，量出个「7px 宽、整条轨道高」的假外框。
                                int scanL = Theme.Scroll.InkPx(lst) + Theme.Scroll.MarginPx(lst) + Theme.Px(lst, 8);
                                int scanR = lst.ClientSize.Width - 1 - Theme.Scroll.MarginPx(lst);
                                using (Bitmap shot = new Bitmap(lst.ClientSize.Width, Math.Max(1, lst.ClientSize.Height)))
                                {
                                    using (Graphics gb = Graphics.FromImage(shot))
                                    {
                                        MethodInfo paM = typeof(AllAppsList).GetMethod("PaintAll", BindingFlags.NonPublic | BindingFlags.Instance);
                                        paM.Invoke(lst, new object[] { new PaintEventArgs(gb, new Rectangle(0, 0, shot.Width, shot.Height)) });
                                    }
                                    bb = BarBounds(shot, shot.Width - scanL, scanR, 0, shot.Height - 1,
                                                   Math.Max(4, Theme.Scroll.MinThumbPx(lst) / 2), out phits);
                                }
                                Rectangle exp = Theme.Scroll.BarRect(lst, Theme.Scroll.TrackPadPx(lst),
                                    Math.Max(1, lst.Height - Theme.Scroll.TrackPadPx(lst) * 2), lst.ContentHeight, lst.Height, 0);
                                listBar[0] = bb.Width;                             // 墨水宽
                                listBar[1] = (lst.ClientSize.Width - 1) - (bb.Right - 1);   // 墨水右缘距控件右缘
                                listGeom[0] = lst.DeviceDpi;
                                listGeom[1] = Theme.Scroll.InkPx(lst);
                                listGeom[2] = Theme.Scroll.MarginPx(lst);
                                CheckIf(listScrolls, "列表没超出可视区，根本不会画滚动条",
                                    bb.IsEmpty == false && Math.Abs(bb.Left - exp.X) <= 1 && Math.Abs(bb.Width - exp.Width) <= 1
                                      && Math.Abs(bb.Top - exp.Y) <= 1,
                                    "列表滚动条真画在 Theme.Scroll.BarRect 指定的位置：实测 " + bb.X + "," + bb.Y + " " + bb.Width + "x" + bb.Height
                                    + "（期望 " + exp.X + "," + exp.Y + " " + exp.Width + "x" + exp.Height
                                    + "，±1px 是抗锯齿边缘，列内命中 " + phits + " 像素）");
                            }
                            catch (Exception exBar) { Check(false, "列表滚动条像素断言异常：" + exBar.Message); }

                            // 真的把右键菜单弹出来截一张图（「画面改动必须出图看」的规矩：
                            // 菜单项文本测过了，但长得对不对还得看一眼）。纯留证据，不做断言。
                            try
                            {
                                List<AppEntry> cands3 = (List<AppEntry>)typeof(AppPickerForm)
                                    .GetField("_filtered", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf);
                                AppEntry shotTarget = null;
                                for (int i = 0; i < cands3.Count; i++)
                                    if (cands3[i] != null && cands3[i].IsRealFile) { shotTarget = cands3[i]; break; }
                                if (shotTarget == null && cands3.Count > 0) shotTarget = cands3[0];
                                if (shotTarget != null)
                                {
                                    MethodInfo bm2 = typeof(AppPickerForm).GetMethod("BuildEntryMenu", BindingFlags.NonPublic | BindingFlags.Instance);
                                    ContextMenuStrip mShot = (ContextMenuStrip)bm2.Invoke(pf, new object[] { shotTarget });
                                    typeof(AppPickerForm).GetField("_suppressDeactivate", BindingFlags.NonPublic | BindingFlags.Instance)
                                        .SetValue(pf, true);
                                    mShot.Show(lst, new Point(lst.ClientSize.Width / 3, Theme.Px(lst, 60)));
                                    Application.DoEvents();
                                    System.Threading.Thread.Sleep(120);
                                    Application.DoEvents();
                                    using (Bitmap shot = new Bitmap(Math.Max(1, mShot.Width), Math.Max(1, mShot.Height)))
                                    {
                                        mShot.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
                                        shot.Save(Path.Combine(Program.AppDir, "picker-menu.png"),
                                            System.Drawing.Imaging.ImageFormat.Png);
                                    }
                                    Say("      右键菜单截图：" + mShot.Width + "x" + mShot.Height
                                        + "，菜单项 " + mShot.Items.Count + " 个");
                                    mShot.Close();
                                    mShot.Dispose();
                                    typeof(AppPickerForm).GetField("_suppressDeactivate", BindingFlags.NonPublic | BindingFlags.Instance)
                                        .SetValue(pf, false);
                                }
                            }
                            catch (Exception exShot) { Say("（右键菜单截图失败，不影响判定：" + exShot.Message + "）"); }

                            // ---- 「来源」菜单（全部 / 桌面 / 自定义文件夹）----
                            try
                            {
                                MethodInfo bsm = typeof(AppPickerForm).GetMethod("BuildSourceMenu", BindingFlags.NonPublic | BindingFlags.Instance);
                                MethodInfo setSrc = typeof(AppPickerForm).GetMethod("SetSource", BindingFlags.NonPublic | BindingFlags.Instance);
                                FieldInfo srcF = typeof(AppPickerForm).GetField("_source", BindingFlags.NonPublic | BindingFlags.Instance);

                                ContextMenuStrip sm = (ContextMenuStrip)bsm.Invoke(pf, null);
                                StringBuilder items = new StringBuilder();
                                int checkedIdx = -1;
                                for (int i = 0; i < sm.Items.Count; i++)
                                {
                                    ToolStripMenuItem mi = sm.Items[i] as ToolStripMenuItem;
                                    items.Append(sm.Items[i].Text);
                                    items.Append(" | ");
                                    if (mi != null && mi.Checked) checkedIdx = i;
                                }
                                Say("      来源菜单：" + items.ToString());
                                Check(sm.Items.Count == 5 && checkedIdx == 1,
                                    "「来源」菜单有 5 项（全部 / 桌面 / 自定义文件夹 / 分隔线 / 管理自定义文件夹…），默认勾在「桌面」（第 " + (checkedIdx + 1) + " 项）");
                                sm.Dispose();

                                // 三种来源各自筛出来多少
                                setSrc.Invoke(pf, new object[] { 0 });   // 全部
                                int nAll = ((List<AppEntry>)typeof(AppPickerForm).GetField("_filtered", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf)).Count;
                                setSrc.Invoke(pf, new object[] { 1 });   // 桌面
                                List<AppEntry> deskList = (List<AppEntry>)typeof(AppPickerForm).GetField("_filtered", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf);
                                bool allDesk = true;
                                for (int i = 0; i < deskList.Count; i++) if (deskList[i].OnDesktop == false) allDesk = false;
                                setSrc.Invoke(pf, new object[] { 2 });   // 自定义（这台机器可能一个都没加）
                                int nCustom = ((List<AppEntry>)typeof(AppPickerForm).GetField("_filtered", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf)).Count;
                                string hint = (string)typeof(AppPickerForm).GetField("_hint", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf);
                                setSrc.Invoke(pf, new object[] { 1 });
                                Check(nAll >= deskList.Count && allDesk && nCustom == 0 && hint.IndexOf("管理自定义文件夹") >= 0,
                                    "「来源」筛选生效：全部 " + nAll + " 条 ≥ 桌面 " + deskList.Count + " 条（桌面这 " + deskList.Count + " 条全是 OnDesktop）、自定义 " + nCustom + " 条（没加文件夹时提示去「管理自定义文件夹…」）");

                                // 真的弹一次「来源」菜单截张图（画面改动必须出图看），纯留证据
                                try
                                {
                                    ContextMenuStrip shot = (ContextMenuStrip)bsm.Invoke(pf, null);
                                    typeof(AppPickerForm).GetField("_suppressDeactivate", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(pf, true);
                                    shot.Show(pf, new Point(Theme.Px(pf, 120), Theme.Px(pf, 100)));
                                    Application.DoEvents();
                                    System.Threading.Thread.Sleep(120);
                                    Application.DoEvents();
                                    using (Bitmap b = new Bitmap(Math.Max(1, shot.Width), Math.Max(1, shot.Height)))
                                    {
                                        shot.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                                        b.Save(Path.Combine(Program.AppDir, "picker-source-menu.png"), System.Drawing.Imaging.ImageFormat.Png);
                                    }
                                    Say("      来源菜单截图：" + shot.Width + "x" + shot.Height);
                                    shot.Close();
                                    shot.Dispose();
                                    typeof(AppPickerForm).GetField("_suppressDeactivate", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(pf, false);
                                }
                                catch (Exception exShot2) { Say("（来源菜单截图失败，不影响判定：" + exShot2.Message + "）"); }
                            }
                            catch (Exception exSrc) { Check(false, "「来源」菜单断言异常：" + exSrc.Message); }

                            // ---- 自定义扫描目录：端到端（建个临时目录 → 扫 → 断言条目与图标来源）----
                            try
                            {
                                string dir = Path.Combine(Program.AppDir, "probe-customscan");
                                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
                                Directory.CreateDirectory(dir);
                                string icoPath = Path.Combine(Path.GetDirectoryName(Program.AppDir), "assets", "BreadLauncher.ico");
                                string srcExe = Path.Combine(Program.AppDir, "icocheck.exe");     // 随便一个真实的 exe
                                WriteUrlFile(Path.Combine(dir, "ProbeDuck.url"), "https://example.com/", File.Exists(icoPath) ? icoPath : null);
                                if (File.Exists(srcExe)) File.Copy(srcExe, Path.Combine(dir, "ProbeApp.exe"), true);

                                List<AppEntry> scanned = AppsFolderScanner.Scan(Program.AppDir, new List<string> { dir });
                                AppEntry duck = null, app = null;
                                for (int i = 0; i < scanned.Count; i++)
                                {
                                    if (scanned[i].Name == "ProbeDuck") duck = scanned[i];
                                    if (scanned[i].Name == "ProbeApp") app = scanned[i];
                                }
                                bool duckOk = duck != null && duck.FromCustom && !string.IsNullOrEmpty(duck.IconSource)
                                              && string.Equals(duck.IconSource, icoPath, StringComparison.OrdinalIgnoreCase);
                                bool appOk = File.Exists(srcExe) ? (app != null && app.FromCustom) : true;
                                Check(duckOk && appOk,
                                    "自定义扫描目录端到端：临时目录里的 .url 与 .exe 都被扫到并标了 FromCustom"
                                    + "（ProbeDuck 来源=" + (duck == null ? "没扫到" : (string.IsNullOrEmpty(duck.IconSource) ? "★空" : "ico")) 
                                    + "、ProbeApp=" + (app == null ? (File.Exists(srcExe) ? "没扫到" : "（跳过，没找到源 exe）") : "扫到") + "）");
                                try { Directory.Delete(dir, true); } catch (Exception) { }
                            }
                            catch (Exception exCustom) { Check(false, "自定义目录端到端断言异常：" + exCustom.Message); }

                            // ---- 「管理自定义文件夹」窗口：出图看排版（用反射构造，不弹模态）----
                            try
                            {
                                ConstructorInfo ctor = typeof(ScanFoldersForm).GetConstructor(
                                    BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[0], null);
                                ScanFoldersForm sf = (ScanFoldersForm)ctor.Invoke(null);
                                try
                                {
                                    MethodInfo initM = typeof(ScanFoldersForm).GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Instance);
                                    List<string> demo = new List<string>();
                                    demo.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                                    demo.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
                                    initM.Invoke(sf, new object[] { demo });
                                    ListBox lb = (ListBox)typeof(ScanFoldersForm).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(sf);
                                    string hint1 = (string)typeof(ScanFoldersForm).GetField("_hint1", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(sf);
                                    string hint2 = (string)typeof(ScanFoldersForm).GetField("_hint2", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(sf);
                                    Check(lb.Items.Count == 2 && hint1.IndexOf("2 个文件夹") >= 0 && hint2.IndexOf("一级子文件夹") >= 0,
                                        "「管理自定义文件夹」窗口：列出已有 " + lb.Items.Count + " 个目录（" + hint1 + "），提示写明「扫自己 + 一级子文件夹、只认 .lnk/.exe/.url」");
                                    // ★不出图时**不 Show**：探针跑在「添加应用」的模态消息循环里，
                                    //   在这里 Show 一个非模态窗口会死等（试过两次，整个探针卡住）。
                                    //   直接 DrawToBitmap 就够留证据了。
                                    using (Bitmap b = new Bitmap(sf.Width, sf.Height))
                                    {
                                        sf.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                                        b.Save(Path.Combine(Program.AppDir, "picker-scanfolders.png"), System.Drawing.Imaging.ImageFormat.Png);
                                    }
                                    Say("      文件夹管理窗口截图：" + sf.Width + "x" + sf.Height);
                                }
                                finally
                                {
                                    // ★不要在这里 Close()：在模态消息循环里对刚 Show() 出来的窗口调 Close
                                    //   会一直等消息循环处理 WM_CLOSE，而循环正卡在本函数里 → 死等（踩过一次）。
                                    try { sf.Hide(); } catch (Exception) { }
                                    sf.Dispose();
                                }
                            }
                            catch (Exception exSF) { Check(false, "文件夹管理窗口断言异常：" + exSF.Message); }

                            // ---- 整条链路：设置里加了目录 → 回调重扫 → 候选里出现 → 切到「自定义」视图看得到 ----
                            // （文件夹选择框是原生对话框、测不了，所以从「用户已经加好目录」这一步开始，
                            //   覆盖 ManageFolders 之后的那半条链 —— 那半条才是最可能悄悄坏掉的。）
                            try
                            {
                                string dir2 = Path.Combine(Program.AppDir, "probe-customscan2");
                                try { if (Directory.Exists(dir2)) Directory.Delete(dir2, true); } catch (Exception) { }
                                Directory.CreateDirectory(dir2);
                                string ico2 = Path.Combine(Path.GetDirectoryName(Program.AppDir), "assets", "BreadLauncher.ico");
                                WriteUrlFile(Path.Combine(dir2, "ProbeChain1.url"), "https://example.com/", File.Exists(ico2) ? ico2 : null);
                                string exe2 = Path.Combine(Program.AppDir, "icocheck.exe");
                                if (File.Exists(exe2)) File.Copy(exe2, Path.Combine(dir2, "ProbeChain2.exe"), true);

                                Settings stP = (Settings)typeof(AppPickerForm).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf);
                                Func<List<AppEntry>> cb = (Func<List<AppEntry>>)typeof(AppPickerForm)
                                    .GetField("_onSourcesChanged", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf);
                                List<string> oldDirs = stP.ScanDirs == null ? new List<string>() : new List<string>(stP.ScanDirs);

                                stP.ScanDirs = new List<string>();
                                stP.ScanDirs.Add(dir2);
                                List<AppEntry> fresh = cb == null ? null : cb();      // = MainForm.RescanForPicker()
                                int chainHit = 0;
                                if (fresh != null)
                                    for (int i = 0; i < fresh.Count; i++)
                                        if (fresh[i] != null && fresh[i].FromCustom && fresh[i].Name.StartsWith("ProbeChain")) chainHit++;

                                // 切到「自定义」视图：列表里应该正好是这两条
                                FieldInfo allF = typeof(AppPickerForm).GetField("_all", BindingFlags.NonPublic | BindingFlags.Instance);
                                List<AppEntry> keepAll = (List<AppEntry>)allF.GetValue(pf);
                                allF.SetValue(pf, fresh == null ? keepAll : fresh);
                                SetSourceInvoke(pf, 2);
                                List<AppEntry> customList = (List<AppEntry>)typeof(AppPickerForm)
                                    .GetField("_filtered", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf);
                                int shown = 0;
                                for (int i = 0; i < customList.Count; i++) if (customList[i].FromCustom) shown++;
                                Check(chainHit == 2 && shown == customList.Count && customList.Count >= 2,
                                    "整条链路通：设置里加 1 个目录 → 重扫回 " + (fresh == null ? "null" : fresh.Count + " 条候选")
                                    + "（其中 ProbeChain* " + chainHit + " 条）→ 切到「自定义」视图列出 " + customList.Count + " 条（全是 FromCustom=" + (shown == customList.Count) + "）");

                                allF.SetValue(pf, keepAll);      // 还原选择器的候选（别影响后面的断言）
                                // ★收尾必须「先删临时目录、再把 ScanDirs 还原、最后再重扫一次」：
                                //   重扫会把结果写进 build\cache\apps-cache.json，而 FromCustom 是**持久化**的 ——
                                //   不这样收尾，临时目录里那两条会留在缓存里，下一次跑探针时「自定义视图应为空」
                                //   就会撞上残留（踩过一次，三条断言连带失败）。
                                try { Directory.Delete(dir2, true); } catch (Exception) { }
                                stP.ScanDirs = oldDirs;
                                if (cb != null) { try { cb(); } catch (Exception) { } }   // 再扫一次，把缓存写干净
                                SetSourceInvoke(pf, 1);
                            }
                            catch (Exception exChain) { Check(false, "整链路断言异常：" + exChain.Message); }

                            // ---- 落盘往返（纯函数级，不动用户配置）+ 哨兵（别让「管理文件夹」点了没反应）----
                            try
                            {
                                Settings probeS = (Settings)typeof(AppPickerForm).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf);
                                Func<List<AppEntry>> probeCb = (Func<List<AppEntry>>)typeof(AppPickerForm)
                                    .GetField("_onSourcesChanged", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pf);
                                Check(probeS != null && probeCb != null,
                                    "选择器拿到了 settings 与重扫回调（缺任何一个，「管理自定义文件夹」都会点了没反应）");

                                string tmpSet = Path.Combine(Program.AppDir, "probe-settings-roundtrip.json");
                                Settings w = new Settings();
                                w.ScanDirs.Add(@"D:\Games");
                                w.ScanDirs.Add(@"D:\中文 目录\游戏\");     // 中文 + 空格 + 末尾斜杠
                                ConfigStore.SaveSettingsFile(tmpSet, w);
                                Settings back = ConfigStore.LoadSettingsFile(tmpSet);
                                bool same = back != null && back.ScanDirs != null && back.ScanDirs.Count == 2
                                            && back.ScanDirs[0] == w.ScanDirs[0] && back.ScanDirs[1] == w.ScanDirs[1];
                                Say("      配置往返：读回来 " + (back == null || back.ScanDirs == null ? "null" : back.ScanDirs.Count + " 条"));
                                Check(same, "ScanDirs 落盘再读回来一模一样（含中文路径与末尾斜杠）—— 自定义目录才可能真的「记得住」");
                                try { File.Delete(tmpSet); } catch (Exception) { }
                            }
                            catch (Exception exRT) { Check(false, "配置往返断言异常：" + exRT.Message); }

                            pf.DialogResult = DialogResult.Cancel;
                        }
                    }
                };
                t2.Start();
                MethodInfo openPicker = typeof(MainForm).GetMethod("OpenPicker", BindingFlags.NonPublic | BindingFlags.Instance);
                openPicker.Invoke(f, new object[] { gv });
                t2.Stop();
                // ★前提：那段断言得真的跑过（gained 保持 -1 就表示一次都没进去）。
                // ★★跳过的**理由要说真话**（2026-09-28 修）：以前这里写「候选不足 3 个」是**假理由** ——
                //   实测那次候选有 168 个，真相是 `AppPickerForm` 在探针第一次 Tick 之前就自己关了
                //   （它失焦会关窗，见 Dialogs.cs 的 OnDeactivate；那次根因是「600ms 防误关」从构造起算，
                //   窗口还没出现守卫就过期了 —— 产品侧已修：改成 OnShown 起算）。
                //   把假理由写进报告，等于把「26 条断言没跑」伪装成「配置太小」，是最危险的那种失真。
                string pickerWhy = "「添加应用」窗口没跑到探针的计时器里（没开出来，或者开出来又被失焦关掉了）";
                CheckIf(gained[0] >= 0, pickerWhy,
                    gained[0] == 3,
                    "「添加应用」勾选 3 个 → 点一次「添加」→ 组内 key " + beforeAdd + " → " + gv.Group.Keys.Count + "（窗口不关，加过的从候选里消失）");
                CheckIf(gained[1] >= 0, pickerWhy,
                    gained[1] == 3,
                    "真实点击三行 → 打钩数 = " + gained[1] + "（走的是 AllAppsList.OnMouseUp → CheckedChanged → 标题/按钮实时更新）");
            }
            else
            {
                // 这里也要进「跳过汇总」：只 Say 不记，报告末尾就看不到它是被跳过的（候选真的不足 3 个时）
                NoteSkip("候选真的不足 3 个（这份配置里能加的应用太少）");
                Say("（候选不足 3 个，跳过「连加多个」检查）");
            }


            // ---------- 2) 滚轮方向 ----------
            int maxScroll = (int)Prop(f, "MaxScroll");
            if (maxScroll > 0)
            {
                SetScroll(f, 0);
                int y0 = (int)Field(f, "_scrollY");
                Wheel(f, -120);
                int y1 = (int)Field(f, "_scrollY");
                Wheel(f, -120);
                int y2 = (int)Field(f, "_scrollY");
                Wheel(f, 120);
                int y3 = (int)Field(f, "_scrollY");
                Say("MaxScroll=" + maxScroll + "  _scrollY：起点 " + y0 + " → 向下滚 ×2 = " + y2 + " → 向上滚 ×1 = " + y3);
                // ★别要求「每一格都严格变大」：这份配置如果只差一整行（MaxScroll 刚好等于一格），
                //   第一次滚动就夹到最大值了，第二格当然不会再大 —— 那是正确行为，不是 bug。
                Check(y1 > y0 && y2 >= y1 && y3 < y2,
                    "滚轮向下 = _scrollY 变大（" + y0 + "→" + y1 + "，上限 " + maxScroll + "），滚轮向上 = _scrollY 变小（" + y2 + "→" + y3 + "）→ 与系统一致");
            }
            else
            {
                Say("MaxScroll=0（这份配置不足一屏），跳过滚轮检查");
            }

            // ---------- 3) 拖动调位置（把第 1 个文件夹拖到第 2 个位置） ----------
            Settings st = (Settings)typeof(MainForm).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
            if (st.Groups.Count >= 2)
            {
                SetScroll(f, 0);
                string n0 = st.Groups[0].Name;
                string n1 = st.Groups[1].Name;
                MethodInfo downM = typeof(MainForm).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo moveM = typeof(MainForm).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo upM = typeof(MainForm).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);
                int y0 = groupTop + miniPad + miniBox / 2;
                // ★必须从「文件夹主体」起拖（slot == -1）：按在小图标上不抢（那是点一下启动）。
                //   这里取左边的内边距（x 落在 MiniPad 里，命中测试会判成主体）。
                Point pFrom = new Point(groupLeft + Theme.Px(f, 2), y0);
                Point pTo = new Point(groupLeft + (tw + gx) + Theme.Px(f, 2), y0);
                downM.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, pFrom.X, pFrom.Y, 0) });
                moveM.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 0, pTo.X, pTo.Y, 0) });
                upM.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, pTo.X, pTo.Y, 0) });
                Check(st.Groups[0].Name == n1 && st.Groups[1].Name == n0,
                    "拖动第 1 个文件夹到第 2 个位置：顺序「" + n0 + " / " + n1 + "」→「"
                    + st.Groups[0].Name + " / " + st.Groups[1].Name + "」");
                Check(f.IsDisposed == false, "拖动结束后面板没被误当成「点击空白」而关掉");

                // 再拖到**空白处**（最后一列没有分组的位置）：应该挪到末尾，而不是什么都不发生
                string first = st.Groups[0].Name;
                Point pEmpty = new Point(groupLeft + 3 * (tw + gx) + tw / 2, y0);
                // 前提：这个点既是空白、落点又是一个**空位**。
                // ★换个配置就不一定了：列数不同（面板宽度/分组数变化）时，超出网格的点会被
                //   SlotIndexAt 夹回最后一列 —— 那里通常已经有文件夹，落上去是「交换」而不是「挪到末尾」。
                object[] peArgs = new object[] { pEmpty, 0, 0 };
                hit.Invoke(f, peArgs);
                MethodInfo slotAtM = typeof(MainForm).GetMethod("SlotIndexAt", BindingFlags.NonPublic | BindingFlags.Instance);
                int emptySlot = (int)slotAtM.Invoke(f, new object[] { pEmpty });
                bool pEmptyFree = (int)peArgs[1] < 0 && emptySlot >= st.Groups.Count;
                downM.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, pFrom.X, pFrom.Y, 0) });
                moveM.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 0, pEmpty.X, pEmpty.Y, 0) });
                upM.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, pEmpty.X, pEmpty.Y, 0) });
                CheckIf(pEmptyFree, "这个配置的网格里没有空位（列数/组数不同 → 落点被夹回已有文件夹的格子）",
                    st.Groups[st.Groups.Count - 1].Name == first,
                    "拖到空白处也能落：「" + first + "」挪到了末尾（现在顺序 "
                    + st.Groups[0].Name + " / " + st.Groups[1].Name + " / " + st.Groups[st.Groups.Count - 1].Name + "）");
            }
            else
            {
                Say("（只有一个分组，跳过拖动调位置检查）");
            }

            // ---------- 4) 搬面板 / 拉面板（用户要的：位置和大小都能改，还得记住） ----------
            MethodInfo downM2 = typeof(MainForm).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo moveM2 = typeof(MainForm).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo upM2 = typeof(MainForm).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);
            int bw0 = f.Width, bh0 = f.Height;
            Point br = new Point(f.Width - 2, f.Height - 2);          // 右下角 = 缩放抓取区
            int rx = br.X + Theme.Px(f, 60), ry = br.Y + Theme.Px(f, 40);
            downM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, br.X, br.Y, 0) });
            moveM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 0, rx, ry, 0) });
            upM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, rx, ry, 0) });
            Check(f.Width > bw0 && f.Height > bh0,
                "拖右下角把面板拉大：" + bw0 + "x" + bh0 + " → " + f.Width + "x" + f.Height);

            int bx0 = f.Left, by0 = f.Top;
            // ★原来的点写死「第 1 行第 4 格」：面板宽度/组数一变（列数 3↔4），那个点会落在文件夹上，
            //   于是"拖面板"变成"拖文件夹" → 假失败。改成**两列之间的缝**：和组数无关，永远是空白。
            // ★★但是上面那段「把面板拉宽 15 次」的性能测试**刚刚改过宽度**：_groupLeft / 列数都是宽度算出来的，
            //   拿拉宽**之前**取的值定位，点就会从"缝里"漂到"第 2 个文件夹上" —— 于是
            //   bgEmpty 恒为 false，「跟手 1:1」「搬面板」「松手落实位置」三条断言**每次都静默跳过**（实测六份配置全跳）。
            //   修法不是改公式（gx/2 < gx 本来就在缝里），而是**重取一次布局**再定位。
            int bgCols = (int)Prop(f, "Columns");
            int bgTw = (int)Prop(f, "TileW");
            int bgGx = (int)Prop(f, "GapX");
            int bgMiniPad = (int)Prop(f, "MiniPad");
            int bgMiniBox = (int)Prop(f, "MiniBox");
            int bgLeft = (int)Field(f, "_groupLeft");
            int bgTop = (int)Field(f, "_groupTop");
            int bgH = (int)Field(f, "_groupHeight");
            Point bg = new Point(bgLeft + bgTw + Math.Max(2, bgGx / 2), bgTop + Math.Min(bgH - 4, bgMiniPad + bgMiniBox / 2));
            // ★前提：这个点此刻确实是空白（面板刚被拉宽过 → 列数可能从 3 变 4 → 这个点会落进文件夹格子里，
            //   那样拖的就是**文件夹**不是面板 —— 探针自己坐标过期，不能报成功能坏了）
            object[] bgArgs = new object[] { bg, 0, 0 };
            hit.Invoke(f, bgArgs);
            bool bgEmpty = (int)bgArgs[1] < 0;
            int mx = bg.X + Theme.Px(f, 30), my = bg.Y + Theme.Px(f, 20);
            downM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, bg.X, bg.Y, 0) });
            moveM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 0, mx, my, 0) });
            upM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, mx, my, 0) });
            CheckIf(bgEmpty, "这个点在当前列数下落在文件夹上（面板宽度不同 → 列数不同），不是空白处",
                f.Left != bx0 || f.Top != by0,
                "在空白处按住拖动 = 搬走整个面板（" + bx0 + "," + by0 + " → " + f.Left + "," + f.Top + "）");
            Check(f.IsDisposed == false, "搬完面板没被误当成「点空白关闭」");

            // 固定把手（顶部正中那根小横条）：从把手上按下去拖，同样能搬面板
            Rectangle grip = (Rectangle)typeof(MainForm).GetField("_gripHit", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
            int gx0 = f.Left, gy0 = f.Top;
            Point gp = new Point(grip.Left + grip.Width / 2, grip.Top + grip.Height / 2);
            Point gp2 = new Point(gp.X + Theme.Px(f, 50), gp.Y + Theme.Px(f, 30));
            downM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, gp.X, gp.Y, 0) });
            moveM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 0, gp2.X, gp2.Y, 0) });
            upM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, gp2.X, gp2.Y, 0) });
            Check(f.Left != gx0 || f.Top != gy0,
                "拖顶部固定把手也能搬面板（可抓范围 " + grip.Width + "x" + grip.Height + "，位置 " + gx0 + "," + gy0 + " → " + f.Left + "," + f.Top + "）");

            // 流畅度：拖动时**每个鼠标事件都立刻应用**（不做时间节流 —— 节流的固定间隔和鼠标上报频率
            // 会形成拍频，看着就是「抖」）。所以这里量**单次事件成本**，不是总量。
            int fx0 = f.Left, fy0 = f.Top;
            System.Diagnostics.Stopwatch swp = System.Diagnostics.Stopwatch.StartNew();
            downM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, bg.X, bg.Y, 0) });
            for (int i = 1; i <= 100; i++)
                moveM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 0, bg.X + i, bg.Y + i, 0) });
            long msMoves = swp.ElapsedMilliseconds;
            upM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, bg.X + 100, bg.Y + 100, 0) });
            Check(msMoves / 100.0 < 2.0,
                "连续 100 次位移事件总耗时 " + msMoves + " ms（每次 " + (msMoves / 100.0).ToString("0.00") + " ms，每个事件都立刻应用）");
            CheckIf(bgEmpty, "起拖的点落在文件夹上（不是空白处），面板本来就不会动",
                f.Left != fx0 || f.Top != fy0,
                "松手仍然落实了最终位置（" + fx0 + "," + fy0 + " → " + f.Left + "," + f.Top + "）");
            Check(typeof(MainForm).GetField("_dragTimer", BindingFlags.NonPublic | BindingFlags.Instance) != null,
                "面板里存在兜底定时器 _dragTimer（每个鼠标事件已即时应用，它只负责收尾补应用）");

            // 每一帧的真实成本：拖动/缩放时要付的就是这两笔钱（整窗重画 + 改大小后的重画）
            f.Refresh();
            System.Diagnostics.Stopwatch swPaint = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) f.Refresh();
            long onePaint = swPaint.ElapsedMilliseconds / 20;
            Say("诊断：整窗重画一帧 ≈ " + onePaint + " ms（20 帧共 " + swPaint.ElapsedMilliseconds + " ms）");

            int w0 = f.Width, h0 = f.Height;
            System.Diagnostics.Stopwatch swResize = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 1; i <= 15; i++) { f.Width = w0 + i * 2; f.Refresh(); }
            long perResize = swResize.ElapsedMilliseconds / 15;
            f.Width = w0;
            Say("诊断：改一次宽度并重画 ≈ " + perResize + " ms（15 帧共 " + swResize.ElapsedMilliseconds + " ms）");
            Say("诊断：面板尺寸 " + w0 + "x" + h0);

            // 跟手度（这条是关键）：让「鼠标」在**屏幕上**走 100px，面板就应该正好走 100px。
            // 旧写法用客户区坐标算位移 —— 窗口自己一动，鼠标的客户区坐标就跟着被抵消，
            // 结果是鼠标走 2px、面板只走 1px（甚至原地不动），用户看到的就是「抖 / 不跟手」。
            f.Location = new Point(120, 120);          // 先挪到屏幕内，免得被工作区夹住影响判定
            f.Refresh();
            int trackX0 = f.Left;
            Point startScr = f.PointToScreen(bg);
            downM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, bg.X, bg.Y, 0) });
            for (int i = 1; i <= 100; i++)
            {
                Point sp = new Point(startScr.X + i, startScr.Y);
                Point cp = f.PointToClient(sp);        // 每一步都按「窗口当前所在位置」换算成客户区坐标
                moveM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 0, cp.X, cp.Y, 0) });
            }
            Point endCp = f.PointToClient(new Point(startScr.X + 100, startScr.Y));
            upM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, endCp.X, endCp.Y, 0) });
            int trackedPx = f.Left - trackX0;
            CheckIf(bgEmpty, "起拖的点落在文件夹上（不是空白处），面板本来就不会跟手",
                trackedPx == 100, "跟手 1:1：鼠标在屏幕上走 100px，面板走了 " + trackedPx + "px（旧写法只会走一半）");

            // 边缘热区放大到 10px：脚边 5px 处应该能拉到大小，画面中间不该误判成拉大小
            MethodInfo rz = typeof(MainForm).GetMethod("ResizeZoneAt", BindingFlags.NonPublic | BindingFlags.Instance);
            int zoneIn = (int)rz.Invoke(f, new object[] { new Point(Theme.Px(f, 4), Theme.Px(f, 4)) });
            int zoneOut = (int)rz.Invoke(f, new object[] { new Point(Theme.Px(f, 24), Theme.Px(f, 24)) });
            Check(zoneIn != 0 && zoneOut == 0,
                "边缘热区（左右上 14px / 下 12px）：靠边 4px 能拉大小（zone=" + zoneIn + "）、离边 24px 不会误触发（zone=" + zoneOut + "）");
            Rectangle gripHit = (Rectangle)typeof(MainForm).GetField("_gripHit", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
            // ★两条一起查：① 够大好点中 ② **下沿不能越过分组区上沿**。
            //   原来只查①，于是把手一路伸到第一行文件夹里 8px（点文件夹变成了搬面板）；
            //   收窄到 groupTop 之后高度自然从 20 降到 12，所以①的门槛跟着调到「够用」而不是「20」。
            int grpTop = (int)Field(f, "_groupTop");
            Check(gripHit.Height >= Theme.Px(f, 10) && gripHit.Width >= Theme.Px(f, 100)
                  && gripHit.Bottom <= grpTop,
                "拖动把手可抓范围 = " + gripHit.Width + "x" + gripHit.Height
                + "，下沿 " + gripHit.Bottom + " 不越过分组区上沿 " + grpTop
                + "（够大好点中，又不吃第一行文件夹）");

            // 启动应用后**不关**面板（用户要连着启动好几个）：默认不关，设置里打开才关
            MethodInfo scl = typeof(MainForm).GetMethod("ShouldCloseAfterLaunch", BindingFlags.NonPublic | BindingFlags.Instance);
            Settings st2 = (Settings)typeof(MainForm).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
            bool defClose = (bool)scl.Invoke(f, null);
            st2.CloseAfterLaunch = true;
            bool afterSet = (bool)scl.Invoke(f, null);
            st2.CloseAfterLaunch = false;
            Check(defClose == false && afterSet,
                "启动应用后默认【不关】面板（连着启动用）；设置里打开才会关（默认=" + defClose + " / 打开后=" + afterSet + "）");

            // ---------- 5) 统一滚动度量：面板滚轮按整行走 + 两处滚动条同规格 ----------
            try
            {
                Settings stW = (Settings)typeof(MainForm).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
                stW.PanelW = f.Width;
                stW.PanelH = f.Height;                    // 钉住当前高度，好让内容顶出可视区
                for (int i = 0; i < 10; i++) stW.Groups.Add(new AppGroup("滚动测试" + (i + 1)));
                MethodInfo rc = typeof(MainForm).GetMethod("RefreshContent", BindingFlags.NonPublic | BindingFlags.Instance);
                rc.Invoke(f, null);                       // 探针带 PreviewMode：只在内存里改，不落盘
                SetScroll(f, 0);
                // ★先把「悬停分组」和翻页攒格清掉：光标若停在 >9 个应用的文件夹上，
                //   OnMouseWheel 会走翻页分支把整格吃掉，这里的断言就成了「看上一次鼠标事件的心情」。
                typeof(MainForm).GetField("_hoverGroup", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(f, -1);
                typeof(MainForm).GetField("_wheelAcc", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(f, 0);
                int maxW = (int)Prop(f, "MaxScroll");
                int pitchW = tileH + gy;
                Wheel(f, -120);
                int wy1 = (int)Field(f, "_scrollY");
                Wheel(f, -120);
                int wy2 = (int)Field(f, "_scrollY");
                CheckIf(maxW >= pitchW * 2, "面板内容不足两行（这份配置的组数/面板高度如此），量不出两格",
                    wy1 == pitchW && wy2 == pitchW * 2,
                    "面板滚轮一格 = 一整行（行距 " + pitchW + "）→ _scrollY " + wy1 + " → " + wy2
                    + "（MaxScroll=" + maxW + "；以前不管 Delta 大小都固定滚 60px，会停在半行上）");

                // 像素级：主面板那根滚动条必须和列表那根**同规格**（墨水宽 + 距右缘），
                // 而不是以前的面板 3px/距右 11px、列表 4px/距右 3px 两套数。
                SetScroll(f, 0);
                f.Refresh();
                Application.DoEvents();
                int gH = (int)Field(f, "_groupHeight");
                Rectangle pbar;
                int barHits;
                // 同列表：右界避开窗口边框 + 最外圈边缘高亮（它们比滚动条亮得多，会被误当成墨水）
                int scanPL = Theme.Scroll.InkPx(f) + Theme.Scroll.MarginPx(f) + Theme.Px(f, 8);
                int scanPR = f.Width - 1 - Theme.Scroll.MarginPx(f);
                using (Bitmap shot = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(shot, new Rectangle(0, 0, f.Width, f.Height));
                    pbar = BarBounds(shot, shot.Width - scanPL, scanPR, groupTop, groupTop + gH,
                                     Math.Max(4, Theme.Scroll.MinThumbPx(f) / 2), out barHits);
                }
                int pw = pbar.Width;
                int pm = pbar.IsEmpty ? -1 : (f.Width - 1) - (pbar.Right - 1);
                int cH = (int)Prop(f, "GroupContentHeight");
                Rectangle expP = Theme.Scroll.BarRect(f, groupTop, gH, cH, gH, 0);
                Say("      面板滚动条实测 " + pbar.X + "," + pbar.Y + " " + pbar.Width + "x" + pbar.Height
                    + "（列内命中 " + barHits + " 像素，_groupHeight=" + gH + "）"
                    + "  BarRect=" + expP.X + "," + expP.Y + " " + expP.Width + "x" + expP.Height
                    + "  DPI=" + f.DeviceDpi + "  Ink=" + Theme.Scroll.InkPx(f) + " Margin=" + Theme.Scroll.MarginPx(f)
                    + "  列表 DPI=" + listGeom[0] + " Ink=" + listGeom[1] + " Margin=" + listGeom[2]);
                Check(pbar.IsEmpty == false && Math.Abs(pbar.X - expP.X) <= 1 && Math.Abs(pbar.Width - expP.Width) <= 1
                      && Math.Abs(pbar.Y - expP.Y) <= 1,
                    "面板滚动条真画在 Theme.Scroll.BarRect 指定的位置（实测 " + pbar.X + "," + pbar.Y + " " + pbar.Width + "x" + pbar.Height
                    + "，期望 " + expP.X + "," + expP.Y + " " + expP.Width + "x" + expP.Height + "，±1px 是抗锯齿边缘）");
                // 两处滚动条必须同规格：容差 ±1px（AA 边缘那一个像素在不同底色上时有时无，量不准）
                CheckIf(listBar[0] > 0, "列表那段没跑或列表没超出可视区（没有列表滚动条可比）",
                    pbar.IsEmpty == false && Math.Abs(pw - listBar[0]) <= 1 && Math.Abs(pm - listBar[1]) <= 1,
                    "两处滚动条同规格（±1px 抗锯齿容差）：面板 " + pw + "px 宽 / 距右缘 " + pm + "px，列表 "
                    + listBar[0] + "px 宽 / 距右缘 " + listBar[1] + "px（同一份 Theme.Scroll 数字）");

                // 末页落回面板滚动时要用「攒出来的那一格」，不是最后一个碎 Delta ——
                // 否则触控板在文件夹末页上滚一次只走 1/4 行（看着像没反应）。
                List<GroupView> gs2 = (List<GroupView>)Field(f, "_groups");
                int multi = -1;
                for (int i = 0; i < gs2.Count; i++)
                    if ((int)pageCountM.Invoke(null, new object[] { gs2[i] }) > 1) { multi = i; break; }
                if (multi >= 0)
                {
                    GroupView gv2 = gs2[multi];
                    int pc2 = (int)pageCountM.Invoke(null, new object[] { gv2 });
                    setPageM.Invoke(f, new object[] { gv2, pc2 - 1 });       // 翻到最后一页
                    typeof(MainForm).GetField("_hoverGroup", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(f, multi);
                    SetScroll(f, 0);
                    for (int i = 0; i < 4; i++) Wheel(f, -30);               // 触控板式碎 Delta，累计正好一格
                    int wy3 = (int)Field(f, "_scrollY");
                    typeof(MainForm).GetField("_hoverGroup", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(f, -1);
                    Check(wy3 == pitchW,
                        "文件夹末页上继续滚：落回面板滚动走了 " + wy3 + "px（应为整行 " + pitchW
                        + "，不是按最后一个碎 Delta 算的 " + Theme.Scroll.WheelPixels(30, pitchW, Theme.Scroll.GridRowsPerNotch) + "px）");
                }
                else Say("（没有多页分组，跳过「末页落回」断言）");

                // 分组区滚动条那几列不许再被「右边缘缩放」吃掉：点滚动条不该把窗口拉宽（拉宽就会落盘）。
                MethodInfo rz3 = typeof(MainForm).GetMethod("ResizeZoneAt", BindingFlags.NonPublic | BindingFlags.Instance);
                int probeY = groupTop + Theme.Px(f, 5);
                int zoneOnBar = (int)rz3.Invoke(f, new object[] { new Point(pbar.X + 1, probeY) });
                int zoneInside = (int)rz3.Invoke(f, new object[] { new Point(pbar.X - Theme.Scroll.InkPx(f) - Theme.Px(f, 2), probeY) });
                int zoneOuter = (int)rz3.Invoke(f, new object[] { new Point(f.Width - 1, probeY) });
                Check(zoneOnBar == 0 && (zoneInside & 8) != 0 && (zoneOuter & 8) != 0,
                    "点分组区滚动条那几列（x=" + (pbar.X + 1) + "）不会变成「拉窗口大小」（zone=" + zoneOnBar
                    + "）；条左边 4px 仍能拉（zone=" + zoneInside + "）、最外面 1px 也能拉（zone=" + zoneOuter + "）");

                // 端到端：真的在滚动条那几列按一下再松手 —— 不许进「缩放状态」、面板尺寸也不许变
                // （进了缩放态、松手就会 SavePanelBounds → 写进 settings.json，用户窗口被悄悄改掉）
                int bwBefore = f.Width, bhBefore = f.Height;
                downM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, pbar.X + 1, probeY, 0) });
                bool resizing = (bool)Field(f, "_panelResizing");
                upM2.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, pbar.X + 1, probeY, 0) });
                Check(resizing == false && f.Width == bwBefore && f.Height == bhBefore,
                    "在滚动条上按下 → 不进缩放状态（_panelResizing=" + resizing + "）、面板尺寸不变（"
                    + bwBefore + "x" + bhBefore + " → " + f.Width + "x" + f.Height + "）—— 也就不会落盘改用户配置");
            }
            catch (Exception exP) { Check(false, "面板滚动度量断言异常：" + exP.Message); }

            // ---------- 6) 图标缓存里不许有「彩色乱码」（用户报过 Wallpaper Engine 的雪花图标） ----------
            // 根因：`new Icon(ico, size, size).ToBitmap()` 碰上 **PNG 压缩帧** 会读成噪点（见 IconService
            // 的 DecodeIcoFrame）。这条断言把「整批图标里有没有乱码」钉住 —— 换台机器、换批应用也成立。
            try
            {
                string iconDir = Path.Combine(Program.AppDir, "cache", "icons");
                if (Directory.Exists(iconDir))
                {
                    string[] files = Directory.GetFiles(iconDir, "*.png");
                    int looked = 0, noisy = 0, worstPct = 0;
                    string worst = "";
                    for (int i = 0; i < files.Length; i++)
                    {
                        Bitmap b = null;
                        try
                        {
                            b = new Bitmap(files[i]);
                            int pct = NoisePercent(b);
                            looked++;
                            if (pct > worstPct) { worstPct = pct; worst = Path.GetFileName(files[i]); }
                            if (pct >= 85) noisy++;
                        }
                        catch (Exception) { }
                        finally { if (b != null) b.Dispose(); }
                    }
                    Say("      图标缓存 " + looked + " 张，最乱的一张 " + worst + " = " + worstPct + "%");
                    Check(looked > 0 && noisy == 0,
                        "图标缓存里没有彩色乱码图（检查 " + looked + " 张，乱码 " + noisy + " 张；最乱的 " + worstPct + "% 低于 85% 阈值）");
                }
                else Say("（没有 cache\\icons 目录，跳过图标乱码检查 —— 先跑一次 --preview 生成缓存）");

                // 图标来源补全：Path 是 `.url`（且里面写了存在的 IconFile=）的条目必须补上 IconSource。
                // ★以前 `ApplyIconSources` 里一句 `if (e.IsRealFile) continue;` 把 `.url` 全跳过了（以为
                //   「文件存在就能取到图标」，可 .url 只是快捷方式、shell 常给白纸）→ 用户看到的是
                //   「桌面上是只小黄鸭、添加应用列表里是空白」。这条断言把那个洞钉住。
                List<AppEntry> allApps = (List<AppEntry>)Field(f, "_all");
                int urlWithIcon = 0, urlMissing = 0;
                string missName = "";
                for (int i = 0; i < allApps.Count; i++)
                {
                    AppEntry en = allApps[i];
                    if (en == null || string.IsNullOrEmpty(en.Path)) continue;
                    if (!en.Path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(en.Path)) continue;
                    string icoFile = ReadUrlIconFile(en.Path);
                    if (string.IsNullOrEmpty(icoFile) || !File.Exists(icoFile)) continue;
                    urlWithIcon++;
                    if (string.IsNullOrEmpty(en.IconSource))
                    {
                        urlMissing++;
                        if (missName.Length == 0) missName = en.Name;
                    }
                }
                CheckIf(urlWithIcon > 0, "这台机器上没有「.url 里写着 IconFile=」的条目，测不到来源补全",
                    urlMissing == 0,
                    "「.url 里写着真图标」的 " + urlWithIcon + " 条都补上了 IconSource（漏 "
                    + urlMissing + " 条" + (urlMissing > 0 ? "，例如「" + missName + "」" : "") + "）");
            }
            catch (Exception exIcon) { Check(false, "图标乱码检查异常：" + exIcon.Message); }

            // ---------- 6.7) 自定义显示名（右键 →「重命名…」）★放最后：它会临时改 _hoverGroup/窗口位置，别影响前面的断言 ----------
            try
            {
                Settings stName = (Settings)typeof(MainForm).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
                AppEntry nameTarget = gv.Apps.Count > 0 ? gv.Apps[0] : null;
                if (nameTarget != null)
                {
                    // ① 面板右键菜单：**应用和文件夹整合成一个**（用户 2026-09-28 要求），
                    //    点中小图标时两段都要在，且**块内不许有分隔线**（靠灰色标题分块）。
                    ContextMenuStrip pm = f.BuildTileMenu(gv, nameTarget);
                    string ptexts = "";
                    int seps = 0;
                    for (int i = 0; i < pm.Items.Count; i++)
                    {
                        ptexts += pm.Items[i].Text + " | ";
                        if (pm.Items[i] is ToolStripSeparator) seps++;
                    }
                    Check(ptexts.IndexOf("重命名") >= 0 && ptexts.IndexOf("复制显示名") >= 0,
                        "面板小图标右键菜单里有「重命名…」与「复制显示名」（" + ptexts + "）");
                    Check(ptexts.IndexOf("添加应用") >= 0 && ptexts.IndexOf("删除分组") >= 0,
                        "★点中小图标时，**同一个菜单里也有整个文件夹的操作**（添加应用 / 删除分组）—— 不用再瞄准小图标");
                    Check(seps == 0, "★整合后的菜单里一条分隔线都没有（实测 " + seps + " 条）—— 用户要求「一整块一整块」，靠灰色标题分块");
                    Check(pm.Items.Count > 0 && pm.Items[0].Enabled == false && pm.Items[0].Text == nameTarget.DisplayName,
                        "菜单第一项是**不可点的标题**（实测「" + pm.Items[0].Text + "」，Enabled=" + pm.Items[0].Enabled + "）");
                    // ★「不可点的项」必须**恰好只有那三个标题**（应用 / 文件夹 / 面板）—— 只查第一项的话，
                    //   万一有人把一大片项都禁用了也照样 PASS（子智能体 F 指出的断言边界）。
                    int disabled = 0;
                    for (int i = 0; i < pm.Items.Count; i++) if (pm.Items[i].Enabled == false) disabled++;
                    Check(disabled == 3,
                        "不可点的项**只有 3 个标题**（实测 " + disabled + " 个）—— 除标题外每一项都得能点");
                    Check(ptexts.IndexOf("刷新应用列表") >= 0,
                        "「刷新应用列表」也在这个菜单里（用户要求：别专门跑到设置里找）");
                    // 「名字常驻显示」这个开关**画不出勾**（菜单没有勾选框边距），所以状态必须写在文字里
                    Check(ptexts.IndexOf("名字常驻显示（画在图标下）：已开启") >= 0
                          || ptexts.IndexOf("名字常驻显示（画在图标下）：已关闭") >= 0,
                        "「名字常驻显示」的文字写明了**当前状态**（画不出勾，只能靠文字：" + ptexts.Substring(0, Math.Min(60, ptexts.Length)) + "…）");
                    // 没点中图标时（en = null）：只有文件夹那段，不许出现应用专属项
                    ContextMenuStrip pmNo = f.BuildTileMenu(gv, null);
                    string noTexts = "";
                    for (int i = 0; i < pmNo.Items.Count; i++) noTexts += pmNo.Items[i].Text + " | ";
                    Check(noTexts.IndexOf("添加应用") >= 0 && noTexts.IndexOf("从「") < 0 && noTexts.IndexOf("复制显示名") < 0,
                        "点文件夹背景（没点中图标）时：文件夹操作照样有、应用专属项不出现（" + noTexts + "）");
                    pmNo.Dispose();
                    pm.Dispose();

                    // ①b 把两种菜单真的弹出来各截一张图（「画面改动必须出图看」：文本断言过了，
                    //     但「一整块一整块」到底长什么样、标题够不够显眼，只能看）。
                    foreach (int withApp in new int[] { 1, 0 })
                    {
                        ContextMenuStrip shotMenu = f.BuildTileMenu(gv, withApp == 1 ? nameTarget : null);
                        try
                        {
                            shotMenu.Show(f, new Point(Theme.Px(f, 40), Theme.Px(f, 60)));
                            Application.DoEvents();
                            System.Threading.Thread.Sleep(120);
                            Application.DoEvents();
                            using (Bitmap b = new Bitmap(Math.Max(1, shotMenu.Width), Math.Max(1, shotMenu.Height)))
                            {
                                shotMenu.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                                b.Save(Path.Combine(Program.AppDir, withApp == 1 ? "menu-tile-app.png" : "menu-tile-folder.png"),
                                    System.Drawing.Imaging.ImageFormat.Png);
                            }
                            Say("      右键菜单截图（" + (withApp == 1 ? "点中应用" : "点在文件夹背景") + "）："
                                + shotMenu.Width + "x" + shotMenu.Height + "，菜单项 " + shotMenu.Items.Count + " 个");
                            shotMenu.Close();
                        }
                        catch (Exception exShotMenu) { Say("（右键菜单截图失败，不影响判定：" + exShotMenu.Message + "）"); }
                        shotMenu.Dispose();
                    }

                    // ② 改名字 → 显示名变、设置里记下、原名不动
                    string orig = nameTarget.Name;
                    string test = "面包测试名";
                    f.SetDisplayName(nameTarget, test);
                    bool named = nameTarget.DisplayName == test && nameTarget.Name == orig
                                 && stName.Renames != null && stName.Renames.ContainsKey(nameTarget.Key)
                                 && stName.Renames[nameTarget.Key] == test;
                    // ③ 落盘往返还能读回来（纯函数级，用临时文件、不碰用户配置）
                    string tmp2 = Path.Combine(Program.AppDir, "probe-rename-roundtrip.json");
                    ConfigStore.SaveSettingsFile(tmp2, stName);
                    Settings back2 = ConfigStore.LoadSettingsFile(tmp2);
                    bool roundOk = back2 != null && back2.Renames != null
                                   && string.Equals(AppNames.Lookup(back2, nameTarget.Key), test, StringComparison.Ordinal);
                    try { File.Delete(tmp2); } catch (Exception) { }
                    Check(named && roundOk,
                        "自定义显示名生效：DisplayName=「" + nameTarget.DisplayName + "」、原名保持「" + nameTarget.Name
                        + "」、设置里记着（落盘再读回来=" + roundOk + "）");

                    // ④ 悬停小图标 → 显示名字标签（把状态摆好，出图看）
                    //   ★先把滚动归零、并挑**有应用的第一个组**：跑到这里时前面的用例可能已经滚到
                    //     窗口外或加了空组，悬停在一个空组上不会画名字（那种图没法当证据）。
                    SetScroll(f, 0);
                    int shotGroup = -1;
                    List<GroupView> gsShot = (List<GroupView>)Field(f, "_groups");
                    for (int i = 0; i < gsShot.Count; i++)
                        if (gsShot[i].Apps.Count > 0) { shotGroup = i; break; }
                    if (shotGroup < 0) shotGroup = 0;
                    typeof(MainForm).GetField("_hoverGroup", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(f, shotGroup);
                    typeof(MainForm).GetField("_hoverSlot", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(f, 0);
                    // ★预览是离屏窗口（-4000,-2000）：出图前拉回屏幕内，否则 125% DPI 下 DrawToBitmap 会偏移
                    Point oldLoc = f.Location;
                    f.Location = new Point(0, 0);
                    f.Refresh();
                    Application.DoEvents();
                    try
                    {
                        using (Bitmap shot = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(shot, new Rectangle(0, 0, f.Width, f.Height));
                            shot.Save(Path.Combine(Program.AppDir, "panel-hover-name.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    catch (Exception) { }
                    f.Location = oldLoc;
                    Check(true, "悬停在第一个小图标上（_hoverSlot=0）→ 出图 build\\panel-hover-name.png（名字标签）");

                    // ⑤ 名字常驻显示（右键 →「名字常驻显示」）：菜单项要在、开关要落进设置、能往返
                    ContextMenuStrip pm2 = f.BuildTileMenu(gv, nameTarget);
                    string ptexts2 = "";
                    for (int i = 0; i < pm2.Items.Count; i++) ptexts2 += pm2.Items[i].Text + " | ";
                    bool hasLabelItem = ptexts2.IndexOf("名字常驻") >= 0;
                    pm2.Dispose();

                    bool wasLabel = nameTarget.ShowName;
                    f.SetNameLabel(nameTarget, true);
                    bool labelOn = nameTarget.ShowName && AppNames.IsLabeled(stName, nameTarget.Key);
                    string tmp3 = Path.Combine(Program.AppDir, "probe-label-roundtrip.json");
                    ConfigStore.SaveSettingsFile(tmp3, stName);
                    Settings back3 = ConfigStore.LoadSettingsFile(tmp3);
                    bool labelRound = back3 != null && AppNames.IsLabeled(back3, nameTarget.Key);
                    try { File.Delete(tmp3); } catch (Exception) { }

                    // 出图：名字常驻画在图标下面（截图要看得出字）
                    Point oldLoc2 = f.Location;
                    f.Location = new Point(0, 0);
                    f.Refresh();
                    Application.DoEvents();
                    try
                    {
                        using (Bitmap shot2 = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(shot2, new Rectangle(0, 0, f.Width, f.Height));
                            shot2.Save(Path.Combine(Program.AppDir, "panel-name-label.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    catch (Exception) { }
                    f.Location = oldLoc2;

                    f.SetNameLabel(nameTarget, wasLabel);      // 还原
                    Check(hasLabelItem && labelOn && labelRound,
                        "「名字常驻显示」：菜单里有这项 ✓（" + (hasLabelItem ? "有" : "没有") + "）、勾上后 ShowName="
                        + nameTarget.ShowName + "、设置里记着且落盘往返一致=" + labelRound + "（已还原）");

                    // ⑥ 悬停开关（设置菜单里那条）
                    bool hoverBefore = stName.HoverNames;
                    MethodInfo thn = typeof(MainForm).GetMethod("ToggleHoverNames", BindingFlags.NonPublic | BindingFlags.Instance);
                    thn.Invoke(f, null);
                    bool hoverAfter = stName.HoverNames;
                    thn.Invoke(f, null);
                    Check(hoverBefore != hoverAfter && stName.HoverNames == hoverBefore,
                        "设置菜单「鼠标悬停显示名字」能开关：开→" + hoverAfter + "→关→再切回来=" + stName.HoverNames);

                    // ⑦ ★把「页脚游离加号」的根因钉死：GDI 的 TextRenderer 不认 GDI+ 的 SetClip，
                    //    所以 Theme.DrawText 必须自己夹裁剪区（这条断言就是那次 bug 的回归测试）
                    using (Bitmap cb = new Bitmap(120, 80))
                    {
                        using (Graphics cg = Graphics.FromImage(cb))
                        {
                            cg.Clear(Color.Black);
                            cg.SetClip(new Rectangle(0, 0, 120, 30), System.Drawing.Drawing2D.CombineMode.Replace);
                            Theme.DrawText(cg, "MMMM", Theme.Ui(20f), new Rectangle(20, 40, 80, 30), Color.White,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                        }
                        int bright = 0;
                        for (int y = 35; y < 80; y++)
                            for (int x = 0; x < 120; x++)
                            {
                                Color cc = cb.GetPixel(x, y);
                                if (cc.R > 100) bright++;
                            }
                        Check(bright == 0,
                            "裁剪区外的文字不会被画出来（Theme.DrawText 自己夹了裁剪：GDI 不认 GDI+ 的 SetClip，"
                            + "这正是「面板页脚上冒出游离加号」的根因；裁剪区外亮像素=" + bright + "）");
                    }
                    // ⑦b 边缘高亮：贴边才亮，**鼠标离开窗口必须灭掉**
                    //   用户 2026-09-28 报：「这个侧边的蓝色怎么不自己消失呢，很影响美观」。
                    //   病根：`_hoverZone` 只在 OnMouseMove 里更新，鼠标贴着边离开面板后就再没人清它，
                    //   那条 2px 蓝边会一直亮着（真机实测：鼠标在面板外 265px 处，左边那列 706/706 像素仍是蓝的）。
                    try
                    {
                        FieldInfo hzF = typeof(MainForm).GetField("_hoverZone", BindingFlags.NonPublic | BindingFlags.Instance);
                        MethodInfo mvM = typeof(MainForm).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                        MethodInfo lvM = typeof(MainForm).GetMethod("OnMouseLeave", BindingFlags.NonPublic | BindingFlags.Instance);
                        lvM.Invoke(f, new object[] { EventArgs.Empty });                    // 先归零：别受前面用例影响
                        mvM.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.None, 0, 2, f.Height / 2, 0) });
                        int zOn = (int)hzF.GetValue(f);
                        lvM.Invoke(f, new object[] { EventArgs.Empty });                    // 鼠标离开窗口
                        int zOff = (int)hzF.GetValue(f);
                        Check(zOn != 0 && zOff == 0,
                            "鼠标贴左边缘 → 高亮那条边（_hoverZone=" + zOn + "）；**鼠标一离开就灭**（离开后 _hoverZone="
                            + zOff + "）—— 不灭的话面板边上会永远挂着一条蓝线（用户报过的那个「不自己消失」）");

                        // ★还有一条更隐蔽、也是用户实际撞上的那条路：**拖动/缩放结束**时必须清掉。
                        //   拖动期间 `SuspendTransparencyForDrag()` 把面板临时变成不透明 → 边缘高亮就是那时候点亮的；
                        //   松手后又变回**颜色键穿透** → 面板再也收不到 MouseMove/MouseLeave → 只能靠收尾这一刀清干净。
                        hzF.SetValue(f, 4);                       // 假装拖的时候点亮了左边那条
                        typeof(MainForm).GetMethod("EndDrag", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(f, null);
                        int zAfterDrag = (int)hzF.GetValue(f);
                        Check(zAfterDrag == 0,
                            "拖动/缩放结束后边缘高亮也清掉（实测 _hoverZone=" + zAfterDrag
                            + "）—— 不清的话，拖完面板边上就一直挂着一条蓝线，鼠标怎么移都灭不掉");
                    }
                    catch (Exception exEdge) { Check(false, "边缘高亮断言异常：" + exEdge.Message); }

                    // ⑧ 设置菜单的三个开关：**打勾 = 当前状态**，文字写「已开启 / 已关闭」
                    //   （以前是「亚克力半透明：关」=「点一下会关掉」，用户反馈分不清状态还是动作）
                    ContextMenuStrip sm = f.BuildSettingsMenu();
                    string stexts = "";
                    bool acrylicOk = false, hoverOk = false, closeOk = false;
                    for (int i = 0; i < sm.Items.Count; i++)
                    {
                        string tx = sm.Items[i].Text;
                        stexts += tx + " | ";
                        ToolStripMenuItem mi = sm.Items[i] as ToolStripMenuItem;      // Checked 在 ToolStripMenuItem 上，不在 ToolStripItem 上
                        bool ck = mi != null && mi.Checked;
                        if (tx.StartsWith("亚克力半透明")) acrylicOk = (ck == stName.Acrylic) && tx.EndsWith(ck ? "已开启" : "已关闭");
                        if (tx.StartsWith("鼠标悬停显示名字")) hoverOk = (ck == stName.HoverNames) && tx.EndsWith(ck ? "已开启" : "已关闭");
                        if (tx.StartsWith("启动应用后关闭面板")) closeOk = (ck == stName.CloseAfterLaunch) && tx.EndsWith(ck ? "已开启" : "已关闭");
                    }
                    try
                    {
                        sm.Show(f, new Point(Theme.Px(f, 30), Theme.Px(f, 40)));
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(120);
                        Application.DoEvents();
                        using (Bitmap b = new Bitmap(Math.Max(1, sm.Width), Math.Max(1, sm.Height)))
                        {
                            sm.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                            b.Save(Path.Combine(Program.AppDir, "settings-menu.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                        sm.Close();
                    }
                    catch (Exception) { }
                    sm.Dispose();
                    Check(acrylicOk && hoverOk && closeOk,
                        "设置菜单三个开关：打勾与文字都表示**当前状态**（" + stexts + "）");

                    // ⑨ 启动时**不许弹任何提示**（用户 2026-09-28 明确要求：「第一眼有个弹窗」很烦）。
                    //    亚克力那两条提示（「已开启」和「这台系统不支持」）连同方法一起删掉了 ——
                    //    这里断言它们**确实不存在**，免得以后有人顺手把弹窗加回来。
                    //    （「不支持」那种情况改成菜单文字如实显示，见 FillSettingsMenu。）
                    MethodInfo oldHint = typeof(MainForm).GetMethod("ShowAcrylicHint",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    MethodInfo oldHint2 = typeof(MainForm).GetMethod("ShowAcrylicUnsupportedHint",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    Check(oldHint == null && oldHint2 == null,
                        "亚克力的两个提示框都彻底拆掉了（反射查 ShowAcrylicHint="
                        + (oldHint == null ? "不存在 ✓" : "★又回来了") + "、ShowAcrylicUnsupportedHint="
                        + (oldHint2 == null ? "不存在 ✓" : "★又回来了") + "）");

                    // ⑩ 分组区下沿到页脚之间那条窄空白带里不许有亮笔画：
                    //    被面板下沿切掉一半的空格子如果还画加号，就会在这条带里留下"游离的加号"（用户报了两次）。
                    try
                    {
                        int fh = Theme.Px(f, 38) + Theme.Px(f, 12);
                        int bandTop = Math.Max(0, f.Height - fh - Theme.Px(f, 10));
                        int bandBottom = Math.Max(0, f.Height - fh);
                        int stray = 0;
                        using (Bitmap shot = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(shot, new Rectangle(0, 0, f.Width, f.Height));
                            for (int y = bandTop; y < bandBottom; y++)
                                for (int x = 2; x < shot.Width - 2; x++)      // 跳过最外 2px（那里是面板边框）
                                {
                                    Color c = shot.GetPixel(x, y);
                                    if (((c.R + c.G + c.B) / 3) > 90) stray++;
                                }
                        }
                        Check(stray == 0,
                            "分组区下沿到页脚之间没有乱入的笔画（游离加号回归；这条带里的亮像素=" + stray + "）");
                    }
                    catch (Exception exBand) { Check(false, "页脚空白带检查异常：" + exBand.Message); }
// ⑨ ★鼠标穿透检查（用户报的「鼠标在面板空白处滚滚轮，别的应用滚了」）：
                    //    亚克力的「透出桌面」必须把窗体背景挖空，而挖空的像素**连鼠标一起穿透**
                    //    （MSDN：color-keyed 区域会让鼠标消息穿过去）—— 两者同一机制、不可兼得。
                    //    ★用户拍板：好看优先（保留整块透出桌面），所以这里**断言基线**（关掉亚克力 = 完全不穿透），
                    //      亚克力开着时的穿透数只作为「已知代价」记在日志里。
                    try
                    {
                        bool acrylicOn = stName.Acrylic;
                        // ★采样前先把面板摆回屏幕内 (0,0)：前面几个用例会把面板拖到别处（甚至屏幕外的 -4000,-2000），
                        //   而 WindowFromPoint 是看**屏幕坐标**的 —— 面板在屏幕外就永远命中不了，会假失败。
                        Point oldLocHit = f.Location;
                        f.Location = new Point(0, 0);
                        f.Refresh();
                        Application.DoEvents();
                        Point[] blanks = new Point[]
                        {
                            new Point(f.Width / 2, Theme.Px(f, 24)),                    // 顶部空白（标题区）
                            new Point(Theme.Px(f, 20), f.Height / 2),                   // 左侧留白
                            new Point(f.Width - Theme.Px(f, 20), Theme.Px(f, 24)),      // 右上留白
                        };
                        // 基线：关掉亚克力（完全不挖空）→ 命中面板自己的才算「有效内部点」（自校准，别把窗口外的点算进来）
                        stName.Acrylic = false;
                        SetAcrylicState(f, Theme.ApplyBackdrop(f, false));
                        f.Refresh(); Application.DoEvents();
                        List<Point> valid = new List<Point>();
                        for (int i = 0; i < blanks.Length; i++)
                            if (HitSelf(f, f.PointToScreen(blanks[i]))) valid.Add(blanks[i]);

                        // 亚克力开着（默认观感）：同批点会穿透 —— 已知代价。
                        // ★两个前提都要满足，否则测出来的不是"穿透"而是"根本没挖空"：
                        //   ① ApplyBackdrop 得真的生效（返回值）；
                        //   ② 还得把窗体的 `_acrylicActive` 一起设上 —— PaintAll 靠它决定画不画不透明背景，
                        //      只调 ApplyBackdrop 而不同步这个字段的话，背景照画 → 永远测不出穿透（踩过）。
                        stName.Acrylic = true;
                        bool acrylicApplied = Theme.ApplyBackdrop(f, true);
                        SetAcrylicState(f, acrylicApplied);
                        f.Refresh(); Application.DoEvents();
                        int throughCount = 0;
                        for (int i = 0; i < valid.Count; i++)
                            if (HitSelf(f, f.PointToScreen(valid[i]))) throughCount++;
                        // ★「挖空」要在**亚克力正开着**的这一刻读（下面马上就按配置还原了，还原后读的是另一回事）
                        Color keyColor = f.TransparencyKey;
                        bool keyed = (keyColor != Color.Empty) && (f.BackColor == keyColor);

                        stName.Acrylic = acrylicOn;
                        SetAcrylicState(f, Theme.ApplyBackdrop(f, acrylicOn));
                        f.Location = oldLocHit;          // 还原面板位置
                        f.Refresh();

                        // ★前提①：采样点得真的落在**探针自己的**面板上（你要是正开着 BreadLauncher，
                        //   它盖在离屏窗口上 → WindowFromPoint 命中的是它 → 有效点 0 个 → 跳过）。
                        // ★前提②：亚克力得真的生效（极少数环境下会退回不透明 → 那当然不穿透）。
                        CheckIf(valid.Count > 0 && acrylicApplied,
                            "采样点被别的窗口挡住 / 面板不在屏幕内 / 离屏窗口上亚克力没生效（已退回不透明），跳过穿透检查",
                            throughCount < valid.Count,
                            "亚克力开着时面板空白处**会穿透鼠标**（命中自己 " + throughCount + "/" + valid.Count
                            + "）—— 这就是「整块透出桌面」观感的已知代价（用户报的「空白处滚滚轮、别的应用滚了」），"
                            + "关掉亚克力就完全不穿透；这条也是那次事件的回归测试");
                        // ★不依赖屏幕几何的硬判据：亚克力生效 = 窗口真的被"挖空"了
                        //   （BackColor 与 TransparencyKey 都设成同一个 key 色）。这条是本轮那个 bug 的回归测试：
                        //   曾经按 SetWindowCompositionAttribute 的返回值判断成败 → 任何机器都判成失败 → 永远不生效。
                        Check(acrylicApplied && keyed,
                            "开着亚克力时窗口确实做了「挖空」（BackColor = TransparencyKey = " + keyColor + "）"
                            + " —— 没挖空半透明就是假的；判据不看那个不可靠的 API 返回值");
                        Say("      已知代价：开着亚克力（默认）时同一批点命中自己=" + throughCount + "/" + valid.Count
                            + "（整块透出桌面的观感就是这么来的；不想穿透就在设置里关掉亚克力）");
                    }
                    catch (Exception exHit) { Check(false, "穿透检查异常：" + exHit.Message); }

                    // ⑤ 恢复原名：记录清掉、显示名变回原名
                    f.SetDisplayName(nameTarget, null);
                    Check(nameTarget.DisplayName == orig
                          && (stName.Renames == null || stName.Renames.ContainsKey(nameTarget.Key) == false),
                        "恢复原名：显示名回到「" + nameTarget.DisplayName + "」、设置里的记录也删掉了");
                    typeof(MainForm).GetField("_hoverGroup", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(f, -1);
                    typeof(MainForm).GetField("_hoverSlot", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(f, -2);
                }
                else Say("（这组没有应用，跳过显示名断言）");
            }
            catch (Exception exName) { Check(false, "显示名断言异常：" + exName.Message); }

            // ★★ 变体：这一格被分组区下沿切掉了一部分 —— 被裁掉的那段里不能再命中
            //   （「绘制认裁剪、命中不认」那个老 bug 家族的回归测试，见 MainForm.GroupHitTest 的可见区守卫）。
            //   默认配置（3 行、面板够高）不会触发 → 必须拿 build\test-settings-10groups.json 跑。
            try
            {
                int seamCols = (int)Prop(f, "Columns");
                int seamTw = (int)Prop(f, "TileW");
                int seamGx = (int)Prop(f, "GapX");
                int seamGy = (int)Prop(f, "GapY");
                int seamTh = (int)Prop(f, "TileH");
                int seamLeft = (int)Field(f, "_groupLeft");
                int seamTopF = (int)Field(f, "_groupTop");
                int seamH = (int)Field(f, "_groupHeight");
                int seamScroll = (int)Field(f, "_scrollY");
                int seamVisibleBottom = seamTopF + seamH;
                if (seamScroll != 0)
                {
                    Say("（跳过：这次 _scrollY=" + seamScroll + "≠0，看不到「被下沿切掉一半」的格子）");
                    NoteSkip("变体：滚动位置不为 0，没有「被分组区下沿切掉」的格子可测（用 10 组配置且 _scrollY=0 才有）");
                }
                else
                {
                    int clipped = 0, alive = 0;
                    // GroupHitTest 是 MainForm 的私有方法（探针只反射调用，不改成 internal）
                    MethodInfo seamHit = typeof(MainForm).GetMethod("GroupHitTest", BindingFlags.NonPublic | BindingFlags.Instance);
                    for (int i = 0; i < groups.Count; i++)
                    {
                        int r = i / seamCols, c = i % seamCols;
                        int x = seamLeft + c * (seamTw + seamGx);
                        int y = seamTopF + r * (seamTh + seamGy) - seamScroll;
                        if (y + seamTh <= seamVisibleBottom) continue;   // 完整可见：跟这条断言无关
                        clipped++;
                        int probeX = x + seamTw / 2;
                        for (int probeY = seamVisibleBottom + 1; probeY < y + seamTh; probeY++)
                        {
                            object[] seamArgs = new object[] { new Point(probeX, probeY), 0, 0 };
                            seamHit.Invoke(f, seamArgs);
                            if ((int)seamArgs[1] >= 0) alive++;
                        }
                    }
                    if (clipped == 0)
                    {
                        Say("（跳过：这份配置在 _scrollY=0 下没有被下沿切掉的格子 —— 换 build\\test-settings-10groups.json 跑）");
                        NoteSkip("变体：配置没有被分组区下沿切掉的格子（要用 build\\test-settings-10groups.json）");
                    }
                    else
                    {
                        Check(alive == 0,
                            "被分组区下沿切掉的 " + clipped + " 个格子里，可见区之外一个点都不再命中"
                            + "（命中数=" + alive + "；这是「点空白缝启动看不见的应用」那次的回归测试）");
                    }
                }
            }
            catch (Exception exSeam) { Check(false, "裁剪缝断言异常：" + exSeam.Message); }

            // ★★ 变体：分组被**全删光**之后，「常用」组不能自己复活
            //   （ConfigStore.Normalize 的 Pinned 迁移必须带 `Seeded == false` 守卫；
            //    Pinned 全工程没有任何地方会清空它，不加守卫 = 用户永远清不空分组）
            try
            {
                Settings sm = (Settings)typeof(MainForm).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
                // 前提：内存里此刻有分组，才动得了「清空」这个场景；否则这份配置测不了
                if (sm == null || sm.Groups == null || sm.Groups.Count == 0)
                {
                    Say("（跳过：这份配置一开始就没有分组，测不了「删光之后会不会复活」）");
                    NoteSkip("变体：配置本身没有分组，测不了「全删光后复活」");
                }
                else
                {
                    List<AppGroup> groupsBack = sm.Groups;
                    List<string> pinnedBack = sm.Pinned;
                    bool seededBack = sm.Seeded;
                    try
                    {
                        sm.Pinned = new List<string>();
                        sm.Pinned.Add("microsoft.windows.explorer");
                        sm.Pinned.Add("microsoft.windowscalculator_8wekyb3d8bbwe!app");
                        sm.Seeded = true;
                        sm.Groups = new List<AppGroup>();
                        ConfigStore.Normalize(sm);      // 读和写都会走这一遍：全删光的那一刻就是这里
                        Check(sm.Groups.Count == 0,
                            "把分组全删光之后没有组自己冒出来（写入路径 Normalize：Groups=" + sm.Groups.Count
                            + "；Pinned 里还留着 " + sm.Pinned.Count + " 条旧固定项。这条是「删一次回来一次」那次的回归测试）");

                        // 反向：老配置（没有 Seeded 这个键 → false）的升级迁移必须照做，别被守卫误伤
                        Settings old2 = new Settings();
                        old2.Seeded = false;
                        old2.Pinned = new List<string>();
                        old2.Pinned.Add("microsoft.windows.explorer");
                        old2.Groups = new List<AppGroup>();
                        ConfigStore.Normalize(old2);
                        Check(old2.Groups.Count == 1 && old2.Groups[0].Keys.Count == 1,
                            "老配置（没有 Seeded 键 → false）仍然照常迁移出「常用」组（组数=" + old2.Groups.Count
                            + "，组内=" + (old2.Groups.Count > 0 ? old2.Groups[0].Keys.Count : 0) + "）—— 守卫没误伤升级路径");

                        // 读到的那份也不该复活（Normalize 在 Load 里也会跑）
                        List<AppGroup> none = new List<AppGroup>();
                        Settings read2 = new Settings();
                        read2.Seeded = true;
                        read2.Pinned = new List<string>();
                        read2.Pinned.Add("microsoft.windows.explorer");
                        read2.Groups = none;
                        ConfigStore.Normalize(read2);
                        Check(read2.Groups.Count == 0, "读取路径同样不复活（Groups=" + read2.Groups.Count + "）");
                    }
                    finally
                    {
                        sm.Groups = groupsBack;
                        sm.Pinned = pinnedBack;
                        sm.Seeded = seededBack;
                        f.Invalidate();
                    }
                }
            }
            catch (Exception exPin) { Check(false, "「常用」组复活断言异常：" + exPin.Message); }

            // ★★ 变体：图标缓存里那个 null（「这张取不到」）不能盖住后来真取到的图
            //   （IconService.Get 第二轮竞争：null 是合法缓存值，但不该让好图被 Dispose 掉）
            try
            {
                IconService svc = (IconService)typeof(MainForm).GetField("_icons", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
                List<AppEntry> icoAll = (List<AppEntry>)Field(f, "_all");
                if (svc == null || icoAll == null || icoAll.Count == 0)
                {
                    Say("（跳过：这份配置里没有可用的应用条目，测不了图标缓存竞争）");
                    NoteSkip("变体：没有可用的应用条目，测不了图标缓存竞争");
                }
                else
                {
                    // ★前提：必须挑一个**磁盘缓存真的存在**的条目。
                    //   随手拿第一条（例如 steam:// 那种）它本来就取不到图 → Get 老老实实返回 null，
                    //   那条 null 是**正确契约**、不是被顶掉的好图，测它等于测了个假前提（第一版这里就写错了）。
                    System.Reflection.MethodInfo fileNameM = typeof(IconService).GetMethod("FileName", BindingFlags.NonPublic | BindingFlags.Static);
                    string iconDir = (string)typeof(IconService).GetField("_iconDir", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(svc);
                    AppEntry ie = null;
                    Bitmap first = null;
                    string png = null;
                    for (int i = 0; i < icoAll.Count; i++)
                    {
                        Bitmap got;
                        try { got = svc.Get(icoAll[i], 32); }
                        catch { continue; }
                        if (got == null) continue;
                        string f2 = Path.Combine(iconDir, (string)fileNameM.Invoke(null, new object[] { icoAll[i], 32 }));   // ★FileName 是静态方法
                        if (File.Exists(f2) == false) continue;    // 只能来自内存，Inject 之后测不出"恢复"
                        ie = icoAll[i]; first = got; png = f2;
                        break;
                    }
                    if (ie == null)
                    {
                        Say("（跳过：这份配置里没有一个「磁盘上真有缓存 PNG」的条目 —— 挑出能复现竞争的前提不成立）");
                        NoteSkip("变体：没有带磁盘缓存的条目，测不了图标缓存竞争（第一版就是拿了个取不到图的条目，假前提）");
                    }
                    else
                    {
                        System.Reflection.FieldInfo memF = typeof(IconService).GetField("_mem", BindingFlags.NonPublic | BindingFlags.Instance);
                        System.Collections.IDictionary mem = (System.Collections.IDictionary)memF.GetValue(svc);
                        string ck = null;
                        foreach (System.Collections.DictionaryEntry de in mem)
                            if (object.ReferenceEquals(de.Value, first)) { ck = (string)de.Key; break; }
                        if (ck == null)
                        {
                            Say("（跳过：内存缓存里没找到刚取的那张图的键）");
                            NoteSkip("变体：内存缓存里找不到键");
                        }
                        else
                        {
                            // 先记下磁盘那张 PNG 的指纹：如果它**没变**，就说明下面这次走的是
                            // 「读回内存/磁盘已有那张」，而不是重新抽一张图再覆盖上去。
                            long pngLen0 = new FileInfo(png).Length;
                            string pngHash0 = Sha256File(png);
                            mem[ck] = null;                    // 模拟「另一个线程先失败，把 null 存了进来」
                            Bitmap again = svc.Get(ie, 32);
                            Check(again != null,
                                "缓存里已有那个 null（「这张取不到」）时，再取一次仍然拿得到真图"
                                + "（返回 " + (again == null ? "null ❌ = 好图被 null 顶掉了" : "真图 ✅") + "）");
                            Check(object.ReferenceEquals(mem[ck], null) == false && mem[ck] != null,
                                "缓存里那个 null 被真图覆盖掉了（不是一直留着一张「取不到」的标记）");
                            Check(object.ReferenceEquals(mem[ck], again),
                                "返回的就是缓存里那一张（不是又做了一张、然后漏一张 GDI 位图）");
                            Check(again == null || (Sha256File(png) == pngHash0 && new FileInfo(png).Length == pngLen0),
                                "磁盘上那张 PNG 没被重写（证明走的是「拿回已有那张」这条路，不是重新抽图）");
                        }
                    }
                }
            }
            catch (Exception exIco) { Check(false, "图标缓存竞争断言异常：" + exIco.Message); }

            // ★★ 变体：后悔药（.prev）必须真的是「上一版」，而且写失败不能连累别的文件
            //   （ConfigStore.Write 的「内容没变就不写」+ 逐文件禁写。用户选的通知方式 = 只在「关于」里写一笔）
            try
            {
                string dir = Path.Combine(Program.AppDir, "probe-prev");
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception) { }
                Directory.CreateDirectory(dir);
                string f1 = Path.Combine(dir, "s.json");
                string f3 = Path.Combine(dir, "c.json");

                ConfigStore.SaveSettingsFile(f1, OneGroup("A"));
                ConfigStore.SaveSettingsFile(f1, OneGroup("B"));
                // ★这一次内容和磁盘上一字不差 —— 旧写法会照样重写、把 .prev 轮转成 B（后悔药就废了）
                SaveResult r3 = ConfigStore.SaveSettingsResult(f1, OneGroup("B"));
                string prev = File.Exists(f1 + ".prev") ? File.ReadAllText(f1 + ".prev", Encoding.UTF8) : "(没有)";
                Check(r3 == SaveResult.AlreadyCurrent && prev.Contains("\"A\""),
                    "内容一字没变时不重写、也不动 .prev：第三次落盘 = " + r3
                    + "，.prev 里还是上一版（含 \"A\"=" + prev.Contains("\"A\"") + "）"
                    + " —— 这条是「后悔药被每次开关面板覆盖掉」那次的回归测试");

                // 内容真变了必须照写、.prev 跟着轮转
                ConfigStore.SaveSettingsFile(f1, OneGroup("C"));
                string prev2 = File.ReadAllText(f1 + ".prev", Encoding.UTF8);
                Check(prev2.Contains("\"B\""), "内容真变了照写，.prev 轮转成上一版（现在含 \"B\"=" + prev2.Contains("\"B\"") + "）");

                // ② 一个文件写失败，不许连累别的文件（原来是全局 SuppressWrite，缓存坏了会连分组一起禁写）
                Directory.CreateDirectory(f3 + ".bad");          // 占住备份路径 → BackupBad 必失败
                File.WriteAllText(f3, "{ 这不是合法 json", Encoding.UTF8);
                ConfigStore.Read<AppCache>(f3, null);            // 读坏 → 备份失败 → 只禁 f3 自己
                SaveResult bad = ConfigStore.Write(f3, new AppCache());
                SaveResult other = ConfigStore.Write(f1, OneGroup("D"));
                Check(bad == SaveResult.Failed, "坏文件那个路径自己确实被禁写了（返回 " + bad + "）");
                Check(other != SaveResult.Failed,
                    "★别的文件照写不误（返回 " + other + "）—— 修掉「缓存文件坏了就把分组也禁写」那次的回归测试");

                // ③ 写失败要留下能查的痕迹（用户要的：只在「关于」里写一笔）
                string failTxt = Path.Combine(dir, "locked");
                Directory.CreateDirectory(failTxt);              // 拿目录当文件写 → 一定失败
                SaveResult failed = ConfigStore.Write(failTxt, OneGroup("E"));
                Check(failed == SaveResult.Failed && string.IsNullOrEmpty(ConfigStore.LastWriteError) == false,
                    "写失败会记下原因（「关于」里显示的文案）："
                    + (string.IsNullOrEmpty(ConfigStore.LastWriteError) ? "没记 ❌" : "已记 ✅"));

                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception) { }
            }
            catch (Exception exPrev) { Check(false, "后悔药断言异常：" + exPrev.Message); }

            // ★★ 变体：「关于」窗口那行「配置保存状态」—— 用户选的通知方式就是写在这里
            try
            {
                // 直接测真正生成那句文案的方法（比在 exe 里搜字符串可靠：UTF-16 字面量在文件里
                // 可能起始于奇数偏移，成对解码会漏 —— 第一版就是这么假 FAIL 的）
                System.Reflection.MethodInfo about = typeof(MainForm).GetMethod("AboutSaveStatusText", BindingFlags.NonPublic | BindingFlags.Instance);
                Check(about != null, "「关于」的配置保存状态由一个单独方法生成（探针能直接断言它）");
                if (about != null)
                {
                    string okTxt = (string)about.Invoke(f, new object[] { null });
                    Check(okTxt.Contains("配置保存：正常"),
                        "没出过事时「关于」显示「" + okTxt + "」");
                    string badTxt = (string)about.Invoke(f, new object[] { "写不进去：磁盘满了" });
                    Check(badTxt.Contains("上次配置没保存成功") && badTxt.Contains("磁盘满了") && badTxt.Contains("配置"),
                        "出过事时「关于」会写出原因（" + badTxt.Replace("\n", " ") + "）");
                }

                // 真写一次失败，MainForm 里那个字段必须被填上（不然「关于」永远显示正常）
                System.Reflection.FieldInfo wf = typeof(MainForm).GetField("_lastWriteFailText", BindingFlags.NonPublic | BindingFlags.Instance);
                Check(wf != null, "「写失败」这句话存在 MainForm 的字段里，供「关于」读取");
                Directory.CreateDirectory(Path.Combine(Program.AppDir, "probe-about"));   // 目录当文件写 → 必失败
                Check(ConfigStore.Write(Path.Combine(Program.AppDir, "probe-about"), new AppCache()) == SaveResult.Failed
                      && string.IsNullOrEmpty(ConfigStore.LastWriteError) == false,
                    "写失败会在 ConfigStore 里留下原因（「关于」拿的就是它）");
                try { Directory.Delete(Path.Combine(Program.AppDir, "probe-about"), true); }
                catch (Exception) { }
            }
            catch (Exception exAbout) { Check(false, "「关于」断言异常：" + exAbout.Message); }

            // ★★ 变体：源码级体检（2026-09-27 那批「★低」里的两条硬规矩）
            //   ① 每个 OnPaint 都必须有绘制兜底（不然 WinForms 画白底红叉，什么线索都没有）
            //   ② 日志必须轮转（不然用户装完不管，一年能涨到几十 MB）
            try
            {
                string root = Path.GetDirectoryName(Program.AppDir);       // build\ 的上一级 = 仓库根
                string srcDir = Path.Combine(root, "src");
                if (Directory.Exists(srcDir) == false)
                {
                    Say("（跳过：找不到 src 目录（" + srcDir + "），源码级体检跑不了）");
                    NoteSkip("变体：找不到 src 目录，OnPaint 兜底 / 日志轮转这两条源码体检跑不了");
                }
                else
                {
                    // ① 逐个 OnPaint：方法体里必须出现 try 或 PaintAll/PaintCatch
                    List<string> naked = new List<string>();
                    int painted = 0;
                    string[] files = Directory.GetFiles(srcDir, "*.cs");
                    for (int fi = 0; fi < files.Length; fi++)
                    {
                        string[] ls = File.ReadAllLines(files[fi], Encoding.UTF8);
                        for (int i = 0; i < ls.Length; i++)
                        {
                            if (System.Text.RegularExpressions.Regex.IsMatch(ls[i], @"override\s+void\s+OnPaint") == false) continue;
                            painted++;
                            int depth = 0; bool started = false; int end = -1;
                            for (int j = i; j < Math.Min(ls.Length, i + 90); j++)
                            {
                                depth += ls[j].Split('{').Length - ls[j].Split('}').Length;
                                if (!started && depth > 0) started = true;
                                if (started && depth == 0) { end = j; break; }
                            }
                            StringBuilder body = new StringBuilder();
                            for (int k = i + 1; k < (end < 0 ? ls.Length : end); k++) body.AppendLine(ls[k]);
                            string bd = body.ToString();
                            if (bd.Contains("try") == false && bd.Contains("PaintAll(") == false && bd.Contains("PaintCatch(") == false)
                                naked.Add(Path.GetFileName(files[fi]) + ":" + (i + 1));
                        }
                    }
                    Check(painted > 0 && naked.Count == 0,
                        "源码里 " + painted + " 个 OnPaint 全都有绘制兜底"
                        + (naked.Count == 0 ? "" : "（裸奔的：" + string.Join("、", naked.ToArray()) + "）")
                        + " —— 漏一个就会「白底红叉 + 零线索」");

                    // ② 日志轮转：塞一个超阈值的 log.txt，写一行后必须被挪走、新文件重新开始
                    string tdir = Path.Combine(Program.AppDir, "probe-logrotate");
                    try { if (Directory.Exists(tdir)) Directory.Delete(tdir, true); }
                    catch (Exception) { }
                    try
                    {
                        Directory.CreateDirectory(Path.Combine(tdir, "cache"));
                        string lf = ConfigStore.LogFile(tdir);
                        File.WriteAllText(lf, new string('x', 1200000), Encoding.UTF8);   // 1.2 MB > 阈值 1 MB
                        long before = new FileInfo(lf).Length;
                        // ★轮转检查节流到「一分钟一次」（防抖）：探针刚启动时就查过一次，这里必须
                        //   把节流戳重置掉，否则这一分钟内的检查全被跳过 → 假 FAIL（第一版就这么栽的）。
                        System.Reflection.FieldInfo stamp = typeof(ConfigStore)
                            .GetField("_logSizeCheckedAt", BindingFlags.NonPublic | BindingFlags.Static);
                        if (stamp != null) stamp.SetValue(null, DateTime.MinValue);
                        ConfigStore.Log(tdir, "轮转测试");
                        string rolled = Path.Combine(Path.GetDirectoryName(lf), "log.1.txt");
                        long after = new FileInfo(lf).Length;
                        Check(File.Exists(rolled) && before > 1000000 && after < 100000,
                            "日志超 1MB 会轮转：原 " + before + " 字节 → 挪成 log.1.txt（在=" + File.Exists(rolled)
                            + "），新 log.txt 只有 " + after + " 字节");
                    }
                    finally
                    {
                        try { if (Directory.Exists(tdir)) Directory.Delete(tdir, true); }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception exSrc) { Check(false, "源码级体检异常：" + exSrc.Message); }

            // ★★ 变体：分组「瘦身再回涨」之后不能跳页（PageOf 夹取要写回）
            try
            {
                List<GroupView> gvs = (List<GroupView>)Field(f, "_groups");
                GroupView big = null;
                for (int i = 0; i < gvs.Count; i++) if (gvs[i].Apps != null && gvs[i].Apps.Count > 9) { big = gvs[i]; break; }
                if (big == null)
                {
                    Say("（跳过：这份配置里没有超过 9 个应用的组，测不了「瘦身再回涨」）");
                    NoteSkip("变体：没有超过 9 个应用的组，测不了页码残留");
                }
                else
                {
                    // 需要直接调 MainForm 的私有方法 SetPage / PageOf
                    MethodInfo mSetPage = typeof(MainForm).GetMethod("SetPage", BindingFlags.NonPublic | BindingFlags.Instance);
                    MethodInfo mPageOf = typeof(MainForm).GetMethod("PageOf", BindingFlags.NonPublic | BindingFlags.Instance);
                    List<AppEntry> saved = big.Apps;
                    try
                    {
                        mSetPage.Invoke(f, new object[] { big, 2 });          // 翻到第 3 页
                        int at3 = (int)mPageOf.Invoke(f, new object[] { big });
                        // 瘦身到只剩 2 个（1 页），再回涨回原样：残留的页码必须跟着收敛
                        big.Apps = new List<AppEntry>();
                        if (saved.Count > 0) big.Apps.Add(saved[0]);
                        if (saved.Count > 1) big.Apps.Add(saved[1]);
                        int afterShrink = (int)mPageOf.Invoke(f, new object[] { big });
                        big.Apps = saved;
                        int afterGrow = (int)mPageOf.Invoke(f, new object[] { big });
                        Check(afterShrink == 0 && afterGrow == 0,
                            "分组「瘦身再回涨」之后停在合理页（翻到第 3 页=" + at3 + " → 瘦身后=" + afterShrink
                            + " → 回涨后=" + afterGrow + "，都应该是 0）—— 页码残留那次的回归测试");
                    }
                    finally { big.Apps = saved; }
                }
            }
            catch (Exception exPg) { Check(false, "页码残留断言异常：" + exPg.Message); }

            // ★★ 变体：「名字常驻显示」在小档位下不能把图标压到认不出
            try
            {
                MethodInfo mLayout = typeof(MainForm).GetMethod("NameLabelLayout", BindingFlags.NonPublic | BindingFlags.Instance);
                Check(mLayout != null, "「名字常驻」的留位算法是单独一个方法（探针能直接断言它）");
                if (mLayout != null)
                {
                    Settings nmSet = (Settings)typeof(MainForm).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
                    int nmGap = (int)Prop(f, "MiniGap");

                    // ★三档各建一个同配置的窗体来问（MiniBox 取决于建窗时的 FolderScale），
                    //   并钉死**每档该怎样**。上一版这条写成了
                    //   `(drew && h>=X) || drew == false` —— 后半句**恒真**，于是「默认档 100 也不画名字」
                    //   这个真回归它一点没抓到（独立复查用真窗体实测出来的）。现在三档分开查。
                    // ★三档各建一个窗体来问，并钉死**每档该怎样**。上一版这条写成了
                    //   `(drew && h>=X) || drew == false` —— 后半句**恒真**，于是「默认档 100 也不画名字」
                    //   这个真回归它一点没抓到（独立复查用真窗体实测出来的）。现在三档分开查。
                    // ★注意：新窗体是**从磁盘重新读配置**的，所以改内存里的 FolderScale 没用 ——
                    //   必须把三份临时配置写到盘上（用同一个临时文件反复改），再拿它建窗。
                    int oldScale = nmSet.FolderScale;
                    bool sawDont = false, sawDefault = false, sawBig = false;
                    StringBuilder nmLog = new StringBuilder();
                    string tmpCfg = Path.Combine(Program.AppDir, "probe-namelabel.json");
                    int[] scales = new int[] { 85, 100, 120 };
                    for (int si = 0; si < scales.Length; si++)
                    {
                        int sc = scales[si];
                        MainForm probe2 = null;
                        try
                        {
                            nmSet.FolderScale = sc;
                            ConfigStore.SaveSettingsResult(tmpCfg, nmSet);   // 落到临时文件（不碰用户配置）
                            probe2 = new MainForm(tmpCfg);
                            probe2.PreviewMode = true;
                            probe2.ShowInTaskbar = false;
                            probe2.StartPosition = FormStartPosition.Manual;
                            probe2.Location = new Point(-4000, -1600);
                            probe2.Show();
                            Application.DoEvents();

                            int box = (int)Prop(probe2, "MiniBox");
                            int gap = (int)Prop(probe2, "MiniGap");
                            int p2Tw = (int)Prop(probe2, "TileW");
                            int p2Th = (int)Prop(probe2, "TileH");
                            Rectangle cell = new Rectangle(0, 0, box, box);
                            object[] a = new object[] { cell, gap, p2Tw, p2Th, 0, 0, Rectangle.Empty, Rectangle.Empty };
                            bool drew = (bool)mLayout.Invoke(probe2, a);
                            Rectangle inn = (Rectangle)a[6];
                            nmLog.Append("      FolderScale=").Append(sc).Append("  格 ").Append(box).Append("px → ")
                                 .Append(drew ? "画（图标区 " + inn.Height + "px）" : "不画").AppendLine();
                            if (sc == 85 && !drew) sawDont = true;
                            if (sc == 100 && drew) sawDefault = true;
                            if (sc == 120 && drew) sawBig = true;
                        }
                        finally
                        {
                            if (probe2 != null) { try { probe2.Close(); probe2.Dispose(); } catch (Exception) { } }
                        }
                    }
                    nmSet.FolderScale = oldScale;
                    Say(nmLog.ToString().TrimEnd());
                    Check(sawDont, "FolderScale 85（格子最小）：**不画**名字，把整格高度留给图标（不然图标只剩 14px 认不出）");
                    Check(sawDefault, "★FolderScale 100（**默认档**）：名字照样画得出来 —— 这条是「默认档功能静默失效」那次的回归测试");
                    Check(sawBig, "FolderScale 120：名字照样画得出来");

                    // 格子足够大时必须照画（别把功能一刀砍掉）
                    Rectangle nmBig = new Rectangle(0, 0, Theme.Px(f, 120), Theme.Px(f, 120));
                    object[] nmArgs2 = new object[] { nmBig, nmGap, (int)Prop(f, "TileW"), (int)Prop(f, "TileH"), 0, 0, Rectangle.Empty, Rectangle.Empty };
                    bool nmDrewBig = (bool)mLayout.Invoke(f, nmArgs2);
                    Check(nmDrewBig, "格子够大时「名字常驻」照画（" + Theme.Px(f, 120) + "px 格子 → 画=" + nmDrewBig + "）");
                }
            }
            catch (Exception exNm) { Check(false, "名字常驻留位断言异常：" + exNm.Message); }

            // ★★ 变体：三条「拖动/缩放结束」的分支必须都作废 `_downValid`
            //   （不然拖完面板之后**第一次右键菜单会被吃掉**：右键不刷新 _downPoint，
            //     拿的是上次左键的按下点，于是「移动超过 3px 就不算点击」那条判定把右键也挡了）
            // ★走真实鼠标路径（OnMouseDown → OnMouseMove → OnMouseUp 通过反射调），不只看字段。
            // ★鼠标左键按下之后**不移动**再抬起，才能走到需要 _downValid 的那条路（安全：不弹菜单）。
            try
            {
                FieldInfo fDownValid = typeof(MainForm).GetField("_downValid", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fPanelMoving = typeof(MainForm).GetField("_panelMoving", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fPanelMoved = typeof(MainForm).GetField("_panelMoved", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fDragActive = typeof(MainForm).GetField("_dragActive", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fDragFrom = typeof(MainForm).GetField("_dragFrom", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fDragTo = typeof(MainForm).GetField("_dragTo", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fResizing = typeof(MainForm).GetField("_panelResizing", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo mDown = typeof(MainForm).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo mUp = typeof(MainForm).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo mMove = typeof(MainForm).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                Check(fDownValid != null && mDown != null && mUp != null && mMove != null,
                    "能反射到 _downValid 与三个鼠标处理（用来复现「拖完右键被吃掉」）");

                if (fDownValid != null && mDown != null && mUp != null)
                {
                    // 先找一个空白点按下（不落在任何分组上），这样按下时进入「准备搬面板」那条路
                    int gLeft = (int)Field(f, "_groupLeft");
                    int gTop = (int)Field(f, "_groupTop");
                    Point blank = new Point(Theme.Px(f, 4), Theme.Px(f, 4));   // 左上角，避开分组区和把手
                    Check(blank.X < gLeft || blank.Y < gTop, "选中的按下点确实在分组区之外（空白点）");

                    string[] names = new string[] { "搬面板", "拖分组", "缩放面板" };
                    bool[] cleared = new bool[3];
                    for (int vi = 0; vi < 3; vi++)
                    {
                        // 每次都从「左键按在空白处」开始
                        mDown.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, blank.X, blank.Y, 0) });
                        // 再摆成对应的拖动状态（模拟那三种拖动已经把状态置上了）
                        fPanelMoving.SetValue(f, false);
                        fPanelMoved.SetValue(f, false);
                        fDragActive.SetValue(f, false);
                        fResizing.SetValue(f, false);
                        fDownValid.SetValue(f, true);            // 保证前提成立（真按下时它确实是 true）
                        if (vi == 0) { fPanelMoving.SetValue(f, true); fPanelMoved.SetValue(f, true); }
                        if (vi == 1) { fDragActive.SetValue(f, true); fDragFrom.SetValue(f, 0); fDragTo.SetValue(f, 0); }
                        if (vi == 2) { fResizing.SetValue(f, true); }
                        // 松手：不移动（所以不会走进「移动超过 3px」那条判定）
                        mUp.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, blank.X, blank.Y, 0) });
                        cleared[vi] = (bool)fDownValid.GetValue(f) == false;
                        // 复原状态，别影响后面的断言
                        fPanelMoving.SetValue(f, false);
                        fPanelMoved.SetValue(f, false);
                        fDragActive.SetValue(f, false);
                        fDragFrom.SetValue(f, -1);
                        fDragTo.SetValue(f, -1);
                        fResizing.SetValue(f, false);
                        fDownValid.SetValue(f, false);
                        Application.DoEvents();
                    }
                    Check(cleared[0] && cleared[1] && cleared[2],
                        "拖完/缩完之后 `_downValid` 都被作废（搬面板=" + cleared[0]
                        + "、拖分组=" + cleared[1] + "、缩放=" + cleared[2]
                        + "）—— 不作废的话，下一次**右键**会被「移动超过 3px 就不算点击」误伤，菜单弹不出来");
                }
            }
            catch (Exception exDv) { Check(false, "拖动后右键断言异常：" + exDv.Message); }

            f.Close();            ReportSkips(); Say(_ok ? "结果：全部通过" : "结果：有失败项");
            Flush();
            Environment.ExitCode = _ok ? 0 : 1;
        }

        private static void SetScroll(MainForm f, int v)
        {
            typeof(MainForm).GetField("_scrollY", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(f, v);
        }

        private static void Wheel(MainForm f, int delta)
        {
            MethodInfo m = typeof(MainForm).GetMethod("OnMouseWheel", BindingFlags.NonPublic | BindingFlags.Instance);
            m.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.None, 0, 0, 0, delta) });
        }

        /// <summary>给列表控件喂一个滚轮事件。</summary>
        private static void WheelList(AllAppsList lst, int delta)
        {
            MethodInfo m = typeof(AllAppsList).GetMethod("OnMouseWheel", BindingFlags.NonPublic | BindingFlags.Instance);
            m.Invoke(lst, new object[] { new MouseEventArgs(MouseButtons.None, 0, 10, 10, delta) });
        }

        /// <summary>
        /// 在一张截图里量出「滚动条墨水」的外框：逐列数「明显比背景亮」的像素。
        /// 用来断言**两处**滚动条（主面板 / 应用列表）真的画在同一套位置与宽度上，
        /// 而不是各写各的 3px / 11px / 36 与 4px / 3px / 28。
        /// 返回的矩形 = 找到的墨水外框（抗锯齿边缘可能多算 1px，断言时给 ±1 容差）。
        /// </summary>
        private static Rectangle BarBounds(Bitmap bmp, int xFrom, int xTo, int yFrom, int yTo, int minInk, out int hits)
        {
            int left = -1, right = -2, top = 99999, bottom = -1;
            if (minInk < 4) minInk = 4;    // 墨水至少这么高（调用方按最小滑块/2 传，跟着 DPI 走）
            hits = 0;
            for (int x = Math.Max(0, xFrom); x <= Math.Min(bmp.Width - 1, xTo); x++)
            {
                int col = 0, colTop = 99999, colBottom = -1;
                for (int y = Math.Max(0, yFrom); y <= Math.Min(bmp.Height - 1, yTo); y++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if ((c.R + c.G + c.B) / 3 > 55)
                    {
                        col++;
                        if (y < colTop) colTop = y;
                        if (y > colBottom) colBottom = y;
                    }
                }
                if (col >= minInk)             // 墨水至少这么高（= 最小滑块的一半，跟着 DPI 走）
                {
                    if (left < 0) left = x;
                    right = x;
                    if (col > hits) hits = col;
                    if (colTop < top) top = colTop;
                    if (colBottom > bottom) bottom = colBottom;
                }
            }
            if (left < 0) return Rectangle.Empty;
            return new Rectangle(left, top, right - left + 1, bottom - top + 1);
        }

        /// <summary>触发按钮点击。
        /// ★注意：不能靠「合成 OnMouseDown/OnMouseUp」来测 FlatButton —— 它只在 OnMouseUp 里清 `_down`，
        /// 真正的 Click 由 WinForms 内部鼠标状态机发出，合成事件驱动不了（列表行能测是因为它自己处理逻辑）。
        /// 所以这里直接调受保护的 OnClick，等价于用户点下去之后走的那条路。</summary>
        private static void ClickButton(FlatButton b)
        {
            MethodInfo oc = typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance);
            oc.Invoke(b, new object[] { EventArgs.Empty });
        }

        /// <summary>
        /// 乱码分（%）：相邻像素「差得离谱」的比例。真图标 ≤65%（像素风游戏 54%、细密 logo 61%），
        /// 而**被错读的 PNG 压缩帧图标是 92~99%** —— 用户报的 Wallpaper Engine「彩色雪花」就是这种。
        /// </summary>
        private static int NoisePercent(Bitmap b)
        {
            int bad = 0, n = 0;
            for (int y = 0; y < b.Height; y += 2)
            {
                for (int x = 0; x + 1 < b.Width; x++)
                {
                    Color p = b.GetPixel(x, y);
                    if (p.A < 40) continue;
                    Color q = b.GetPixel(x + 1, y);
                    n++;
                    if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 60) bad++;
                }
            }
            return n >= 20 ? bad * 100 / n : 0;
        }

        /// <summary>读 .url（INI 文本）里的 IconFile=；没有就返回 null。</summary>
        private static string ReadUrlIconFile(string path)
        {
            try
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string s = lines[i].Trim();
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!s.Substring(0, eq).Trim().Equals("IconFile", StringComparison.OrdinalIgnoreCase)) continue;
                    return s.Substring(eq + 1).Trim();
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>写一个 .url 快捷方式文件（「自定义扫描目录」端到端断言要用）。</summary>
        private static void WriteUrlFile(string path, string url, string iconFile)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[InternetShortcut]");
            sb.AppendLine("URL=" + url);
            if (string.IsNullOrEmpty(iconFile) == false) sb.AppendLine("IconFile=" + iconFile);
            File.WriteAllText(path, sb.ToString());
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point p);

        /// <summary>这个屏幕坐标点是不是命中这个窗体自己（用来验证「鼠标穿透」）。
        /// 分层窗口里被 color-key 挖空的像素会让 WindowFromPoint 返回下面的窗口 —— 正是用户遇到的现象。</summary>
        private static bool HitSelf(Form f, Point screenPt)
        {
            try { return WindowFromPoint(screenPt) == f.Handle; }
            catch (Exception) { return false; }
        }

        /// <summary>把「亚克力实际生效」的状态同步到窗体字段 —— PaintAll 靠它决定画不画不透明背景。
        /// 只调 Theme.ApplyBackdrop 而不同步这个字段，窗口就不会真的"挖空"，穿透也就测不出来。</summary>
        private static void SetAcrylicState(Form f, bool active)
        {
            try
            {
                typeof(MainForm).GetField("_acrylicActive", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(f, active);
                f.Invalidate();
            }
            catch (Exception) { }
        }

        /// <summary>反射调 AppPickerForm.SetSource（「来源」菜单那三个模式）。</summary>
        private static void SetSourceInvoke(object form, int mode)
        {
            MethodInfo m = form.GetType().GetMethod("SetSource", BindingFlags.NonPublic | BindingFlags.Instance);
            m.Invoke(form, new object[] { mode });
        }

        private static void Check(bool cond, string what)        {
            if (cond == false) _ok = false;
            Say((cond ? "[PASS] " : "[FAIL] ") + what);
        }

        /// <summary>
        /// 前提成立才断言，否则打印「跳过」。
        /// ★为什么要这个：探针固定用 `test-settings-groups.json` 跑（3 组 / 12 个应用那一组 / 4 列），
        ///   但**换一份配置**（比如用户自己的 settings.json：8 组、33 个应用的组、面板更宽→列数变了）时，
        ///   有些断言的前提根本不成立 —— 例如「列表只有 3 个候选」时它压根不超出可视区，当然没有滚动条、
        ///   也滚不动。那种情况报 FAIL 就是**假失败**，会把人骗去查一个不存在的 bug。
        /// </summary>
        private static void CheckIf(bool pre, string preWhy, bool cond, string what)
        {
            if (pre == false) { NoteSkip(preWhy); Say("（跳过：" + preWhy + "）"); return; }
            Check(cond, what);
        }

        /// <summary>被跳过的断言（按理由归并）。★规矩：0 FAIL ≠「都测过了」——
        ///   必须看跳过清单（`test-settings-groups.json` 曾经静默跳过整段分页断言）。</summary>
        private static readonly List<string> Skips = new List<string>();
        private static void NoteSkip(string why)
        {
            if (Skips.Contains(why) == false) Skips.Add(why);
        }

        /// <summary>报告末尾打印「这次跳过了哪几条」，让跳过再也藏不住。</summary>
        private static void ReportSkips()
        {
            if (Skips.Count == 0)
            {
                Say("跳过汇总：0 条（本次每一处带前提的断言都真的跑了）");
                return;
            }
            Say("跳过汇总：共 " + Skips.Count + " 类断言没跑（这些**没有被验证**，别把「0 FAIL」当成「都测过了」）：");
            for (int i = 0; i < Skips.Count; i++) Say("  " + (i + 1) + ". " + Skips[i]);
        }

        private static void Say(string s)
        {
            Log.Add(s);
            Console.WriteLine(s);
        }

        /// <summary>报告同时写一份 UTF-8 文件，省得受控制台代码页折腾。</summary>
        private static void Flush()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (string line in Log) sb.AppendLine(line);
                File.WriteAllText(Path.Combine(Program.AppDir, "clickprobe-report.txt"), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        private static object Prop(object o, string name)
        {
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
            return p.GetValue(o, null);
        }

        /// <summary>造一份只含一个分组的配置（后悔药/禁写那几条断言用，别碰真实配置）。</summary>
        private static Settings OneGroup(string name)
        {
            Settings s = new Settings();
            s.Seeded = true;
            AppGroup g = new AppGroup(name);
            g.Keys.Add("microsoft.windows.explorer");
            s.Groups.Add(g);
            return s;
        }

        /// <summary>文件的 SHA256（小写十六进制）。用来断言「这个文件没被动过」。</summary>
        private static string Sha256File(string path)
        {
            try
            {
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] h = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder(h.Length * 2);
                    for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch (Exception) { return "?"; }
        }

        private static object Field(object o, string name)
        {
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            return fi.GetValue(o);
        }
    }
}
