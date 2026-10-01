# 自检命令（排查问题用）

> 这些是**命令行自检模式**，跟面板里的功能互不影响。README 只留了速查，这里是完整版：
> 每个模式干什么、会写哪些文件、有没有副作用、以及「独立验收」用的那几条。

```bat
build\BreadLauncher.exe --scan                      列出扫描到多少应用、分组统计、前 40 条（桌面快捷方式现在也算正式条目）
build\BreadLauncher.exe --preview 图.png [settings.json]
                                                    离屏渲染一张面板图（检查排版）；给了 settings.json 就读它
build\BreadLauncher.exe --previewall 图.png 0 [settings.json]
                                                    离屏渲染「查看全部」窗口（0 = 第 1 个分组，序号必填）
build\BreadLauncher.exe --launch 名称                直接走启动链路（验证能不能拉起应用）
build\BreadLauncher.exe --icontest 名称              把某个应用的图标导出到 build\icontest\（含清缓存强制重取）
build\BreadLauncher.exe --icons                     列出补到「真实图标来源」的条目
build\BreadLauncher.exe --dupicons                  按图标像素哈希找重复图标（看「通用空白图标」清干净没有）
build\BreadLauncher.exe --previewscan 图.png        离屏渲染「管理自定义文件夹」窗口（排版改动要看图；它没有按钮点击用例）
```

> 本机实测：桌面快捷方式补齐之后，`--scan` 报 **175** 条，其中 **88** 条在桌面上有快捷方式 —— 79 条是本来就扫到的同名条目（打「桌面」标记），9 条是只存在于桌面、按快捷方式本身新建的条目，**和桌面上实际的 88 个快捷方式完全对齐**（个人桌面 59 个 + 公共桌面 29 个）。数字随机器上装的软件而变。

这些模式都**不弹可见的面板**（预览窗口建在屏幕外的 `-4000,-2000`），**唯一保证绝对不动的是 `settings.json`**（你的分组数据不会被改；唯一例外是它本来就已读坏时会被备份成 `settings.json.bad`，原文件依然不动）。其余副作用是真实存在的，跑之前心里有数：

- `--scan` / `--launch` / `--icons` / `--dupicons` 都会真扫一遍应用，每次往 `build\cache\log.txt` 追加**三行**（「图标来源补全」+「桌面快捷方式：标记已有 N 条、新增 M 条」+「扫描 shell:AppsFolder 完成」）。
- `--dupicons` 除了扫一遍写日志，还会**对每个条目取一张 32px 图标并落盘**到 `build\cache\icons\`。
- `--preview` / `--previewall` 会**真的创建一个屏幕外窗口**（布局、取图标全跑一遍），所以会把取到的图标 PNG 写进 `build\cache\icons\`。写日志**不是无条件的**：只有真的触发扫描（没有可用缓存，或缓存超过 12 小时触发后台刷新）或取图失败时才写 `build\cache\log.txt`；缓存新鲜、图标齐全时预览不写日志。
- `apps-cache.json` **`--preview` 和 `--previewall` 都不写**：两处 `ConfigStore.SaveCache(...)` 调用点分别在 `MainForm.LoadData` 和 `MainForm.StartBackgroundRefresh` 里，**两条都被 `PreviewMode == false` 挡住**（`--preview` 在 `Show()` 之前就置了 `PreviewMode = true`，而 `LoadData` 是在 `OnLoad` 里才调的，正好走守卫）。2026-09-27 实测：预览 / 预览全部跑完，`apps-cache.json` 的哈希和 mtime 都不变。
  ★这条原来写反了（说"`--preview` 没有可用缓存时必写"）——**照那句去删守卫就会把历史事故招回来**（探针/预览把临时目录的条目写进用户真实缓存，`MainForm.LoadData` 里的注释写着这件事）。
  `--previewall` 只读缓存或现扫一遍，同样不落盘。
- `--icontest` **会先清掉图标缓存再强制重取**：`IconService.ClearDiskCache()` 会清内存缓存 + 删掉 `build\cache\icons\` 目录下的所有 `*.png`（目录本身保留）—— 想保住图标缓存就别跑它。
- `--launch` 会真的启动应用，别拿它试你不认识的名字。
- 缓存新鲜时（本机独立验收时实测过）预览连日志都不写、`settings.json` 的 mtime 和哈希都不变；但别把这条当成「自检绝对无副作用」的保证 —— 上面几条才是完整规则。

三个坑：

- **参数给少了会变成正常启动**：自检分支是按参数个数严格匹配的（`--preview` 要 2 个参数、`--previewall` 要 3 个以上），所以 `--previewall 图.png`（漏了组序号）或者光写一个 `--preview` **不匹配任何自检分支**，程序会掉进「单实例 + 打开面板」那条正常启动路径 —— 也就是**真的把面板 GUI 开出来，并正常读写你的 `settings.json`**。自检时参数一定写全，`--previewall` 的组序号（第一个分组写 `0`）不能省。
- **抓 stdout**：exe 确实是 **GUI 子系统**（PE Subsystem=2），但 PowerShell 管道**能**拿到输出 —— pwsh 7.6.4 实测 `& .\build\BreadLauncher.exe --scan 2>&1 | Out-String` 抓到 2684 字符真实输出；只是子进程吐的字节是 **CP936/GBK**，pwsh 按 UTF-8 解出来是乱码。想读得懂就重定向到文件再按 GBK 解码：`cmd /c "build\BreadLauncher.exe --scan > build\scan.txt"`。
- **别把两种编码搞混**：子进程 stdout 是 GBK，而 `build\cache\log.txt` 本身是 **UTF-8（无 BOM）**。

### 开发用探针：`tools\ClickProbe.cs`

它和 `src\*.cs` 一起编成一个**独立的小 exe**（不是成品的一部分），用反射验证几处最容易写错的交互：分页算得对不对（这组几页、第 1 页第 9 格就是第 9 个应用、第 2 页第 1 格是第 10 个、越界格子必须是空）、滚轮停在文件夹上是不是翻内页（向下 → 下一页，向上 → 上一页）、滚轮不在文件夹上时方向与系统一致（向下 = 看更下面的分组）；图标之间那几像素的缝隙也要测出来是「图块主体」而不是旁边的图标。它还会真的走一遍这些新交互：在「添加应用」选择器里点三行 → 确认打钩数 = 3；把一个文件夹拖到第 2 个位置、再拖到空白处（应该落到末尾）；拖右下角把面板拉大；在空白处按住拖动搬走面板；**拖顶部那根固定把手也能搬面板** —— 后两条同时验证「拖完面板不会被误关掉」。滚动相关它也管：扫像素核对面板那条和列表那条滚动条真的画在同一套位置上（都按 `Theme.Scroll` 的墨水 4px、距右缘 3px 来，比对时留 ±1px 抗锯齿容差），核对滚轮步长（列表一格 = 3 行、面板整格 = 一整行），核对滚动条的命中区不会把行右端的勾选框抢走；还能直接断言「添加应用」选择器的右键菜单有哪些项、菜单开着时窗口不会被「失去焦点」关掉、**在文件夹最后一页继续滚会落回面板滚动并正好走一整行**、**点分组区那条滚动条不会变成「把面板拉大」**。

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe /platform:x64 /codepage:65001 ^
  /out:build\clickprobe.exe /main:BreadLauncher.ClickProbe ^
  /reference:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Web.Extensions.dll ^
  tools\ClickProbe.cs src\*.cs

build\clickprobe.exe [settings.json] [组序号]
```

它还会验**格子上的右键菜单**（2026-09-28 整合成一个、2026-10-01 缩成两块）：同一个菜单里**同时**有应用项和文件夹项（点中小图标时）、点格子背景时**只有文件夹块**、**主菜单里一条分隔线都没有**（用户要求「一整块一整块」靠灰色标题分块）、第一项是**不可点的标题**、不可点的项**恰好只有 2 个**；★**「刷新应用列表」不许出现在这个菜单里**（2026-10-01 撤掉，见 `dev-notes.md` 坑 48），而**设置菜单里那份必须保留**。★另有一组**不设前提**的断言（放在探针收尾处，**7 份配置每份都会跑**）：右键空白处只剩「新建分组…」/ 两个自动重扫阈值（`CacheRefreshMinutes = 5`、`PickerRescanMinutes = 1`）/ **`IsCacheStale` 的单位是分钟**（90 分钟前的缓存算旧、2 分钟前的不算）/ 「开窗前要不要重扫」的判决口径；「添加应用」窗口里还会**真按一下左下角「重新扫描」**并出图 `build\picker-rescan.png`。它还会断言亚克力那两个提示框方法**已经不存在**（`ShowAcrylicHint` / `ShowAcrylicUnsupportedHint`），并**真的把两种菜单各弹一次截图**：`build\menu-tile-app.png`（点中应用）与 `build\menu-tile-folder.png`（点背景）—— 按项目规矩「画面改动必须出图看」。探针只读配置（面板带预览模式，**不写 `settings.json`、也不写 `apps-cache.json`**）、不会启动任何应用，「查看全部」窗口由定时器自动取消；报告同时打印到控制台并写一份 `build\clickprobe-report.txt`。它和 `--preview` 一样会真的创建屏幕外窗口，所以也会写图标缓存（日志同 `--preview`：只有触发扫描或取图失败时才写）。要验分页那几条，得给它一份含**超过 9 个应用**的分组的配置（正好 9 个会去验边界：第 9 格能启动、只有 1 页）；它跑完还会顺手截一张第 2 页的面板图 `build\panel-page2.png`，可以直接打开看排版。★**跑完要逐份比条数**（数字对不上说明有断言被静默跳过，见 `dev-notes.md` 坑 47）。

### ★ 哪份配置覆盖哪一段（别只用一份跑完就说「全过」）

| 配置 | 覆盖 | 备注 |
|---|---|---|
| `test-settings-groups.json` | 命中测试 / 框选 / 右键菜单 / 显示名 / 滚动条像素 / 亚克力 | 组1 名义 12 个 key，**实际只有 5 个能解析** |
| `test-settings-10apps.json` | **分页 / 滚轮翻页 / 碎 Delta 攒格** | 10/10 可解析，分页那段只有这份跑得到 |
| `test-settings-10groups.json` | **「被分组区下沿切掉的格子，可见区之外不能再命中」** | 10 组 → 第 4 行被下沿切掉；其余配置会跳过这条 |
| `test-settings-9apps.json` | 正好一页的边界 | 9/9 |
| `test-settings-36groups.json` | 面板滚动 | 每组 2 个 |
| `test-settings-scroll.json` | 滚动 / 空态 | 13 个空组 |
| 你自己的 `settings.json` | 真实数据（大组 31 个 → 4 页） | 只读，不落盘 |

**规矩：`0 FAIL` ≠「都测过了」。** 探针在报告末尾打印**「跳过汇总」**（2026-09-27 起），
看到 `（跳过：…）` 就等于那一条**没有被验证**。
★特别注意两条容易「静默跳过」的关键断言：**「跟手 1:1：鼠标走 100px 面板走 100px」**（拖动/搬面板那一段，
以前因为坐标在性能测试改宽度之前取好而恒跳过 → 2026-09-27 已修）和**「被下沿切掉的格子不能再命中」**
（`test-settings-10groups.json` / `test-settings-36groups.json` / `test-settings-scroll.json` 都会真的跑：实测分别切掉 1 / 16 / 5 个格子）。这两条必须亲眼看到 `[PASS]`。
