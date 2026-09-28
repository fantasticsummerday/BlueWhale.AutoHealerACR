# -*- coding: utf-8 -*-
"""Same regexes as CactbotTimelineParser.cs, run over the real cactbot file.
This is a stand-in test: C# can't be compiled here, so verify the parsing logic in Python."""
import re, sys, collections

ZONE = re.compile(r"^\s*#\s*ZoneId\s*:\s*(\d+)", re.M)
LABEL = re.compile(r'^(?P<t>[0-9]+(?:\.[0-9]+)?)\s+label\s+"(?P<name>[^"]*)"')
MAIN = re.compile(r'^(?P<t>[0-9]+(?:\.[0-9]+)?)\s+"(?P<name>[^"]*)"\s+(?P<type>[A-Za-z]+)\s*(?:\{(?P<args>[^}]*)\})?\s*(?P<rest>.*)$')
ID = re.compile(r'id\s*:\s*(?:"(?P<one>[0-9A-Fa-f]+)"|\[(?P<many>[^\]]*)\])')
SRC = re.compile(r'source\s*:\s*"([^"]*)"')
WIN = re.compile(r'window\s+([0-9]+(?:\.[0-9]+)?)\s*,\s*([0-9]+(?:\.[0-9]+)?)')
DUR = re.compile(r'duration\s+([0-9]+(?:\.[0-9]+)?)')
JUMP = re.compile(r'(?:force)?jump\s+"?([^"\s]+)"?')

path = sys.argv[1]
text = open(path, encoding="utf-8").read()

m = ZONE.search(text)
print("ZoneId:", m.group(1) if m else "NOT FOUND")

items = []
for raw in text.split("\n"):
    line = raw.strip().rstrip("\r")
    if not line or line.startswith("#"):
        continue
    if line.lower().startswith("hideall") or line.lower().startswith("window"):
        continue

    lm = LABEL.match(line)
    if lm:
        items.append(dict(t=float(lm.group("t")), name=lm.group("name"), type="label", ids=[],
                          src="", win=None, dur=None, jump=None, label=True))
        continue

    mm = MAIN.match(line)
    if not mm:
        continue

    ids = []
    args = mm.group("args") or ""
    if args:
        im = ID.search(args)
        if im:
            if im.group("one"):
                ids.append(int(im.group("one"), 16))
            elif im.group("many"):
                for piece in im.group("many").split(","):
                    piece = piece.strip().strip('"')
                    if piece:
                        try:
                            ids.append(int(piece, 16))
                        except ValueError:
                            pass

    items.append(dict(
        t=float(mm.group("t")),
        name=mm.group("name"),
        type=mm.group("type"),
        ids=ids,
        src=(SRC.search(args).group(1) if SRC.search(args) else ""),
        win=(WIN.search(mm.group("rest") or "").group(1) if WIN.search(mm.group("rest") or "") else None),
        dur=(DUR.search(mm.group("rest") or "").group(1) if DUR.search(mm.group("rest") or "") else None),
        jump=(JUMP.search(mm.group("rest") or "").group(1) if JUMP.search(mm.group("rest") or "") else None),
        label=False,
    ))

items.sort(key=lambda x: x["t"])

print("total lines parsed:", len(items))
print("types:", dict(collections.Counter(i["type"] for i in items)))
print("labels:", sum(1 for i in items if i["label"]))
print("with jump:", sum(1 for i in items if i["jump"]))
print("with window:", sum(1 for i in items if i["win"]))
print("with duration:", sum(1 for i in items if i["dur"]))
print("no id (won't trigger):", sum(1 for i in items if not i["ids"] and not i["label"]))

su = [i for i in items if i["type"] == "StartsUsing" and i["ids"]]
print("\nStartsUsing with id:", len(su))
for i in su[:12]:
    print(f"  {i['t']:8.1f}s  {', '.join(hex(x) for x in i['ids']):20} {i['name']}  src={i['src']}")

print("\nfirst 8 overall:")
for i in items[:8]:
    print("  ", i["t"], i["type"], i["name"], [hex(x) for x in i["ids"]])
