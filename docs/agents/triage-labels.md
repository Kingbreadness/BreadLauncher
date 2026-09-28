# Triage Labels

Skills 使用五个 canonical triage roles。这个文件把这些 roles 映射到此 repo issue tracker 中实际使用的 label 字符串。

本 repo 用**默认** vocabulary（没有改名）：这五个字符串就是 **GitHub issue 上的 label 名字**，直接用 `gh issue edit <n> --add-label "..."` 打。这五个 label **已经在仓库里建好了**（2026-09-27，`gh label list` 可复查），不用再建，也**不要**指望"第一次用到时 GitHub 会自动创建"——不会。

| Label in mattpocock/skills | Label in our tracker | Meaning                                  |
| -------------------------- | -------------------- | ---------------------------------------- |
| `needs-triage`             | `needs-triage`       | Maintainer needs to evaluate this issue  |
| `needs-info`               | `needs-info`         | Waiting on reporter for more information |
| `ready-for-agent`          | `ready-for-agent`    | Fully specified, ready for an AFK agent  |
| `ready-for-human`          | `ready-for-human`    | Requires human implementation            |
| `wontfix`                  | `wontfix`            | Will not be actioned                     |

当某个 skill 提到 role（例如 “apply the AFK-ready triage label”）时，使用此表中对应的 label 字符串。

本 repo **就用默认值**，所以上表右边那一列 = 左边那一列，**不需要编辑**。（哪天要换名字，比如改叫 `bug:triage`，才来改这右列，并且要在 GitHub 上把新 label 建好。）
