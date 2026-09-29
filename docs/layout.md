# 目录结构

> 克隆下来之后，每个文件 / 目录是干什么的，以及 `src\` 里 13 个源文件各自的职责。

```
BreadLauncher\
├─ 编译-零安装.cmd        一键编译（缺图标时顺手生成图标）
├─ 运行.cmd               只运行已经编好的 exe（不编译）
├─ app.manifest           asInvoker + 系统 DPI，避免高 DPI 下糊
├─ assets\
│   └─ BreadLauncher.ico  16~256 多尺寸图标（丢了可用 tools\MakeIcon.cs 生成）
├─ src\                   全部源码（见下表）
├─ tools\                 开发 / 自检工具（5 个 .cs，不随成品发布）
│   ├─ MakeIcon.cs        图标生成器（生成 assets\BreadLauncher.ico；配色也在这里改）
│   ├─ ClickProbe.cs      自检探针（分页、命中、框选、拖排序、滚动条、右键菜单、来源筛选、显示名、鼠标穿透…）→ build\clickprobe.exe
│   │                     ★**别在这里写死「多少处 `Check(...)`」「跑多少条断言」**：改一次代码就变，写死了必然过期
│   │                     （想看就现场数：`(Select-String -Path tools\ClickProbe.cs -Pattern 'Check\(|CheckIf\(' -AllMatches).Count`；
│   │                     实际跑到的条数以 `build\clickprobe-report.txt` 末尾的「跳过汇总」为准 —— 0 FAIL ≠ 都测过了）
│   ├─ AppsProbe.cs       按名字打印 AppsFolder 条目的 ExtendedProperty（查 TargetParsingPath 用）
│   ├─ IconProbe.cs       取图标探针（对比各种取图方式）
│   └─ IcoCheck.cs        拆开 .ico 的每帧 + 各取法的「噪声分」（查彩色乱码图标用）
├─ build\                 编译产物 + 数据（**不随仓库发布**，只有 6 个自检配置随仓库走）
│   ├─ BreadLauncher.exe  成品
│   ├─ clickprobe.exe     开发探针（可选）
│   ├─ settings.json      你的分组配置
│   ├─ verify\            独立验收留下的复现报告与原始证据（本机生成、不随仓库发布）
│   └─ cache\             图标、应用列表缓存、日志
├─ README.md              用户向文档：怎么用、怎么编译、已知限制（这份目录说明在 docs\layout.md）
├─ AGENTS.md              **短入口**：三条红线 + 指向下面几份（文件名是 AI 助手的通用约定名）
├─ docs\dev-notes.md      开发约定 / 验收标准 / 四十多条踩坑清单（原 `AGENTS.md`，2026-09-29 挪到这里并改成中性标题）
├─ 交接文档.md            内部交接与验收记录（含个人路径，**不随仓库发布**）
├─ .gitignore             挡住用户数据（settings / 缓存 / 本机测试配置）、构建产物和一次性证据
  └─ .gitattributes         统一换行（Windows 项目：全部 CRLF）、图标/截图按二进制处理
```

`build\` 里还会散着一些自检 / 探针留下的临时文件（`scan.txt`、`preview-*.png`、`clickprobe-report.txt` 之类），可以随手删；但 `build\verify\` 是独立验收留下的原始证据（复现报告 + 各次运行的输出字节），**先别删**。

⚠ **要把这个项目发给别人 / 开源的话**：`build\test-settings-*.json` 这 6 份是**故意放行进仓库**的（`.gitignore` 末尾的 `!build/test-settings-*.json`），已经脱敏（本机复核：6 份里盘符路径 0 条、`steam://` 0 条）；本机临时配置是 `build\tmp.json`，别带出去。仓库里的 `.gitignore` 已经挡住了 `build\settings*.json`、`build\cache\`、`build\verify\`、`build\*.png` 这些含个人数据或一次性证据的东西，照着它挑要带的东西就不会漏。

`src\` 里各文件的分工：

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口、单实例、全部自检模式 |
| `MainForm.cs` | 面板窗体：大文件夹布局与**组内分页**、**面板搬动 / 缩放**、**拖动分组换位**、命中测试、启动、**三套右键菜单**（格子上的整合菜单 / 面板空白 / 左下角设置）、落盘 |
| `GroupAppsForm.cs` | 「查看全部」窗口：整组列表、单击启动、右键移除 |
| `Dialogs.cs` | 三个深色对话框：`TextPromptForm`（新建 / 重命名分组 / 输入扫描路径）、`AppPickerForm`（**勾选式多选**添加应用，带筛选框 + **「来源」菜单**（全部 / 桌面 / 自定义文件夹 / 管理自定义文件夹…）+ 右键重命名 / 名字常驻）、`ScanFoldersForm`（管理自定义扫描文件夹） |
| `Controls.cs` | 自绘控件：`FlatButton`、`AllAppsList`（列表 + 勾选框 + 「桌面」/「新增」小标签 + 手画滚动条）、深色菜单渲染器 |
| `Groups.cs` | `AppGroup`（落盘结构）/ `GroupView`（解析后的视图模型） |
| `IconService.cs` | 取图标（`IShellItemImageFactory` + 兜底），内存 / 磁盘缓存，3 条后台线程 |
| `AppsFolderScanner.cs` | 扫 `shell:AppsFolder` **和桌面快捷方式**（桌面按顶层 + 一级子文件夹，且桌面条目不套名字噪声规则），噪声过滤、去重、补图标来源 |
| `ConfigStore.cs` | `settings.json` / `apps-cache.json` / `log.txt` 读写（原子写） |
| `Launcher.cs` | 启动应用（协议 / 真文件 / UWP 三个分支） |
| `PinyinLetter.cs` | 汉字转拼音首字母（列表的 `# A B C …` 分组用） |
| `Theme.cs` | 颜色、字体、DPI 换算、圆角绘制、亚克力 |
| `Model.cs` | `AppEntry` 数据模型 |
