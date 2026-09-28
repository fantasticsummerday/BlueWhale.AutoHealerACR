# Git 工作流 —— 这个项目的用法

## 当前状态

```
仓库:   C:\Users\MECHREVO\Documents\deepseek-harness\default-workspace
分支:   master
文件:   75 个（只跟踪源代码）
大小:   .git 0.97 MB（工作区 193 MB）
tag:    v1.0-基线
```

## 日常命令

### 看当前状态

```powershell
git status              # 哪些文件改了
git diff                # 逐行看改了什么（最重要）
git diff --stat         # 只看统计：哪几个文件、各改了多少行
```

### 存一个检查点

```powershell
git add -A
git commit -m "说明这次改了什么"
```

**改动小的时候可以合并成一条**：
```powershell
git commit -am "修了 DoT 补判窗口"
```
（`-a` 会自动包含已跟踪文件的修改，但**新文件还是要先 `git add`**）

### 改坏了，退回去

```powershell
# 还没 commit —— 丢弃所有未提交的改动
git checkout -- .

# 丢弃单个文件的改动
git checkout -- HealerACR/Common/HealerACR.cs

# 已经 commit 了 —— 看历史找回来
git log --oneline
git revert <commit>            # 做一个"反向提交"（保留历史，安全）
git reset --hard <commit>      # 直接回到某个提交（会丢后面的历史）
```

### 对比两个版本

```powershell
git log --oneline                     # 列出所有提交
git show <commit>                     # 看某个提交改了什么
git diff <commit1> <commit2> -- 路径   # 对比两个提交
git diff v1.0-基线 -- HealerACR/       # 对比 tag 和当前
```

## 已经配好的全局设置

```powershell
git config --global user.name  "ACR Dev"
git config --global user.email "acr@localhost"
git config --global core.quotepath false    # 中文文件名正常显示
git config --global core.autocrlf false     # 不自动转换换行符
```

**想改成自己的名字**：
```powershell
git config --global user.name "你的名字"
```

## 什么被排除了

```
bin/  obj/  build/          编译产物
release/                    发布包（每个几十 MB）
localnuget/  ref/           本地依赖和参考 dll
.snapshots/                 旧的快照工具目录
dump*.tsv                   游戏数据导出（能用工具重新生成）
*.log  *.txt(部分)           日志和临时文件
doc*.json  probe.*  ...     文档抓取的一次性脚本
```

**判断标准**：**能不能用代码重新生成？** 能 → 不跟踪。

## 建议的提交节奏

| 时机 | 做法 |
|---|---|
| **改之前** | 如果改动很大（批量替换、跨文件重构），先 `git commit` 存一个 |
| **改完能编译** | `git commit -m "..."` |
| **发版** | `git commit` + `git tag -a v1.6.0 -m "说明"` |
| **实验性改动** | 先 commit，改坏了 `git checkout -- .` 一秒还原 |

## 和 snapshot.ps1 的关系

**建议保留 `.snapshots\` 目录**（已经删掉跟踪了，但本地文件还在）。

- **git**：主要用，逐行 diff + 历史
- **snapshot.ps1**：不用了，但留着也无害

**想删掉**：
```powershell
Remove-Item .snapshots -Recurse -Force
```
**历史已经在 git 里了，删了不丢东西。**

## 这个项目最初为什么没版本控制

**这台机器上原来没装 git** —— 所以写了 `snapshot.ps1` 顶着（全量复制 + 手动回滚）。

**结果那段时间误改了 4 次**（删方法签名 ×2、插错位置 ×1、删变量 ×1），**每次都是手工比对恢复的**。

**现在有 git 了，这类问题应该不会再浪费那么多时间。**

---

## 常用组合（我干活时的）

```powershell
# 改大东西之前的保险
git commit -am "改动前的状态" --allow-empty

# 改完检查
git diff --stat        # 确认真的改了该改的文件
dotnet build ...       # 编译

# 编译过了再提交
git commit -am "做了什么"

# 中途想放弃
git checkout -- .      # 一秒回到上次提交
```
