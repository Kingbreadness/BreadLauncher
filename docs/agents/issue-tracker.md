# Issue tracker: GitHub

这个 repo 的 issues 和 specs 存放在 **GitHub Issues** 中（仓库的 Issues 标签页），所有操作都走 [`gh` CLI](https://cli.github.com/)。

> 为什么用 GitHub：这个项目准备开源，别人报的 bug / 想要的功能会直接进 Issues；`to-tickets`、`triage`、`to-spec` 这些 skill 会从这里读取和写入。

## Conventions

- **Create an issue**：`gh issue create --title "..." --body "..."`（多行 body 用 heredoc）
- **Read an issue**：`gh issue view <number> --comments`，需要时用 `jq` 过滤 comments，并同时看 labels
- **List issues**：`gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'`，按需加 `--label` / `--state` 过滤
- **Comment**：`gh issue comment <number> --body "..."`
- **Apply / remove labels**：`gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**：`gh issue close <number> --comment "..."`

repo 从 `git remote -v` 推断；在 clone 目录里运行时 `gh` 会自己处理。

## Pull requests as a triage surface

**PRs as a request surface: no.**（本 repo **不**把外部的 PR 当成 feature request 来源；`/triage` 会读这个 flag。想改就把这行改成 `yes`。）

改成 `yes` 之后，PRs 走和 issues 一样的 labels / states，对应命令是：

- **Read a PR**：`gh pr view <number> --comments`，diff 用 `gh pr diff <number>`
- **List external PRs for triage**：`gh pr list --state open --json number,title,body,labels,author,authorAssociation,comments`，只保留 `authorAssociation` 为 `CONTRIBUTOR` / `FIRST_TIME_CONTRIBUTOR` / `NONE` 的（丢掉 `OWNER` / `MEMBER` / `COLLABORATOR`）
- **Comment / label / close**：`gh pr comment`、`gh pr edit --add-label` / `--remove-label`、`gh pr close`

GitHub 的 issue 和 PR 共用一个编号空间，所以裸 `#42` 可能是两者之一 —— 先用 `gh pr view 42`，失败再退回 `gh issue view 42`。

## When a skill says "publish to the issue tracker"

创建一个 GitHub issue。

## When a skill says "fetch the relevant ticket"

运行 `gh issue view <number> --comments`。

## Wayfinding operations

供 `/wayfinder` 使用。**map** 是单个 issue，**child** issues 是它的 tickets。

- **Map**：一个带 `wayfinder:map` label 的 issue，body 里放 Notes / Decisions-so-far / Fog。`gh issue create --label wayfinder:map`
- **Child ticket**：用 GitHub sub-issue 挂到 map 上（通过 sub-issues endpoint 走 `gh api`）。没开 sub-issues 时，把 child 加进 map body 的 task list，并在 child body 顶部写 `Part of #<map>`。Labels 用 `wayfinder:<type>`（`research` / `prototype` / `grilling` / `task`）。被 claim 之后 assign 给动手的人
- **Blocking**：用 GitHub 原生 issue dependencies。`gh api --method POST repos/<owner>/<repo>/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`，其中 `<blocker-db-id>` 是 blocker 的数字 **database id**（`gh api repos/<owner>/<repo>/issues/<n> --jq .id`，**不是** `#number`，也不是 `node_id`）。GitHub 用 `issue_dependencies_summary.blocked_by` 报告（只算 open 的 blocker = 真正的门）。依赖功能不可用时，退回 child body 顶部的 `Blocked by: #<n>, #<n>` 行；所有 blocker 关闭即 unblocked
- **Frontier query**：列出 map 的 open children（`gh issue list --state open`，限定在 map 的 sub-issues / task list），丢掉有 open blocker 的（`issue_dependencies_summary.blocked_by > 0`，或 `Blocked by` 行里还有 open issue）和已经有 assignee 的；按 map 顺序取第一个
- **Claim**：`gh issue edit <n> --add-assignee @me` —— 这是本轮 session 的第一次写入
- **Resolve**：`gh issue comment <n> --body "<answer>"` → `gh issue close <n>` → 往 map 的 Decisions-so-far 追加一条 context pointer（要点 + 链接）
