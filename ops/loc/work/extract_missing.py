import json
from pathlib import Path

p = Path(r"ops/loc/work/ko-KR/all-missing.json")
d = json.loads(p.read_text(encoding="utf-8"))
rows = d["rows"]
print("rows", len(rows))
print("skip", sum(1 for r in rows if r.get("keep_english") or r.get("status") == "skip"))
todo = [r for r in rows if not r.get("keep_english") and r.get("status") != "skip"]
print("todo", len(todo))
out = Path(r"ops/loc/work/all-missing.tsv")
with out.open("w", encoding="utf-8") as f:
    for r in todo:
        en = r["english"].replace("\t", " ").replace("\n", " ")
        f.write(f"{r['key']}\t{r['icu']}\t{en}\n")
print("wrote", out)
