# AGENTS.md

> 文件名 `AGENTS.md` 是个**通用约定**（AI 编程助手会自动读仓库根目录的这个文件），并不是"交接条子"。
> 这个仓库的**开发约定、验收规矩、以及四十多条「已经踩过的坑」**全在 **[docs/dev-notes.md](docs/dev-notes.md)** —— 动代码之前先读它。

## 三条红线（其余见 docs/dev-notes.md）

1. **不写 C 盘**：数据只落在程序自己所在的目录（`settings.json`、`cache\`），不碰注册表、不写 `%AppData%`。
2. **零安装、零依赖**：只用系统自带的 `csc.exe` 编译，不引 NuGet、不引第三方 DLL，目标 .NET Framework 4.0。
3. **关掉就退出**：不加托盘图标、不加开机自启、不留后台进程。

## 想快速上手

| 想干什么 | 看哪份 |
|---|---|
| 用这个软件 / 下载 | [README.md](README.md) |
| 知道每个文件干什么 | [docs/layout.md](docs/layout.md) |
| 跑自检（探针） | [docs/selfcheck.md](docs/selfcheck.md) |
| 改代码的约定、验收标准、踩坑清单 | **[docs/dev-notes.md](docs/dev-notes.md)** |
