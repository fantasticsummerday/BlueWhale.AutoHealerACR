# -*- coding: utf-8 -*-
"""Inspect the structure of the Tencent Doc opendoc JSON."""
import json, sys

with open("doc.json", "r", encoding="utf-8") as f:
    d = json.load(f)

def walk(o, path="", depth=0, maxdepth=4, out=None):
    if out is None:
        out = []
    if depth > maxdepth:
        return out
    if isinstance(o, dict):
        for k, v in o.items():
            p = f"{path}.{k}"
            if isinstance(v, (dict, list)):
                out.append((p, type(v).__name__, len(v)))
                walk(v, p, depth + 1, maxdepth, out)
            else:
                s = str(v)
                out.append((p, type(v).__name__, len(s)))
    elif isinstance(o, list):
        for i, v in enumerate(o[:3]):
            p = f"{path}[{i}]"
            if isinstance(v, (dict, list)):
                out.append((p, type(v).__name__, len(v)))
                walk(v, p, depth + 1, maxdepth, out)
            else:
                out.append((p, type(v).__name__, len(str(v))))
    return out

lines = walk(d, "root", 0, 3)
for p, t, n in lines:
    if n and (n > 200 or t in ("dict", "list")):
        print(f"{p}  <{t}> len={n}")
