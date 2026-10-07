"""Wraps Japanese display text in XAML with the translator.

  Text="日本語"                          ->  Text="{l:T '日本語'}"
  Text="{Binding N, StringFormat=…件}"   ->  Text="{l:TBinding N, TFormat=…件}"
  <Hyperlink …>開く</Hyperlink>          ->  <Hyperlink …><Run Text="{l:T '開く'}"/></Hyperlink>

Adds xmlns:l to the root element. Idempotent. Usage: python tools/i18n/wrap_xaml.py <file-or-dir>...
"""
import os
import re
import sys

JP = re.compile(r"[\u3000-\u303f\u3040-\u30ff\u3400-\u9fff\uff01-\uff60]")
XMLNS = 'xmlns:l="clr-namespace:LocalMediaAI.App.Localization"'
ATTR = re.compile(r'(\s)([\w]+(?:\.[\w]+)?)="([^"]*)"')
CONTENT = re.compile(r">([^<>]+)<")


def quote(text):
    return "'" + text.replace("\\", "\\\\").replace("'", "\\'") + "'"


def rewrite_attr(match):
    space, name, value = match.groups()
    if not JP.search(value) or name.startswith("x:") or "l:T" in value or "l:TBinding" in value:
        return match.group(0)
    if value.startswith("{Binding") or value.startswith("{Binding "):
        if "StringFormat=" not in value:
            return match.group(0)
        value = "{l:TBinding" + value[len("{Binding"):]
        value = value.replace("StringFormat=", "TFormat=", 1)
        return f'{space}{name}="{value}"'
    if value.startswith("{}"):
        value = value[2:]
    elif value.startswith("{"):
        print(f"  ! unknown markup with Japanese, left as is: {name}={value[:80]}", file=sys.stderr)
        return match.group(0)
    return f'{space}{name}="{{l:T {quote(value)}}}"'


def rewrite_content(match):
    text = match.group(1)
    if not JP.search(text) or text.strip().startswith("!--"):
        return match.group(0)
    stripped = text.strip()
    return f'><Run Text="{{l:T {quote(stripped)}}}"/><'


def strip_comments(xml):
    """Positions of XML comments, so text inside them is not touched."""
    return [(m.start(), m.end()) for m in re.finditer(r"<!--.*?-->", xml, re.S)]


def rewrite(xml):
    comments = strip_comments(xml)

    def outside(pos):
        return not any(start <= pos < end for start, end in comments)

    def sub(regex, repl, text):
        return regex.sub(lambda m: repl(m) if outside(m.start()) else m.group(0), text)

    new = sub(ATTR, rewrite_attr, xml)
    comments = strip_comments(new)
    new = sub(CONTENT, rewrite_content, new)
    if new != xml and XMLNS not in new:
        new = re.sub(r'(xmlns:x="http://schemas\.microsoft\.com/winfx/2006/xaml")', r'\1 ' + XMLNS, new, count=1)
    return new


def process(path):
    with open(path, "rb") as handle:
        data = handle.read()
    bom = data.startswith(b"\xef\xbb\xbf")
    xml = data.decode("utf-8-sig")
    new = rewrite(xml)
    if new == xml:
        return False
    with open(path, "wb") as handle:
        handle.write((b"\xef\xbb\xbf" if bom else b"") + new.encode("utf-8"))
    return True


def main(paths):
    for target in paths:
        files = [target] if os.path.isfile(target) else [
            os.path.join(root, name) for root, dirs, names in os.walk(target)
            for name in names if name.endswith(".xaml") and not any(p in ("bin", "obj") for p in root.split(os.sep))]
        for path in files:
            if process(path):
                print("rewrote", path)


if __name__ == "__main__":
    main(sys.argv[1:])
