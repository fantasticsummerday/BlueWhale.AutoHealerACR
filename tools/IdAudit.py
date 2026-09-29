"""
ID 与开关的静态核对工具 —— 防止"静默失效"类 bug。

═══════════════════════════════════════════════════════════════════════
  为什么需要它（用户要求："不要再出现这次这种了"）
═══════════════════════════════════════════════════════════════════════

"50 级神兵不读再生"那个 bug 的本质是**静默失效**：
    var 坦克 = HealTargetHelper.血量最低的坦克(0.95f);
    if (坦克 == null) return -1;        // ← 没坦克就永不触发

它**不报错、不崩溃、编译通过**，只能靠人去读代码发现。
而同类问题在别处还有（盾、HoT、各种判断），靠"记得查"是不可靠的。

所以把**能机械验证的部分**做成脚本，每次发版前跑一遍。
本脚本覆盖：

  ① 技能 ID  ←→ 官方 Action.csv    名字必须对得上
  ② 状态 ID  ←→ 官方 Status.csv    名字必须对得上（别名要能解释）
  ③ QT 开关  ←→ 注册默认值 vs 读取默认值
  ④ 「找目标 → 找不到就 return」的坦克门（该模式出过两次 bug）
  ⑤ AuraIds 里值为 0 的条目（用它判断会永远为假）

═══════════════════════════════════════════════════════════════════════
  用法
═══════════════════════════════════════════════════════════════════════

    python tools/IdAudit.py [官方表目录]

  默认官方表目录 = D:\\Download
  缺少官方表时对应检查会跳过（不报错），其余检查照跑。

  [!] 退出码：有问题返回 1，全通过返回 0 —— 可以接进发布流程。
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
TABLES = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else r"D:\Download")

STATUS_CSV = TABLES / "Status.csv"
ACTION_CSV = TABLES / "Action.csv"

问题 = []
信息 = []


def 报(s):
    信息.append(str(s))


def 错(s):
    问题.append(str(s))


def 读表(path):
    """读官方表 -> {id: 名字}"""
    m = {}
    if not path.exists():
        return m
    with path.open("r", encoding="utf-8-sig", errors="replace") as f:
        for line in f:
            if not re.match(r"^\d+,", line):
                continue
            parts = line.split(",", 3)
            if len(parts) < 2:
                continue
            try:
                m[int(parts[0])] = parts[1].strip().strip('"')
            except ValueError:
                pass
    return m


def 是注释(line):
    s = line.strip()
    return s.startswith("//") or s.startswith("///") or s.startswith("*") or s.startswith("/*")


def 规范化(s):
    return re.sub(r"[\s「」『』\"']", "", s)


def 找文件(*相对路径):
    for p in 相对路径:
        f = ROOT / p
        if f.exists():
            return f
    return None


status = 读表(STATUS_CSV)
action = 读表(ACTION_CSV)
报(f"官方表: Status {len(status)} 条 / Action {len(action)} 条")


# ══════════════════════════════════════════════════════════════════
#  ① 技能 ID ←→ Action.csv
# ══════════════════════════════════════════════════════════════════
报("")
报("=== ① SpellIds 技能 ID ===")

f = 找文件(r"HealerACR\Common\SpellIds.cs")
if not f:
    错("找不到 SpellIds.cs")
elif not action:
    报("  跳过（没有 Action.csv）")
else:
    lines = f.read_text(encoding="utf-8").split("\n")
    ok = 0
    别名 = []
    for i, line in enumerate(lines, 1):
        if 是注释(line):
            continue
        m = re.search(r'\["([^"]+)"\]\s*=\s*(\d+)', line)
        if not m:
            continue
        name, id_ = m.group(1), int(m.group(2))
        off = action.get(id_)
        if off is None:
            错(f"  SpellIds.cs:{i}  {name} = {id_}  **不在官方 Action 表里**")
        elif 规范化(off) == 规范化(name):
            ok += 1
        else:
            别名.append((i, name, id_, off))

    报(f"  名字完全一致: {ok} 个")
    if 别名:
        报(f"  名称不同但 ID 存在: {len(别名)} 个（多为语言差异或别名，需人工确认）")
        for i, name, id_, off in 别名[:40]:
            报(f"    L{i}  {name} = {id_}  官方「{off}」")


# ══════════════════════════════════════════════════════════════════
#  ② 状态 ID ←→ Status.csv
# ══════════════════════════════════════════════════════════════════
报("")
报("=== ② AuraIds 状态 ID ===")

f = 找文件(r"HealerACR\Common\AuraIds.cs")
if not f:
    错("找不到 AuraIds.cs")
elif not status:
    报("  跳过（没有 Status.csv）")
else:
    lines = f.read_text(encoding="utf-8").split("\n")
    ok = 0
    别名 = []
    零值 = []
    for i, line in enumerate(lines, 1):
        if 是注释(line):
            continue
        m = re.search(r'取\("([^"]+)"\s*,\s*(\d+)\)', line)
        if not m:
            continue
        name, id_ = m.group(1), int(m.group(2))

        # ⑤ 值为 0 的条目 —— 用它判断会永远为假
        if id_ == 0:
            零值.append((i, name))

        off = status.get(id_) or action.get(id_)
        if off is None:
            错(f"  AuraIds.cs:{i}  {name} = {id_}  **不在官方表里**")
        elif 规范化(off) == 规范化(name):
            ok += 1
        else:
            别名.append((i, name, id_, off))

    报(f"  名字完全一致: {ok} 个")
    if 零值:
        for i, name in 零值:
            错(f"  AuraIds.cs:{i}  {name} = 0  **值为 0，用它做判断永远为假**")
    if 别名:
        报(f"  名称不同但 ID 存在: {len(别名)} 个")
        for i, name, id_, off in 别名[:40]:
            报(f"    L{i}  {name} = {id_}  官方「{off}」")


# ══════════════════════════════════════════════════════════════════
#  ③ QT 开关：注册 vs 读取
# ══════════════════════════════════════════════════════════════════
报("")
报("=== ③ QT 开关 ===")

注册 = {}
读取 = {}
for cs in (ROOT / "HealerACR").rglob("*.cs"):
    if "\\obj\\" in str(cs) or "\\bin\\" in str(cs):
        continue
    try:
        text = cs.read_text(encoding="utf-8")
    except Exception:
        continue
    for line in text.split("\n"):
        if 是注释(line):
            continue
        for m in re.finditer(r'加开关\(\s*"([^"]+)"\s*,\s*(true|false)', line):
            注册[m.group(1)] = m.group(2)
        for m in re.finditer(r'GetQt\(\s*"([^"]+)"\s*(?:,\s*(true|false))?\s*\)', line):
            读取.setdefault(m.group(1), set())
            if m.group(2):
                读取[m.group(1)].add(m.group(2))

报(f"  注册 {len(注册)} 个 / 读取 {len(读取)} 个")
不一致 = 0
for k, v in 注册.items():
    if k in 读取 and 读取[k] and v not in 读取[k]:
        错(f'  开关「{k}」默认值不一致：注册={v} 读取={"/".join(读取[k])}')
        不一致 += 1
if 不一致 == 0:
    报("  默认值全部一致")


# ══════════════════════════════════════════════════════════════════
#  ④ 「坦克门」模式
# ══════════════════════════════════════════════════════════════════
报("")
报("=== ④ 「只找坦克 → 找不到就跳过」模式 ===")
报("    （这个模式出过两次 bug：HoT 和单盾。凡是坦克专属技能属正常，")
报("      但通用技能（护盾/HoT/治疗）走这条路就会在无坦克场景静默失效）")

pattern = re.compile(r"血量最低的坦克\([^)]*\)")
for cs in (ROOT / "HealerACR").rglob("*.cs"):
    if "\\obj\\" in str(cs) or "\\bin\\" in str(cs):
        continue
    try:
        lines = cs.read_text(encoding="utf-8").split("\n")
    except Exception:
        continue
    for i, line in enumerate(lines, 1):
        if 是注释(line) or not pattern.search(line):
            continue
        # 往后看 3 行有没有 return -1 / return（= 找不到就整个不触发）
        tail = "\n".join(lines[i - 1:i + 3])
        if re.search(r"== null\)\s*return", tail):
            报(f"  {cs.name}:{i}  {line.strip()}")
            报(f"      -> 需确认：这是坦克专属技能（正常），还是通用技能（会静默失效）？")


# ══════════════════════════════════════════════════════════════════
#  ⑤ 技能 ID 当 buff ID 用（会静默失效）
# ══════════════════════════════════════════════════════════════════
报("")
报("=== ⑤ 技能 ID vs buff ID（技能转Buff 映射的缺口）===")
报("    技能 ID 和 buff ID 常常不同。拿技能 ID 去查 buff")
报("    （HasAura(技能ID)）会永远 false —— 静默失效。")
报("    实例：吉星相位技能 3595，buff 是 835；")
报("          3595 在官方表里其实是「般若汤」(食物)。")
报("          贤者诊断技能 24284，buff 是 2607（24284 根本不是状态）。")

if status:
    f = 找文件(r"HealerACR\Common\AuraIds.cs")
    if f:
        text = f.read_text(encoding="utf-8")
        blk = re.search(r"技能转Buff\(uint 技能Id\).*?switch\s*\{(.*?)\};", text, re.S)
        mapped = set()
        if blk:
            for a, _b in re.findall(r"(\d+)\s*=>\s*(\w+)", blk.group(1)):
                mapped.add(int(a))

        # 这些技能会被 有该技能的Buff / HasAura 查，必须登记
        必须登记 = {
            "再生": 137, "吉星相位": 3595, "鼓舞激励之策": 185,
            "诊断": 24284, "均衡预后": 24286, "水流幕": 25861,
            "生命回生法": 25867, "天星交错": 16556, "秘策": 16542,
            "活化": 24300, "混合": 24317, "无中生有": 7430,
            "星位合图": 3612, "拯救": 24294, "天宫图": 16557,
        }
        缺口 = []
        for 名, id_ in sorted(必须登记.items()):
            if id_ in mapped:
                continue
            # 技能 ID 恰好等于官方表里同名状态 → 安全
            if 规范化(status.get(id_, "")) == 规范化(名):
                continue
            缺口.append((名, id_))

        if 缺口:
            for 名, id_ in 缺口:
                报("    [!] " + 名 + " = " + str(id_) + " 不在 技能转Buff 映射表里")
                off = status.get(id_)
                if off:
                    报("        -> 官方表里 " + str(id_) + " 是「" + off + "」，不是「" + 名 + "」")
                    报("        -> HasAura(" + str(id_) + ") 查到的是别的东西，判断会失效")
                else:
                    报("        -> " + str(id_) + " 不是状态 ID，HasAura 恒为 false")
        else:
            报("    全部已登记")


# ══════════════════════════════════════════════════════════════════
#  ⑥ 「用返回值表达优先级」（无效代码）
# ══════════════════════════════════════════════════════════════════
报("")
报("=== ⑥ 用返回值表达优先级的写法 ===")
报("    反汇编 AEAssist 实测（PVE_RunSlotHelper+<CheckNext>d__4::MoveNext）：")
报("        IL_0013 callvirt ISlotResolver::Check")
报("        IL_0066 ldc.i4.0 ; IL_0067 blt   <- 只看 < 0，无比大小的指令")
报("        IL_0054 AppendFormatted<int>      <- 分值只进日志")
报("    而 <RunSlotResolvers>d__2 是 foreach 顺序试、第一个 >=0 的就 leave 退出。")
报("    => 优先级 = 在决策队列里的行号，分值不参与仲裁。")
报("    => 想表达让路必须 return -1，写小分值无效。")
报("    （官方指南写的 higher priority wins 是误导性的。）")

_hits6 = 0
for cs in (ROOT / "HealerACR").rglob("*.cs"):
    if "\\obj\\" in str(cs) or "\\bin\\" in str(cs):
        continue
    try:
        lines = cs.read_text(encoding="utf-8").split("\n")
    except Exception:
        continue
    for i, line in enumerate(lines, 1):
        if 是注释(line):
            continue
        if not re.search(r"return \d+;", line):
            continue
        ctx = "\n".join(lines[max(0, i - 5):i])
        if re.search(r"让路|让位|高于|提前于", ctx):
            报("    " + cs.name + ":" + str(i) + "  " + line.strip())
            报("        -> 上方注释提到让路/高于，但返回正数 = 仍会抢先；要真让路得 return -1")
            _hits6 += 1
if _hits6 == 0:
    报("    没有发现这类写法")


# ══════════════════════════════════════════════════════════════════
#  ⑦ 工具脚本自身的 GBK 安全
# ══════════════════════════════════════════════════════════════════
报("")
报("=== ⑦ 工具脚本的 GBK 安全 ===")
报("    中文 Windows 控制台是 GBK。脚本里出现 emoji 之类")
报("    GBK 编不了的字符，**一 print 就 UnicodeEncodeError 崩掉**。")
报("    这个坑踩过两次（IdAudit 和 PromptBudget 各一次），所以机械检查。")

_TOOLS = ROOT / "tools"
_bad_tools = []
for _p in _TOOLS.rglob("*.py"):
    if "\\obj\\" in str(_p) or "\\bin\\" in str(_p):
        continue
    try:
        _t = _p.read_text(encoding="utf-8")
    except Exception:
        continue
    _bad = set()
    for _ch in _t:
        try:
            _ch.encode("gbk")
        except UnicodeEncodeError:
            _bad.add(hex(ord(_ch)))
    if _bad:
        _bad_tools.append((_p.name, sorted(_bad)))

if _bad_tools:
    for _n, _b in _bad_tools:
        错("    " + _n + " 含非 GBK 字符 " + str(_b) + "，在中文控制台会崩")
else:
    报("    所有 .py 工具都是 GBK 安全的")


# ══════════════════════════════════════════════════════════════════
#  输出
# ══════════════════════════════════════════════════════════════════
print("\n".join(信息))

if 问题:
    print("")
    print("=" * 60)
    print(f"发现 {len(问题)} 个问题：")
    print("=" * 60)
    for p in 问题:
        print(p)
    print("")
    print("ID/开关核对未通过 —— 修好再发版。")
    sys.exit(1)

print("")
print("ID/开关核对全部通过。")
sys.exit(0)
