# -*- coding: utf-8 -*-
"""Extract readable text from the headless-Chrome DOM dump of a Tencent Doc page."""
import sys
from lxml import html as LH

src = sys.argv[1] if len(sys.argv) > 1 else "dump.html"
out = sys.argv[2] if len(sys.argv) > 2 else "doc_text.txt"

with open(src, "r", encoding="utf-8", errors="replace") as f:
    raw = f.read()

tree = LH.fromstring(raw)

# drop non-content nodes
for tag in ("script", "style", "noscript", "svg", "link", "meta", "head"):
    for el in tree.xpath("//" + tag):
        el.drop_tree()

lines = []
for el in tree.xpath("//text()"):
    s = " ".join(el.split())
    if not s:
        continue
    lines.append(s)

# collapse consecutive duplicates (Tencent Docs often renders shadow copies)
dedup = []
for s in lines:
    if dedup and dedup[-1] == s:
        continue
    dedup.append(s)

with open(out, "w", encoding="utf-8") as f:
    f.write("\n".join(dedup))

print("raw nodes:", len(lines), "deduped:", len(dedup))
print("chars:", sum(len(x) for x in dedup))
