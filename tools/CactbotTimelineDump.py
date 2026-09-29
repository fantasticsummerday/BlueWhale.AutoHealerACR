"""从 cactbot 的 raidboss_data.bundle.js 提取全部时间轴。

═══════════════════════════════════════════════════════════════════
  为什么需要这个工具
═══════════════════════════════════════════════════════════════════

cactbot v3+ 把时间轴**编译进了 webpack 产物**，不再有独立的 .txt 目录：

    <ACT>\\Plugins\\cactbot-offline\\ui\\common\\raidboss_data.bundle.js   (13 MB)

而它**官方支持**的 user\\raidboss 目录默认是空的。
所以想用 cactbot 的时间轴，只能从 bundle 里提取 —— 这就是本工具做的事。

═══════════════════════════════════════════════════════════════════
  数据在 bundle 里怎么组织（实测确认，不是猜的）
═══════════════════════════════════════════════════════════════════

每个副本有**两个**模块：

  ① trigger set 模块   ./ui/raidboss/data/02-arr/raid/t11.ts
         const t11_triggerSet = {
           zoneId: zone_id/* default.TheFinalCoilOfBahamutTurn2 */.Z.TheFinalCoilOfBahamutTurn2,
           timelineFile: 't11.txt',        ← ★ 连到时间轴
           ...

  ② 时间轴模块         ./ui/raidboss/data/02-arr/raid/t11.txt
         const raid_t11_namespaceObject = "# Turn 11\\r\\n..."

**关键**：时间轴模块里**没有 zoneId**，必须靠 ① 的 `timelineFile` 连起来。

而 zoneId 是**符号名**，数值表在另一个 bundle 里：

    <ACT>\\Plugins\\cactbot-offline\\ui\\raidboss\\raidboss.bundle.js
        'MiddleLaNoscea': 134,
        'Zadnor': 975

═══════════════════════════════════════════════════════════════════
  为什么要补 ZoneId 头
═══════════════════════════════════════════════════════════════════

cactbot 有两种年代的时间轴：
  · 新格式：文件里写了 `# ZoneId: N`
  · 旧格式：**没有**，靠 trigger set 的 zoneId 匹配地图

实测 310 份里只有 54 份自带。而 ACR 的解析器（CactbotTimelineParser）
**只认 `# ZoneId:`** —— 不补的话 256 份用不了。

═══════════════════════════════════════════════════════════════════
  授权
═══════════════════════════════════════════════════════════════════

cactbot 是 **Apache-2.0** 开源项目（https://github.com/OverlayPlugin/cactbot）。
时间轴是它的公开数据，提取自用没问题 —— 但**分发时要保留出处**。
本工具生成的 `_manifest.json` 里记录了每份的来源路径。

═══════════════════════════════════════════════════════════════════
  用法
═══════════════════════════════════════════════════════════════════

    python tools/CactbotTimelineDump.py [cactbot目录] [输出目录]

默认：
    cactbot目录 = D:\\FF14\\ACT\\Plugins\\cactbot-offline
    输出目录   = 当前目录下的 cactbot_timelines

提取完把输出目录整个拷到 ACR 的 Timelines 下即可：
    D:\\FF14\\ACR\\BlueWhale\\Timelines\\cactbot\\
"""

import json
import pathlib
import re
import sys

DEFAULT_CACTBOT = r"D:\FF14\ACT\Plugins\cactbot-offline"
DEFAULT_OUT = "cactbot_timelines"


def js_string_at(s: str, i: int):
    """从 s[i]（应为引号）解析一个 JS 字符串字面量 -> (内容, 结束位置)。

    ⚠️ 必须正确反转义 —— 时间轴正文在 bundle 里是 `\\r\\n` 形式的转义序列，
       直接拿原始文本会得到一坨字面的 \\r\\n。
    """
    quote = s[i]
    if quote not in "\"'":
        return None, i
    i += 1
    buf, n = [], len(s)
    simple = {"n": "\n", "r": "\r", "t": "\t", '"': '"', "'": "'",
              "\\": "\\", "0": "\0", "b": "\b", "f": "\f", "v": "\v"}
    while i < n:
        ch = s[i]
        if ch == "\\":
            if i + 1 >= n:
                break
            nxt = s[i + 1]
            if nxt in simple:
                buf.append(simple[nxt]); i += 2; continue
            if nxt == "u":
                try:
                    buf.append(chr(int(s[i + 2:i + 6], 16))); i += 6; continue
                except ValueError:
                    pass
            if nxt == "x":
                try:
                    buf.append(chr(int(s[i + 2:i + 4], 16))); i += 4; continue
                except ValueError:
                    pass
            if nxt == "\n":
                i += 2; continue
            buf.append(nxt); i += 2; continue
        if ch == quote:
            return "".join(buf), i + 1
        buf.append(ch); i += 1
    return "".join(buf), i


def looks_like_timeline(s: str) -> bool:
    """判断一段字符串是不是时间轴。

    ⚠️ 不能只看 `ZoneId` —— **旧格式时间轴没有那个头**（实测 t10.txt 就是）。
       所以用结构特征：ZoneId 头 / hideall 指令 / `数字.数字 "..."` 行。
    """
    if not s or len(s) < 40:
        return False
    if "ZoneId" in s or "hideall" in s:
        return True
    return bool(re.search(r'^\s*\d+\.\d+\s+"', s, re.M))


def main() -> int:
    cactbot = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else DEFAULT_CACTBOT)
    out_dir = pathlib.Path(sys.argv[2] if len(sys.argv) > 2 else DEFAULT_OUT)

    data_bundle = cactbot / "ui" / "common" / "raidboss_data.bundle.js"
    zone_bundle = cactbot / "ui" / "raidboss" / "raidboss.bundle.js"

    for p in (data_bundle, zone_bundle):
        if not p.exists():
            print(f"找不到: {p}")
            print("（cactbot 版本不同的话，路径可能变了 —— 找一下 raidboss_data.bundle.js）")
            return 1

    text = data_bundle.read_text(encoding="utf-8", errors="replace")
    rb = zone_bundle.read_text(encoding="utf-8", errors="replace")
    print(f"数据 bundle: {len(text):,} 字符")

    # ── ① zoneId 符号名 -> 数值 ──
    #
    # ⚠️ 结尾逗号写成 `,?` —— 表的**最后一项**后面是 `\n};` 而不是逗号。
    #    实测 `'Zadnor': 975` 正好是最后一项，写成 `,` 就漏了它。
    zone_map = {}
    for m in re.finditer(r"'([A-Za-z0-9_]+)'\s*:\s*(\d+)\s*,?", rb):
        name, num = m.group(1), int(m.group(2))
        if len(name) > 3 and num > 0:
            zone_map.setdefault(name, num)
    print(f"zoneId 符号表: {len(zone_map)} 条")

    # ── ② 切模块 ──
    mark = ";// CONCATENATED MODULE: "
    markers = [(m.start(), m.group(1))
               for m in re.finditer(re.escape(mark) + r"(\./[^\n]+)", text)]
    chunks = []
    for k, (pos, rel) in enumerate(markers):
        end = markers[k + 1][0] if k + 1 < len(markers) else len(text)
        chunks.append((rel.strip(), text[pos:end]))
    print(f"模块数: {len(chunks)}")

    # ── ③ trigger set: timelineFile -> zoneId ──
    file_to_zone = {}
    for rel, chunk in chunks:
        if not rel.endswith(".ts"):
            continue
        tf = re.search(r"timelineFile:\s*'([^']+)'", chunk)
        if not tf:
            continue
        # ⚠️ 原文形如  zoneId: zone_id/* default.Zadnor */.Z.Zadnor,
        #    `zone_id` 和 `.Z.` 之间夹着**注释** ——
        #    不能用 `zone_id\w*\.Z\.`（那样一条都匹配不到）。
        zm = re.search(r"zoneId:\s*(\w+)[^,]*\.Z\.(\w+)", chunk)
        sym = zm.group(2) if zm else None
        zid = zone_map.get(sym) if sym else None
        if zid:
            file_to_zone[tf.group(1)] = (zid, sym)
    print(f"timelineFile -> zoneId 映射: {len(file_to_zone)} 条")

    # ── ④ 提取 + 补 ZoneId ──
    out_dir.mkdir(parents=True, exist_ok=True)
    stats = {"原样": 0, "补上": 0, "无法确定": 0}
    manifest, skipped = [], []

    for rel, chunk in chunks:
        if not rel.endswith(".txt") or rel.endswith("raidboss_manifest.txt"):
            continue

        m = re.search(r'_namespaceObject\s*=\s*(?=["\'])', chunk)
        if not m:
            m = re.search(r'=\s*(?=["\'])', chunk)
        if not m:
            skipped.append((rel, "找不到赋值"))
            continue

        content, _ = js_string_at(chunk, m.end())
        if not looks_like_timeline(content):
            skipped.append((rel, "不像时间轴"))
            continue

        body = content.replace("\r\n", "\n").replace("\r", "\n").rstrip("\n") + "\n"
        filename = pathlib.PurePosixPath(rel).name

        has_zone = bool(re.search(r"^#\s*ZoneId\s*:\s*\d+", body, re.M))
        zone_id, zone_sym = None, None

        if has_zone:
            mm = re.search(r"^#\s*ZoneId\s*:\s*(\d+)", body, re.M)
            zone_id = int(mm.group(1)) if mm else None
            stats["原样"] += 1
        else:
            hit = file_to_zone.get(filename)
            if hit:
                zone_id, zone_sym = hit
            if zone_id:
                lines = body.split("\n")
                at = 1
                for i, l in enumerate(lines[:6]):
                    if l.startswith("###"):
                        at = i + 1
                        break
                lines.insert(at, f"# ZoneId: {zone_id}")
                body = "\n".join(lines)
                stats["补上"] += 1
            else:
                stats["无法确定"] += 1

        out_rel = rel
        for prefix in ("./ui/raidboss/data/", "./ui/raidboss/"):
            if out_rel.startswith(prefix):
                out_rel = out_rel[len(prefix):]
                break
        out_rel = out_rel.lstrip("./")

        dst = out_dir / out_rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        dst.write_text(body, encoding="utf-8", newline="\n")

        manifest.append({
            "file": out_rel,
            "zoneId": zone_id,
            "zoneSymbol": zone_sym,
            "自带ZoneId": has_zone,
            "来源": rel,
        })

    (out_dir / "_manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=1), encoding="utf-8")

    print()
    print("=== 统计 ===")
    for k, v in stats.items():
        print(f"  {k}: {v}")
    for p, why in skipped:
        print(f"  跳过 {p}  ({why})")
    print(f"  写出 {len(manifest)} 个文件 -> {out_dir}")

    (out_dir / "授权说明.txt").write_text(
        "本目录下的时间轴提取自 cactbot。\n"
        "\n"
        "cactbot 是 Apache-2.0 开源项目：https://github.com/OverlayPlugin/cactbot\n"
        "提取来源：<ACT>\\Plugins\\cactbot-offline\\ui\\common\\raidboss_data.bundle.js\n"
        "每份文件的原始路径记录在 _manifest.json 的「来源」字段。\n"
        "\n"
        "提取工具：tools/CactbotTimelineDump.py\n"
        "cactbot 更新后重新跑一次即可。\n"
        "\n"
        "注意：提取时补写了 `# ZoneId:` 头（cactbot 的旧格式时间轴没有它），\n"
        "      ACR 的解析器依赖这个头来匹配地图。\n",
        encoding="utf-8")

    return 0


if __name__ == "__main__":
    sys.exit(main())
