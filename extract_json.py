# -*- coding: utf-8 -*-
"""Flatten the Tencent Doc keyframe mutations into plain readable text."""
import json, re

with open("doc.json", "r", encoding="utf-8") as f:
    d = json.load(f)

mutations = d["clientVars"]["collab_client_vars"]["initialAttributedText"]["text"][0]["commands"][0]["mutations"]
print("mutations:", len(mutations))
print("types:", sorted({m.get("ty") for m in mutations}))

parts = []
for m in mutations:
    ty = m.get("ty")
    if ty == "is":
        parts.append(m.get("s", ""))
    elif ty == "d":
        pass  # deletion, ignore
    else:
        parts.append("")

raw = "".join(parts)
print("raw length:", len(raw))

# Word-style field codes: \x13 <instruction> \x14 <display> \x15
out = []
i = 0
n = len(raw)
while i < n:
    ch = raw[i]
    if ch == "\x13":
        # skip instruction up to \x14, then keep display text
        j = raw.find("\x14", i)
        if j == -1:
            i += 1
            continue
        i = j + 1
        continue
    if ch in ("\x14", "\x15"):
        i += 1
        continue
    out.append(ch)
    i += 1

text = "".join(out)

# normalise control chars
text = text.replace("\r\n", "\n").replace("\r", "\n").replace("\x0b", "\n")
text = re.sub(r"[\x00-\x08\x0c\x0e-\x1f]", "", text)

with open("doc_clean.txt", "w", encoding="utf-8") as f:
    f.write(text)
print("clean length:", len(text))
