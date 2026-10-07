"""Wraps Japanese display text in C# sources with the translator.

  "日本語"            ->  L.T("日本語")
  $"…{a}…{b:N0}…"     ->  L.F("…{0}…{1:N0}…", a, b)

Run once over a project; it is idempotent (text already inside L.T(/L.F( is left alone). Lines carrying the marker
comment `i18n: keep` (and files listed in SKIP_FILES) are not touched: model prompts, persisted identifiers, logs.
Usage: python tools/i18n/wrap_csharp.py <file-or-dir>...
"""
import os
import re
import sys

JP = re.compile(r"[\u3000-\u303f\u3040-\u30ff\u3400-\u9fff\uff01-\uff60]")
SKIP_FILES = {"Tagging.cs", "L.cs"}
KEEP_MARK = "i18n: keep"


class Lit:
    """A string literal found in source: [start, end) span, kind and raw body."""

    def __init__(self, start, end, interpolated, verbatim, body, holes):
        self.start, self.end = start, end
        self.interpolated, self.verbatim = interpolated, verbatim
        self.body = body          # text between the quotes (interpolation holes kept as written)
        self.holes = holes        # for interpolated: list of (start, end) of each {…} hole inside body, relative to body


def scan(src):
    """Yields top-level string literals, skipping comments and char literals. Nested literals inside interpolation
    holes are part of their outer literal (handled by recursion on the hole text)."""
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        if src.startswith("//", i):
            j = src.find("\n", i)
            i = n if j < 0 else j
            continue
        if src.startswith("/*", i):
            j = src.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        if c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == "\\" else 1
            i = j + 1
            continue
        m = re.match(r'(\$@|@\$|\$|@)?"', src[i:i + 3])
        if m and not src.startswith('"""', i + len(m.group(1) or "")):
            prefix = m.group(1) or ""
            lit, end = read_literal(src, i, prefix)
            yield lit
            i = end
            continue
        i += 1


def read_literal(src, start, prefix):
    interpolated = "$" in prefix
    verbatim = "@" in prefix
    i = start + len(prefix) + 1
    body_start = i
    holes = []
    n = len(src)
    while i < n:
        c = src[i]
        if verbatim and c == '"' and i + 1 < n and src[i + 1] == '"':
            i += 2
            continue
        if not verbatim and c == "\\":
            i += 2
            continue
        if c == '"':
            break
        if interpolated and c == "{":
            if i + 1 < n and src[i + 1] == "{":
                i += 2
                continue
            hole_start = i
            i = skip_hole(src, i + 1)
            holes.append((hole_start - body_start, i - body_start))
            continue
        if interpolated and c == "}" and i + 1 < n and src[i + 1] == "}":
            i += 2
            continue
        i += 1
    return Lit(start, i + 1, interpolated, verbatim, src[body_start:i], holes), i + 1


def skip_hole(src, i):
    """Returns the index just past the '}' closing a hole that starts at i (after '{')."""
    depth = 0
    n = len(src)
    while i < n:
        c = src[i]
        m = re.match(r'(\$@|@\$|\$|@)?"', src[i:i + 3])
        if m:
            _, i = read_literal(src, i, m.group(1) or "")
            continue
        if c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == "\\" else 1
            i = j + 1
            continue
        if c in "([{":
            depth += 1
        elif c in ")]":
            depth -= 1
        elif c == "}":
            if depth == 0:
                return i + 1
            depth -= 1
        i += 1
    return n


def split_hole(hole):
    """'{expr,align:fmt}' -> (expr, ',align:fmt' suffix). Splits at the first top-level ',' or ':' outside
    parentheses/brackets/strings; a ':' that belongs to a ?: inside parentheses is not top level."""
    inner = hole[1:-1]
    depth = 0
    i = 0
    while i < len(inner):
        c = inner[i]
        m = re.match(r'(\$@|@\$|\$|@)?"', inner[i:i + 3])
        if m:
            _, i = read_literal(inner, i, m.group(1) or "")
            continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif depth == 0 and c in ",:" and not inner.startswith("::", i):
            if c == ":" and "?" in inner[:i]:
                i += 1
                continue
            return inner[:i], inner[i:]
        i += 1
    return inner, ""


def csharp_string(text):
    """A regular C# string literal for text."""
    out = []
    for ch in text:
        if ch == "\\":
            out.append("\\\\")
        elif ch == '"':
            out.append('\\"')
        elif ch == "\n":
            out.append("\\n")
        elif ch == "\r":
            out.append("\\r")
        elif ch == "\t":
            out.append("\\t")
        else:
            out.append(ch)
    return '"' + "".join(out) + '"'


def unescape_regular(body):
    """The value of a regular literal's body (only the escapes used in this code base)."""
    return re.sub(r"\\(u[0-9a-fA-F]{4}|.)", lambda m: {"n": "\n", "r": "\r", "t": "\t", "0": "\0"}.get(m.group(1), None) or (chr(int(m.group(1)[1:], 16)) if m.group(1).startswith("u") else m.group(1)), body)


def literal_value(lit, body):
    return body.replace('""', '"') if lit.verbatim else unescape_regular(body)


def rewrite_interpolated(lit):
    parts, args = [], []
    pos = 0
    for k, (hs, he) in enumerate(lit.holes):
        parts.append(literal_value(lit, lit.body[pos:hs]).replace("{{", "{{").replace("}}", "}}"))
        expr, suffix = split_hole(lit.body[hs:he])
        parts.append("{" + str(k) + suffix + "}")
        args.append(rewrite_source(expr.strip()))
        pos = he
    parts.append(literal_value(lit, lit.body[pos:]))
    fmt = "".join(parts)
    return "L.F(" + csharp_string(fmt) + "".join(", " + a for a in args) + ")"


def is_wrapped(src, start):
    before = src[max(0, start - 6):start]
    return before.endswith("L.T(") or before.endswith("L.F(")


def in_const_or_attribute(src, start):
    line_start = src.rfind("\n", 0, start) + 1
    prefix = src[line_start:start]
    return bool(re.search(r"\bconst\b", prefix)) or bool(re.match(r"\s*\[", prefix)) or bool(re.search(r"\bcase\s*$", prefix))


def has_keep_mark(src, start):
    line_end = src.find("\n", start)
    line_start = src.rfind("\n", 0, start) + 1
    return KEEP_MARK in src[line_start:line_end if line_end >= 0 else len(src)]


def rewrite_source(src):
    out, pos = [], 0
    for lit in list(scan(src)):
        if not JP.search(lit.body) or is_wrapped(src, lit.start) or has_keep_mark(src, lit.start):
            if lit.interpolated and lit.holes and JP.search(lit.body):
                pass
            continue
        if in_const_or_attribute(src, lit.start):
            print(f"  ! left as is (const/attribute/case): {src[lit.start:lit.end][:80]}", file=sys.stderr)
            continue
        out.append(src[pos:lit.start])
        if lit.interpolated:
            if JP.search(_text_outside_holes(lit)):
                out.append(rewrite_interpolated(lit))
            else:
                out.append(_rewrite_inside_holes(lit, src))
        else:
            out.append("L.T(" + csharp_string(literal_value(lit, lit.body)) + ")")
        pos = lit.end
    out.append(src[pos:])
    return "".join(out)


def _text_outside_holes(lit):
    text, pos = [], 0
    for hs, he in lit.holes:
        text.append(lit.body[pos:hs])
        pos = he
    text.append(lit.body[pos:])
    return "".join(text)


def _rewrite_inside_holes(lit, src):
    """An interpolated string whose own text has no Japanese, but whose holes do: rewrite only the holes."""
    raw = src[lit.start:lit.end]
    prefix_len = raw.index('"') + 1
    body = lit.body
    out, pos = [], 0
    for hs, he in lit.holes:
        out.append(body[pos:hs])
        expr, suffix = split_hole(body[hs:he])
        out.append("{" + rewrite_source(expr) + suffix + "}")
        pos = he
    out.append(body[pos:])
    return raw[:prefix_len] + "".join(out) + '"'


def process(path):
    if os.path.basename(path) in SKIP_FILES:
        return False
    with open(path, encoding="utf-8-sig", newline="") as handle:
        raw = handle.read()
    with open(path, "rb") as handle:
        bom = handle.read(3) == b"\xef\xbb\xbf"
    new = rewrite_source(raw)
    if new == raw:
        return False
    with open(path, "w", encoding="utf-8-sig" if bom else "utf-8", newline="") as handle:
        handle.write(new)
    return True


def main(paths):
    changed = 0
    for target in paths:
        files = [target] if os.path.isfile(target) else [
            os.path.join(root, name) for root, dirs, names in os.walk(target)
            for name in names if name.endswith(".cs") and not any(part in ("bin", "obj") for part in root.split(os.sep))]
        for path in files:
            if process(path):
                changed += 1
                print("rewrote", path)
    print("files changed:", changed)


if __name__ == "__main__":
    main(sys.argv[1:])
