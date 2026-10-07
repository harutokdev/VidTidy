"""Lists the Japanese display text that needs a translation, and reports what en.json lacks.

  python tools/i18n/extract.py                 # counts and missing entries
  python tools/i18n/extract.py --missing out.json   # writes the untranslated texts as a JSON list
The same rules are checked by LocalizationCatalogTests, so a missing translation fails the tests.
"""
import html
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SRC = os.path.join(ROOT, "src")
TABLE = os.path.join(SRC, "LocalMediaAI.Core", "Localization", "en.json")
CS_CALL = re.compile(r'\bL\.(?:T|F)\(\s*"((?:[^"\\\n]|\\.)*)"')
XAML_T = re.compile(r"\{l:T '((?:[^'\\]|\\.)*)'\}")
ESCAPES = {"n": "\n", "r": "\r", "t": "\t", "\\": "\\", '"': '"', "'": "'", "0": "\0"}


def cs_unescape(body: str) -> str:
    return re.sub(r"\\(u[0-9a-fA-F]{4}|.)", lambda m: chr(int(m.group(1)[1:], 16)) if m.group(1).startswith("u") else ESCAPES.get(m.group(1), m.group(1)), body)


def tformat_values(xml: str):
    for m in re.finditer(r"TFormat=", xml):
        i = m.end()
        if xml.startswith("{}", i):
            i += 2
        depth, start = 0, i
        while i < len(xml):
            c = xml[i]
            if c == "{":
                depth += 1
            elif c == "}":
                if depth == 0:
                    break
                depth -= 1
            elif c in ',"' and depth == 0:
                break
            i += 1
        yield html.unescape(xml[start:i])


def keys() -> set:
    found = set()
    for root, dirs, names in os.walk(SRC):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for name in names:
            path = os.path.join(root, name)
            if name.endswith(".cs"):
                text = open(path, encoding="utf-8-sig").read()
                found.update(cs_unescape(m.group(1)) for m in CS_CALL.finditer(text))
            elif name.endswith(".xaml"):
                text = open(path, encoding="utf-8-sig").read()
                found.update(html.unescape(re.sub(r"\\(.)", r"\1", m.group(1))) for m in XAML_T.finditer(text))
                found.update(tformat_values(text))
    return found


def main(argv) -> None:
    table = json.load(open(TABLE, encoding="utf-8")) if os.path.exists(TABLE) else {}
    all_keys = keys()
    missing = sorted(k for k in all_keys if k not in table)
    unused = sorted(k for k in table if k not in all_keys)
    print(f"texts: {len(all_keys)}  translated: {len(all_keys) - len(missing)}  missing: {len(missing)}  unused: {len(unused)}")
    if len(argv) >= 2 and argv[0] == "--missing":
        json.dump(missing, open(argv[1], "w", encoding="utf-8"), ensure_ascii=False, indent=0)


if __name__ == "__main__":
    main(sys.argv[1:])
