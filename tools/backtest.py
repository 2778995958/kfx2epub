#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把「轉換結果」與出版社原始 EPUB（bw）逐元素對照，產出回測報告。

用法：
    python tools/backtest.py 我們.epub bw.epub -o report.md

可選：加上 KFX 原始樣式 dump，報告會多一欄「KFX 原始」。
    set KFX_DUMP_CLASSES=<classes.jsonl>
    set KFX_DUMP_ELEMS=<elems.jsonl>
    python convert_kfx2epub_fast.py <kfx-zip> <outdir> -w 1
    python tools/backtest.py <outdir>/xxx.epub bw.epub -o report.md ^
        --kfx-classes <classes.jsonl> --kfx-elems <elems.jsonl>

報告內容：
  1. 每個頁型（扉／奥付／奥付2／目次）的元素三欄對照表
  2. 全書差異分類：等比例（轉換邏輯）／我們缺（bw 有）／KFX 多餘（bw 無）／真差異
  3. publisher class 的比例證據
"""
import argparse
import collections
import html
import io
import json
import os
import re
import sys
import zipfile
from xml.etree import ElementTree as ET

XHTML = "{http://www.w3.org/1999/xhtml}"
SIDES = ("top", "right", "bottom", "left")

PROPS = {"text-align", "font-size", "font-weight", "font-family", "line-height", "text-indent",
         "width", "height", "max-width", "max-height", "display", "vertical-align", "writing-mode"}
for side in SIDES:
    for base in ("margin", "padding"):
        PROPS.add("%s-%s" % (base, side))
    for extra in ("style", "width", "color"):
        PROPS.add("border-%s-%s" % (side, extra))
PROPS |= {"margin", "padding", "border-style", "border-width", "border-color"}

SHOW = ["font-size", "font-weight", "text-align", "text-indent", "line-height",
        "margin-top", "margin-bottom", "margin-left", "margin-right",
        "padding-top", "padding-bottom", "padding-left", "padding-right",
        "border-top-width", "border-bottom-width"]

INHERIT = ("font-size", "font-family", "font-weight", "line-height", "text-align",
           "text-indent", "color", "writing-mode")

PAGE_TITLES = {"p-titlepage": "扉", "p-colophon": "奥付",
               "p-colophon2": "奥付2", "p-toc": "目次"}


# ---------------------------------------------------------------- CSS

def parse_rules(css):
    css = re.sub(r"/\*.*?\*/", "", css, flags=re.S)
    css = re.sub(r"@charset[^;]*;", "", css)
    rules, stack, buf = [], [], ""
    for ch in css:
        if ch == "{":
            stack.append(buf.strip())
            buf = ""
        elif ch == "}":
            body, buf = buf.strip(), ""
            if not stack:
                continue
            sel = stack.pop()
            if sel.startswith("@"):
                continue
            decls = {}
            for part in body.split(";"):
                if ":" not in part:
                    continue
                name, _, value = part.partition(":")
                decls[re.sub(r"\s+", " ", name).strip().lower()] = re.sub(r"\s+", " ", value).strip()
            if decls:
                rules.append((sel, decls))
        else:
            buf += ch
    return rules


def expand(decls):
    out = {}
    for name, value in decls.items():
        if name in ("margin", "padding"):
            parts = value.split()
            if len(parts) == 1:
                vals = parts * 4
            elif len(parts) == 2:
                vals = [parts[0], parts[1], parts[0], parts[1]]
            elif len(parts) == 3:
                vals = [parts[0], parts[1], parts[2], parts[1]]
            elif len(parts) == 4:
                vals = parts
            else:
                continue
            for side, one in zip(SIDES, vals):
                out["%s-%s" % (name, side)] = one
            continue
        if name in ("border-style", "border-width", "border-color"):
            parts = value.split()
            if len(parts) == 1:
                vals = parts * 4
            elif len(parts) == 2:
                vals = [parts[0], parts[1], parts[0], parts[1]]
            elif len(parts) == 3:
                vals = [parts[0], parts[1], parts[2], parts[1]]
            elif len(parts) == 4:
                vals = parts
            else:
                continue
            suffix = name.split("-")[1]
            for side, one in zip(SIDES, vals):
                out["border-%s-%s" % (side, suffix)] = one
            continue
        match = re.fullmatch(r"border-(top|right|bottom|left)", name)
        if match:
            prefix = "border-%s-" % match.group(1)
            for token in value.split():
                if token in ("solid", "dashed", "dotted", "double", "none"):
                    out[prefix + "style"] = token
                elif re.fullmatch(r"[0-9.]+px", token):
                    out[prefix + "width"] = token
                else:
                    out[prefix + "color"] = token
            continue
        out[name] = value
    return out


def imports_of(z, name):
    path = next((x for x in z.namelist() if os.path.basename(x) == name), None)
    if not path:
        return []
    text = z.read(path).decode("utf-8", "replace")
    return [os.path.basename(m.group(1)) for m in
            re.finditer(r'@import\s+(?:url\()?["\']?([^"\')\s]+)', text)]


def css_names_for_page(z, text):
    names = []
    for match in re.finditer(r'<link[^>]*href="([^"]+\.css)"', text):
        names.append(os.path.basename(match.group(1)))
    ordered = []
    for name in names:
        if name == "book-style.css":
            ordered.extend(imports_of(z, name))
        ordered.append(name)
    return ordered


class Loader:
    def __init__(self, z):
        self.z = z
        self.cache = {}

    def rules(self, names):
        key = tuple(names)
        if key in self.cache:
            return self.cache[key]
        rules = []
        for name in names:
            path = next((x for x in self.z.namelist() if os.path.basename(x) == name), None)
            if not path:
                continue
            for sel, decls in parse_rules(self.z.read(path).decode("utf-8", "replace")):
                expanded = expand(decls)
                for part in sel.split(","):
                    part = part.strip()
                    if part:
                        rules.append((part, expanded, len(rules)))
        self.cache[key] = rules
        return rules


def part_match(part, tag, classes):
    if ":" in part:
        part = part.split(":", 1)[0]
    if not part or "[" in part:
        return False
    match = re.fullmatch(r"([A-Za-z][\w-]*)?((?:\.[\w-]+)*)", part)
    if not match:
        return False
    need_tag, cls_part = match.group(1), match.group(2)
    if need_tag and need_tag.lower() != tag:
        return False
    return all(c in classes for c in re.findall(r"\.([\w-]+)", cls_part))


def selector_match(parts, tag, classes, ancestors):
    if not part_match(parts[-1], tag, classes):
        return False
    index = len(ancestors) - 1
    for part in reversed(parts[:-1]):
        while index >= 0:
            atag, acls = ancestors[index]
            index -= 1
            if part_match(part, atag, set(acls)):
                break
        else:
            return False
    return True


def specificity(sel):
    return (len(re.findall(r"\.", sel)), len(re.findall(r"(?:^|\s)([A-Za-z][\w-]*)", sel)))


def resolve(tag, classes, style, ancestors, rules):
    matched = []
    for sel, decls, order in rules:
        if selector_match(sel.split(), tag, set(classes), ancestors):
            matched.append((specificity(sel), order, decls))
    matched.sort(key=lambda item: (item[0], item[1]))
    result = {}
    for _, _, decls in matched:
        for name, value in decls.items():
            if name in PROPS:
                result[name] = value
    for part in (style or "").split(";"):
        if ":" in part:
            name, _, value = part.partition(":")
            name = name.strip().lower()
            if name in PROPS:
                result[name] = value.strip()
    return result


def elems(z, path, rules):
    root = ET.fromstring(z.read(path))
    body = root.find(XHTML + "body")
    out = []

    def rec(element, ancestors, depth, in_ruby, inherited, parent):
        tag = element.tag.split("}")[-1]
        classes = (element.get("class") or "").split()
        decl = resolve(tag, classes, element.get("style", ""), ancestors, rules)
        for name in INHERIT:
            if name not in decl and name in inherited:
                decl[name] = inherited[name]
        text = re.sub(r"\s+", "", html.unescape("".join(element.itertext())))
        index = len(out)
        out.append({"depth": depth, "tag": tag, "class": " ".join(classes), "decl": decl,
                    "text": text, "in_ruby": in_ruby, "parent": parent})
        descendant_inherit = {name: decl[name] for name in INHERIT if name in decl}
        for child in element:
            rec(child, ancestors + [(tag, classes)], depth + 1,
                in_ruby or tag == "ruby", descendant_inherit, index)

    html_classes = (root.get("class") or "").split()
    html_decl = resolve("html", html_classes, root.get("style", ""), [], rules)
    rec(body, [("html", html_classes)], 0, False,
        {name: html_decl[name] for name in INHERIT if name in html_decl}, None)
    return out


def align_seq(a, b):
    """以 (tag, 前 12 字) 做 LCS，回傳 a 索引 → b 元素。"""
    ka = [(e["tag"], e["text"][:12]) for e in a]
    kb = [(e["tag"], e["text"][:12]) for e in b]
    n, m = len(ka), len(kb)
    dp = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(n - 1, -1, -1):
        for j in range(m - 1, -1, -1):
            dp[i][j] = dp[i + 1][j + 1] + 1 if ka[i] == kb[j] else max(dp[i + 1][j], dp[i][j + 1])
    out, i, j = {}, 0, 0
    while i < n and j < m:
        if ka[i] == kb[j]:
            out[id(a[i])] = b[j]
            i += 1
            j += 1
        elif dp[i + 1][j] >= dp[i][j + 1]:
            i += 1
        else:
            j += 1
    return out


def spine(z):
    path = next(x for x in z.namelist() if x.lower().endswith(".opf"))
    text = z.read(path).decode("utf-8", "replace")
    items = {}
    for match in re.finditer(r"<item\b([^>]*?)/?>", text):
        attrs = dict(re.findall(r'([\w:-]+)="([^"]*)"', match.group(1)))
        if "id" in attrs and "href" in attrs:
            items[attrs["id"]] = attrs
    base = os.path.dirname(path)
    return [os.path.normpath(os.path.join(base, items[i]["href"])).replace("\\", "/")
            for i in re.findall(r"<itemref[^>]*idref=\"([^\"]+)\"", text) if i in items]


def body_classes(elements):
    for elem in elements:
        if elem["tag"] == "body":
            return elem["class"].split()
    return []


def num(prop, value):
    if value is None:
        return None
    value = str(value).strip().lower()
    match = re.fullmatch(r"(-?[0-9.]+)(em|rem|%|px)?", value)
    if not match:
        return ("s", value)
    amount = float(match.group(1))
    unit = match.group(2) or ""
    if prop == "font-size":
        if unit == "%":
            return ("n", amount)
        if unit in ("em", "rem"):
            return ("n", amount * 100)
        if unit == "px":
            return ("n", amount / 16 * 100)
        return ("s", value)
    if unit == "rem":
        return ("n", amount)
    if unit and unit != "em":
        return ("u", unit, amount)
    return ("n", amount)


def same(prop, a, b):
    if a is None or b is None:
        return a is None and b is None
    na, nb = num(prop, a), num(prop, b)
    if na[0] != nb[0]:
        return False
    if na[0] == "n":
        return abs(na[1] - nb[1]) < 1e-6
    if na[0] == "u":
        return na[1] == nb[1] and abs(na[2] - nb[2]) < 1e-6
    return na[1] == nb[1]


def show(prop, value):
    if value is None:
        return "-"
    parsed = num(prop, value)
    if parsed and parsed[0] == "n":
        return "%g" % round(parsed[1], 4)
    return value


def main():
    parser = argparse.ArgumentParser(description="轉換結果與 bw 原書的逐元素回測")
    parser.add_argument("ours")
    parser.add_argument("bw")
    parser.add_argument("-o", "--output", default="backtest.md")
    parser.add_argument("--kfx-classes")
    parser.add_argument("--kfx-elems")
    args = parser.parse_args()

    zour, zbw = zipfile.ZipFile(args.ours), zipfile.ZipFile(args.bw)
    lour, lbw = Loader(zour), Loader(zbw)
    sp_our, sp_bw = spine(zour), spine(zbw)

    kfx_classes = {}
    if args.kfx_classes and os.path.exists(args.kfx_classes):
        for line in io.open(args.kfx_classes, encoding="utf-8"):
            row = json.loads(line)
            kfx_classes[row["class"]] = {k: v for k, v in row["decls"].items()
                                         if not k.startswith("-kfx-")}
    kfx_elems = collections.defaultdict(list)
    if args.kfx_elems and os.path.exists(args.kfx_elems):
        for line in io.open(args.kfx_elems, encoding="utf-8"):
            row = json.loads(line)
            kfx_elems[os.path.basename(row["page"])].append(row)

    report = []

    def write(line=""):
        report.append(line)

    def page_elements(z, path, loader):
        text = z.read(path).decode("utf-8", "replace")
        return elems(z, path, loader.rules(css_names_for_page(z, text)))

    write("# 回測：轉換結果 vs bw 原書")
    write()
    write("| 項目 | 檔案 |")
    write("|---|---|")
    write("| 轉換結果 | `%s` |" % os.path.basename(args.ours))
    write("| bw 原書 | `%s` |" % os.path.basename(args.bw))
    write()

    # 逐頁型對照表
    for index in range(min(len(sp_our), len(sp_bw))):
        eo = page_elements(zour, sp_our[index], lour)
        eb = page_elements(zbw, sp_bw[index], lbw)
        classes = set(body_classes(eo))
        page_class = next((c for c in PAGE_TITLES if c in classes), None)
        if page_class is None:
            continue

        kfx_rows = kfx_elems.get("p-%03d.xhtml" % index, [])
        kfx_by_text = {}
        for row in kfx_rows:
            key = re.sub(r"\s+", "", row["text"])[:16]
            if key and key not in kfx_by_text:
                kfx_by_text[key] = row

        bw_by_text = collections.defaultdict(list)
        for pos, elem in enumerate(eb):
            if elem["text"]:
                bw_by_text[elem["text"][:16]].append(pos)

        def pick_bw(our):
            idxs = bw_by_text.get(our["text"][:16], [])
            if not idxs:
                return None
            for pos in idxs:
                if eb[pos]["tag"] == our["tag"] and eb[pos]["class"]:
                    return eb[pos]
            for pos in idxs:
                if eb[pos]["tag"] == our["tag"]:
                    return eb[pos]
            for pos in sorted(idxs, key=lambda i: eb[i]["depth"]):
                node = pos
                while node is not None and not eb[node]["class"]:
                    node = eb[node]["parent"]
                if node is not None and "p-" not in eb[node]["class"]:
                    return eb[node]
            return None

        write("## %s（`%s` ↔ `%s`）" % (PAGE_TITLES[page_class],
                                        sp_our[index].split("/")[-1], sp_bw[index].split("/")[-1]))
        write()
        write("| 文字 | KFX 原始 | 轉換結果 | bw | 差異 |")
        write("|---|---|---|---|---|")
        for elem in eo:
            if elem["in_ruby"] or elem["tag"] in ("body", "html", "ruby", "rt"):
                continue
            bw = pick_bw(elem)
            kfx_row = kfx_by_text.get(elem["text"][:16]) if kfx_classes else None
            kfx_cell = "—"
            if kfx_row:
                decls = kfx_classes.get(kfx_row["kfx_class"], {})
                kfx_cell = "`%s` %s" % (kfx_row["kfx_class"],
                                        " ".join("%s:%s" % (p, decls[p]) for p in SHOW if p in decls))
            diff = []
            if bw:
                for prop in SHOW:
                    if not same(prop, elem["decl"].get(prop), bw["decl"].get(prop)):
                        diff.append("%s %s→%s" % (prop, show(prop, elem["decl"].get(prop)),
                                                  show(prop, bw["decl"].get(prop))))
            text = elem["text"][:12] or ("（%s）" % elem["tag"])
            write("| %s | %s | `%s` | %s | %s |" % (
                text, kfx_cell, elem["class"] or "—",
                ("`%s`" % bw["class"]) if bw and bw["class"] else "—",
                "<br>".join(diff[:5]) if diff else "—"))
        write()

    # 全書分類
    categories = collections.Counter()
    examples = collections.defaultdict(list)
    length_prefix = ("margin-", "padding-", "border-", "text-indent")
    for index in range(min(len(sp_our), len(sp_bw))):
        eo = page_elements(zour, sp_our[index], lour)
        eb = page_elements(zbw, sp_bw[index], lbw)
        mapping = align_seq(eo, eb)
        for elem in eo:
            bw = mapping.get(id(elem))
            if bw is None:
                continue
            ratios = []
            for prop in SHOW:
                a, b = elem["decl"].get(prop), bw["decl"].get(prop)
                if a and b and prop.startswith(length_prefix):
                    na, nb = num(prop, a), num(prop, b)
                    if na and nb and na[0] == "n" and nb[0] == "n" and na[1] > 0 and nb[1] > 0:
                        ratios.append(na[1] / nb[1])
            scaled = len(ratios) >= 2 and max(ratios) - min(ratios) < 0.01
            for prop in SHOW:
                a, b = elem["decl"].get(prop), bw["decl"].get(prop)
                if same(prop, a, b):
                    continue
                if a and not b:
                    kind = "KFX 多餘（bw 無）"
                elif b and not a:
                    kind = "我們缺（bw 有）"
                elif scaled and prop.startswith(length_prefix):
                    kind = "等比例（轉換邏輯）"
                else:
                    kind = "真差異"
                categories[kind] += 1
                if len(examples[kind]) < 4:
                    examples[kind].append("%s: %s %s→%s" % (
                        elem["text"][:8] or elem["tag"], prop, show(prop, a), show(prop, b)))

    write("## 全書差異分類")
    write()
    write("| 分類 | 筆數 | 例 |")
    write("|---|---|---|")
    for kind, count in categories.most_common():
        write("| %s | %d | %s |" % (kind, count, examples[kind][0] if examples[kind] else ""))
    write()
    write("「等比例」＝同一元素內兩條以上長度與 bw 共用同一比例，代表同一條規則換了 em 基準。")
    write("「KFX 多餘」＝bw 同一句沒有這個宣告，可作為 prune 候選。")

    with io.open(args.output, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(report) + "\n")
    print("寫出 %s（%d 行）" % (args.output, len(report)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
