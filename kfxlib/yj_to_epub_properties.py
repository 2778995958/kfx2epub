import collections
import decimal
import functools
import io
import json
from lxml import etree
import os
import pkgutil
import re
import unicodedata
import zipfile

from .epub_output import (EPUB_NS_URI, EPUB_TYPE, IDX_ENTRY, MATH, qname, split_value, SVG, XML_LANG, XML_NS_URI, value_str)
from .ion import (ion_type, IonBool, IonDecimal, IonFloat, IonInt, IonList, IonString, IonStruct, IonSymbol, isstring)
from .message_logging import log
from .utilities import (get_url_filename, list_symbols, natural_sort_key, remove_duplicates, type_name, urlabspath, urlrelpath)


__license__ = "GPL v3"
__copyright__ = "2016-2025, John Howell <jhowell@acm.org>"


STYLE_TEST = False
KEEP_STYLES_INLINE = False
EMIT_OEB_PAGE_PROPS = False
DETECT_LAYOUT_ENHANCER = False
FIX_PT_TO_PX = True
REMOVE_EMPTY_NAMED_CLASSES = True

EMIT_WEBKIT_EQUIVALENTS = False

# Properties already supplied by the bundled ebpaj style-reset.css, redundant when emitted on a class.
# margin-*:0 is the reset default for div/p/h1-6 (non-inherited); text-align:justify is the reset
# default inherited from body (only strip when no ancestor overrides text-align).
RESET_DEFAULT_MARGIN_TAGS = {"p", "div", "h1", "h2", "h3", "h4", "h5", "h6"}
RESET_DEFAULT_FONT_FAMILIES = {"JA,serif", "V,JA,serif", "serif-ja,serif", "serif-ja, serif"}

# KFX 以區塊方向的 lh margin 表示空段落。這裡先標記，
# 等 simplify_styles 決定元素的最終標籤後，再插入 <p><br/></p>。
BLANK_BEFORE_PROPERTY = "-kfx-blank-before"
BLANK_AFTER_PROPERTY = "-kfx-blank-after"
BLANK_ORIG_BEFORE_PROPERTY = "-kfx-blank-orig-before"
BLANK_ORIG_AFTER_PROPERTY = "-kfx-blank-orig-after"
BLANK_PROP_BEFORE_PROPERTY = "-kfx-blank-prop-before"
BLANK_PROP_AFTER_PROPERTY = "-kfx-blank-prop-after"
BLANK_LINE_REM = decimal.Decimal("1.05")

BOX_PADDING_PROPERTIES = {"padding", "padding-top", "padding-right", "padding-bottom", "padding-left"}

REVERSE_INHERITANCE = True
REVERSE_INHERITANCE_FRACTION = 0.8

FIX_NONSTANDARD_FONT_WEIGHT = False

USE_NORMAL_LINE_HEIGHT = True

LINE_HEIGHT_SCALE_FACTOR = decimal.Decimal("1.2")
NORMAL_LINE_HEIGHT_EM = "%sem" % LINE_HEIGHT_SCALE_FACTOR

PX_PER_PERCENT = 8.534

MINIMUM_LINE_HEIGHT = decimal.Decimal("1.0")

CVT_DIRECTION_PROPERTY_TO_MARKUP = False

DEFAULT_DOCUMENT_FONT_FAMILY = "serif"
DEFAULT_DOCUMENT_LINE_HEIGHT = "normal" if USE_NORMAL_LINE_HEIGHT else NORMAL_LINE_HEIGHT_EM
DEFAULT_DOCUMENT_FONT_SIZE = "1em"

DEFAULT_KC_COMIC_FONT_SIZE = "16px"

DEFAULT_CLASS_NAME_PREFIX = "class"

HEADING_TAGS = {"h1", "h2", "h3", "h4", "h5", "h6"}
INLINE_TAGS = {"span", "a", "b", "i", "em", "strong", "sub", "sup", "code",
               "ruby", "rt", "rp", "br", "img"}


class StyleUsage(object):
    """記錄樣式被哪些元素使用，供「可省略宣告」與方向相關 class 判斷。"""
    __slots__ = ("tags", "all_headings", "all_links", "all_hr", "all_body_no_text",
                 "body_only", "direction", "has_direction", "font_px")

    def __init__(self):
        self.tags = set()
        self.all_headings = True
        self.all_links = True
        self.all_hr = True
        self.all_body_no_text = True
        self.body_only = True
        self.direction = None
        self.has_direction = False
        self.font_px = 16.0


_utility_class_names = None


_publisher_rules = None

PUBLISHER_STRUCTURAL_CLASSES = {"main"}


def parse_declarations(body):
    props = {}
    for part in body.split(";"):
        if ":" not in part:
            continue
        name, _, value = part.partition(":")
        name = re.sub(r"\s+", " ", name).strip().lower()
        value = re.sub(r"\s+", " ", value).strip()
        if name and value:
            props[name] = value
    return expand_declarations(props)


def expand_declarations(props):
    result = {}
    sides = ("top", "right", "bottom", "left")
    for name, value in props.items():
        if name in ("margin", "padding"):
            values = box_values(value)
            if values is None:
                continue
            for side, one in zip(sides, values):
                result["%s-%s" % (name, side)] = one
            continue
        if name in ("border-style", "border-width", "border-color"):
            values = box_values(value)
            if values is None:
                continue
            suffix = name.split("-", 1)[1]
            for side, one in zip(sides, values):
                result["border-%s-%s" % (side, suffix)] = one
            continue
        match = re.fullmatch(r"border(?:-(top|right|bottom|left))?", name)
        if match:
            prefix = "border-%s-" % match.group(1) if match.group(1) else "border-"
            for token in value.split():
                if token in ("solid", "dashed", "dotted", "double", "none", "hidden"):
                    result[prefix + "style"] = token
                elif re.fullmatch(r"[0-9.]+px", token) or token in ("thin", "medium", "thick"):
                    result[prefix + "width"] = token
                else:
                    result[prefix + "color"] = token
            continue
        result[name] = value
    return result


def box_values(value):
    parts = value.split()
    if len(parts) == 1:
        return [parts[0]] * 4
    if len(parts) == 2:
        return [parts[0], parts[1], parts[0], parts[1]]
    if len(parts) == 3:
        return [parts[0], parts[1], parts[2], parts[1]]
    if len(parts) == 4:
        return parts
    return None


def publisher_rules():
    global _publisher_rules
    if _publisher_rules is not None:
        return _publisher_rules

    rules = []
    for filename in ("style-kadokawa.css", "style-karc.css"):
        path = os.path.join(BUNDLED_STYLES_DIR, filename)
        if not os.path.exists(path):
            continue
        css = open(path, encoding="utf-8").read()
        css = re.sub(r"/\*.*?\*/", "", css, flags=re.S)
        css = re.sub(r"@charset[^;]*;", "", css)
        css = re.sub(r"@import[^;]*;", "", css)
        for match in re.finditer(r"([^{}]+)\{([^{}]*)\}", css):
            parts = match.group(1).split()
            if not (2 <= len(parts) <= 3) or not parts[0].startswith(".p-"):
                continue
            if not parts[1].startswith("."):
                continue
            page_type = parts[0][1:]
            parent = tag = class_name = None
            if len(parts) == 2:
                class_name = parts[1][1:]
            elif parts[2].startswith("."):
                parent, class_name = parts[1][1:], parts[2][1:]
            else:
                parent, tag = parts[1][1:], parts[2]
            decls = parse_declarations(match.group(2))
            if decls:
                rules.append((page_type, parent, tag, class_name, decls, len(rules)))
    _publisher_rules = rules
    return rules


def try_length(value):
    match = re.fullmatch(r"(-?[0-9.]+)(em|rem|%|px)", (value or "").strip())
    if not match:
        return None
    unit = match.group(2)
    return (float(match.group(1)), "em" if unit == "rem" else unit)


def font_percent(value):
    parsed = try_length(value)
    if parsed is None:
        return None
    amount, unit = parsed
    if unit == "%":
        return amount
    if unit == "em":
        return amount * 100
    return amount / 16 * 100


def element_font_percent(elem):
    for class_name in (elem.get("class") or "").split():
        match = re.fullmatch(r"font-(\d{3})per", class_name)
        if match:
            return int(match.group(1))
        match = re.fullmatch(r"font-(\d+)em(\d*)", class_name)
        if match:
            text = match.group(1) + ("." + match.group(2) if match.group(2) else "")
            return float(text) * 100
    return None


def line_height_class(value):
    if not value:
        return None
    text = value.strip().lower()
    if text == "normal":
        return "line-height-normal"
    try:
        number = float(text)
    except ValueError:
        return None
    if abs(number - int(number)) < 1e-9:
        name = "line-height-%dem" % int(number)
    else:
        name = "line-height-" + ("%.2f" % number).replace(".", "em")
    return name if name in utility_class_names() else None


def font_compatible(decls, font_pct):
    if font_pct is None:
        return True
    value = decls.get("font-size")
    if value is None:
        return True
    candidate = font_percent(value)
    return candidate is not None and abs(font_pct - candidate) <= 0.5


def covers_residual(residual, candidate, font_pct):
    ratios = []
    for name, value in residual.items():
        other = candidate.get(name)
        if other is None:
            return None
        if name == "font-size":
            a, b = font_percent(value), font_percent(other)
            if a is None or b is None or abs(a - b) > 0.5:
                return None
            continue
        current, reference = try_length(value), try_length(other)
        if current and reference and current[1] == reference[1]:
            if reference[1] == "px":
                if abs(current[0] - reference[0]) > 1e-6:
                    return None
            elif reference[0] > 0:
                ratios.append(current[0] / reference[0])
            continue
        if value.strip().lower() != other.strip().lower():
            return None

    if font_pct is not None and candidate.get("font-size"):
        candidate_pct = font_percent(candidate["font-size"])
        if candidate_pct is not None and abs(font_pct - candidate_pct) > 0.5:
            return None
    return ratios


def utility_class_names():
    global _utility_class_names
    if _utility_class_names is None:
        names = set()
        for filename in ("style-standard.css", "style-advance.css"):
            path = os.path.join(BUNDLED_STYLES_DIR, filename)
            css = re.sub(r"/\*.*?\*/", "", open(path, encoding="utf-8").read(), flags=re.S)
            names.update(re.findall(r"\.([A-Za-z][\w-]*)", css))
        _utility_class_names = names
    return _utility_class_names


def box_size_name(value):
    match = re.fullmatch(r"(\d+(?:\.\d+)?)(em|%)", value or "")
    if not match:
        return None
    magnitude = float(match.group(1))
    if match.group(2) == "%":
        if magnitude != int(magnitude):
            return None
        return "%03dper" % int(magnitude)
    if magnitude == int(magnitude):
        return "%dem" % int(magnitude)
    return ("%.2f" % magnitude).replace(".", "em")

# 電書協樣式表中實際存在的 utility class（字級百分比、寬高尺寸）。
UTILITY_FONT_PERCENTS = {
    "050", "060", "070", "075", "080", "085", "090", "100", "110", "115", "120",
    "130", "140", "150", "160", "170", "180", "190", "200", "250", "300",
    }

UTILITY_FONT_EM_SIZES = {
    "0em50", "0em60", "0em70", "0em75", "0em80", "0em85", "0em90", "1em", "1em10",
    "1em15", "1em20", "1em30", "1em40", "1em50", "1em60", "1em70", "1em80", "1em90",
    "2em", "2em50", "3em",
    }

UTILITY_BOX_PERCENTS = {
    "010", "020", "025", "030", "033", "040", "050", "060", "067", "070", "075",
    "080", "090", "100",
    }

UTILITY_BOX_EM_SIZES = {
    "0em25", "0em50", "0em75", "1em", "1em25", "1em50", "1em75", "2em", "2em50",
    "3em", "4em", "5em", "5em25", "6em", "7em", "8em", "8em75", "9em", "10em",
    "11em", "12em", "13em", "14em", "15em", "20em", "30em", "40em",
    }

ALPHA_MASK = 0xff000000


class Prop(object):

    def __init__(self, name, values=None):
        self.name = name
        self.values = values


COLLISIONS = {
    "$352": "always",
    "$652": "queue",
    }


BORDER_STYLES = {
    "$349": "none",
    "$328": "solid",
    "$331": "dotted",
    "$330": "dashed",
    "$329": "double",
    "$335": "ridge",
    "$334": "groove",
    "$336": "inset",
    "$337": "outset",
    }


LAYOUT_HINT_ELEMENT_NAMES = {
    "$453": "caption",
    "$282": "figure",
    "$760": "heading",
}


YJ_PROPERTY_INFO = {
    "$479": Prop("background-image"),
    "$480": Prop("-kfx-background-positionx"),
    "$481": Prop("-kfx-background-positiony"),

    "$547": Prop("background-origin", {
        "$378": "border-box",
        "$377": "content-box",
        "$379": "padding-box",
        }),

    "$484": Prop("background-repeat", {
        "$487": "no-repeat",
        "$485": "repeat-x",
        "$486": "repeat-y",
        }),

    "$482": Prop("-kfx-background-sizex"),
    "$483": Prop("-kfx-background-sizey"),

    "$31": Prop("-kfx-baseline-shift"),

    "$44": Prop("-kfx-baseline-style", {
        "$60": "bottom",
        "$320": "middle",
        "$350": "baseline",
        "$371": "sub",
        "$370": "super",
        "$449": "text-bottom",
        "$447": "text-top",
        "$58": "top",
        }),

    "$682": Prop("direction", {
        "$376": "ltr",
        "$375": "rtl",
        }),

    "$674": Prop("unicode-bidi", {
        "$675": "embed",
        "$676": "isolate",
        "$678": "isolate-override",
        "$350": "normal",
        "$677": "bidi-override",
        "$679": "plaintext",
        }),

    "$83": Prop("border-color"),
    "$86": Prop("border-bottom-color"),
    "$85": Prop("border-left-color"),
    "$87": Prop("border-right-color"),
    "$84": Prop("border-top-color"),

    "$461": Prop("border-bottom-left-radius"),
    "$462": Prop("border-bottom-right-radius"),
    "$459": Prop("border-top-left-radius"),
    "$460": Prop("border-top-right-radius"),

    "$457": Prop("-webkit-border-horizontal-spacing"),
    "$456": Prop("-webkit-border-vertical-spacing"),

    "$88": Prop("border-style", BORDER_STYLES),
    "$91": Prop("border-bottom-style", BORDER_STYLES),
    "$90": Prop("border-left-style", BORDER_STYLES),
    "$92": Prop("border-right-style", BORDER_STYLES),
    "$89": Prop("border-top-style", BORDER_STYLES),

    "$93": Prop("border-width"),
    "$96": Prop("border-bottom-width"),
    "$95": Prop("border-left-width"),
    "$97": Prop("border-right-width"),
    "$94": Prop("border-top-width"),

    "$60": Prop("bottom"),
    "$580": Prop("-kfx-box-align", {
        "$320": "center",
        "$59": "left",
        "$61": "right",
        }),

    "$133": Prop("page-break-after", {
        "$352": "always",
        "$383": "auto",
        "$353": "avoid",
        }),

    "$134": Prop("page-break-before", {
        "$352": "always",
        "$383": "auto",
        "$353": "avoid",
        }),

    "$135": Prop("page-break-inside", {
        "$383": "auto",
        "$353": "avoid",
        }),

    "$708": Prop("-kfx-character-width", {
        "$383": None,
        }),

    "$476": Prop("overflow", {
        False: "visible",
        True: "hidden",
        }),

    "$112": Prop("column-count", {
        "$383": "auto",
        }),

    "$116": Prop("column-rule-color"),

    "$192": Prop("direction", {
        "$376": "ltr",
        "$375": "rtl",
        }),

    "$99": Prop("box-decoration-break", {
        False: "slice",
        True: "clone",
        }),

    "$73": Prop("background-clip", {
        "$378": "border-box",
        "$377": "content-box",
        "$379": "padding-box",
        }),

    "$70": Prop("-kfx-fill-color"),
    "$72": Prop("-kfx-fill-opacity"),

    "$140": Prop("float", {
        "$59": "left",
        "$61": "right",
        "$786": "snap-block",
        }),

    "$11": Prop("font-family"),
    "$16": Prop("font-size"),

    "$15": Prop("font-stretch", {
        "$365": "condensed",
        "$368": "expanded",
        "$350": "normal",
        "$366": "semi-condensed",
        "$367": "semi-expanded",
        }),

    "$12": Prop("font-style", {
        "$382": "italic",
        "$350": "normal",
        "$381": "oblique",
         }),

    "$13": Prop("font-weight", {
        "$361": "bold",
        "$363": "900",
        "$357": "300",
        "$359": "500",
        "$350": "normal",
        "$360": "600",
        "$355": "100",
        "$362": "800",
        "$356": "200",
        }),

    "$583": Prop("font-variant", {
        "$349": "normal",
        "$369": "small-caps"}),

    "$57": Prop("height"),

    "$458": Prop("empty-cells", {
        False: "show",
        True: "hide",
        }),

    "$127": Prop("hyphens", {
        "$383": "auto",
        "$384": "manual",
        "$349": "none",
        }),

    "$785": Prop("-kfx-keep-lines-together"),

    "$10": Prop("-kfx-attrib-xml-lang"),
    "$761": Prop("-kfx-layout-hints"),
    "$59": Prop("left"),
    "$32": Prop("letter-spacing"),

    "$780": Prop("line-break", {
        "$783": "anywhere",
        "$383": "auto",
        "$781": "loose",
        "$350": "normal",
        "$782": "strict",
        }),

    "$42": Prop("line-height", {
        "$383": "normal",
        }),

    "$577": Prop("-kfx-link-color"),
    "$576": Prop("-kfx-visited-color"),

    "$100": Prop("list-style-type", {
        "$346": "lower-alpha",
        "$347": "upper-alpha",
        "$342": "circle",
        "$737": "cjk-earthly-branch",
        "$738": "cjk-heavenly-stem",
        "$736": "cjk-ideographic",
        "$796": "decimal-leading-zero",
        "$340": "disc",
        "$795": "georgian",
        "$739": "hiragana",
        "$740": "hiragana-iroha",
        "$271": None,
        "$743": "japanese-formal",
        "$744": "japanese-informal",
        "$741": "katakana",
        "$742": "katakana-iroha",
        "$793": "lower-armenian",
        "$791": "lower-greek",
        "$349": "none",
        "$343": "decimal",
        "$344": "lower-roman",
        "$345": "upper-roman",
        "$746": "simp-chinese-formal",
        "$745": "simp-chinese-informal",
        "$341": "square",
        "$748": "trad-chinese-formal",
        "$747": "trad-chinese-informal",
        "$794": "upper-armenian",
        "$792": "upper-greek",
        }),

    "$503": Prop("list-style-image"),

    "$551": Prop("list-style-position", {
        "$552": "inside",
        "$553": "outside",
        }),

    "$46": Prop("margin"),
    "$49": Prop("margin-bottom"),
    "$48": Prop("margin-left"),
    "$50": Prop("margin-right"),
    "$47": Prop("margin-top"),

    "$64": Prop("max-height"),
    "$65": Prop("max-width"),
    "$62": Prop("min-height"),
    "$63": Prop("min-width"),

    "$45": Prop("white-space", {
        False: "normal",
        True: "nowrap",
        }),

    "$105": Prop("outline-color"),
    "$106": Prop("outline-offset"),
    "$107": Prop("outline-style", BORDER_STYLES),
    "$108": Prop("outline-width"),

    "$554": Prop("text-decoration", {
        "$330": "overline dashed",
        "$331": "overline dotted",
        "$329": "overline double",
        "$349": None,
        "$328": "overline",
        }),

    "$555": Prop("text-decoration-color"),

    "$51": Prop("padding"),
    "$54": Prop("padding-bottom"),
    "$53": Prop("padding-left"),
    "$55": Prop("padding-right"),
    "$52": Prop("padding-top"),

    "$183": Prop("position", {
        "$324": "absolute",
        "$455": "oeb-page-foot",
        "$151": "oeb-page-head",
        "$488": "relative",
        "$489": "fixed",
        }),

    "$61": Prop("right"),

    "$766": Prop("ruby-align", {
        "$320": "center",
        "$773": "space-around",
        "$774": "space-between",
        "$680": "start",
        }),

    "$764": Prop("ruby-merge", {
        "$772": "collapse",
        "$771": "separate",
        }),

    "$762": Prop("ruby-position", {
        "$60": "under",
        "$58": "over",
        }),

    "$763": Prop("ruby-position", {
        "$59": "under",
        "$61": "over",
        }),

    "$765": Prop("ruby-align", {
        "$320": "center",
        "$773": "space-around",
        "$774": "space-between",
        "$680": "start",
        }),

    "$496": Prop("box-shadow"),

    "$546": Prop("box-sizing", {
        "$378": "border-box",
        "$377": "content-box",
        "$379": "padding-box",
        }),

    "src": Prop("src"),

    "$27": Prop("text-decoration", {
        "$330": "line-through dashed",
        "$331": "line-through dotted",
        "$329": "line-through double",
        "$349": None,
        "$328": "line-through"}),

    "$28": Prop("text-decoration-color"),

    "$75": Prop("-webkit-text-stroke-color"),

    "$531": Prop("-svg-stroke-dasharray"),

    "$532": Prop("-svg-stroke-dashoffset"),

    "$77": Prop("-svg-stroke-linecap", {
        "$534": "butt",
        "$533": "round",
        "$341": "square",
        }),

    "$529": Prop("-svg-stroke-linejoin", {
        "$536": "bevel",
        "$535": "miter",
        "$533": "round",
        }),

    "$530": Prop("-svg-stroke-miterlimit"),

    "$76": Prop("-webkit-text-stroke-width"),

    "$173": Prop("-kfx-style-name"),

    "$150": Prop("border-collapse", {
        False: "separate",
        True: "collapse",
        }),

    "$148": Prop("-kfx-attrib-colspan"),
    "$149": Prop("-kfx-attrib-rowspan"),

    "$34": Prop("text-align", {
        "$320": "center",
        "$321": "justify",
        "$59": "left",
        "$61": "right",
        }),

    "$35": Prop("text-align-last", {
        "$383": "auto",
        "$320": "center",
        "$681": "end",
        "$321": "justify",
        "$59": "left",
        "$61": "right",
        "$680": "start",
        }),

    "$21": Prop("background-color"),
    "$528": Prop("background-image"),
    "$19": Prop("color"),

    "$707": Prop("text-combine-upright", {
        "$573": "all",
        }),

    "$718": Prop("text-emphasis-color"),

    "$719": Prop("-kfx-text-emphasis-position-horizontal", {
        "$58": "over",
        "$60": "under",
        }),

    "$720": Prop("-kfx-text-emphasis-position-vertical", {
        "$59": "left",
        "$61": "right",
        }),

    "$717": Prop("text-emphasis-style", {
        "$724": "filled",
        "$728": "filled circle",
        "$726": "filled dot",
        "$730": "filled double-circle",
        "$734": "filled sesame",
        "$732": "filled triangle",
        "$725": "open",
        "$729": "open circle",
        "$727": "open dot",
        "$731": "open double-circle",
        "$735": "open sesame",
        "$733": "open triangle",
        }),

    "$706": Prop("text-orientation", {
        "$383": "mixed",
        "$778": "sideways",
        "$779": "upright",
        }),

    "$36": Prop("text-indent"),

    "$41": Prop("text-transform", {
        "$373": "lowercase",
        "$349": "none",
        "$374": "capitalize",
        "$372": "uppercase",
        }),

    "$497": Prop("text-shadow"),
    "$58": Prop("top"),
    "$98": Prop("transform"),
    "$549": Prop("transform-origin"),

    "$23": Prop("text-decoration", {
        "$330": "underline dashed",
        "$331": "underline dotted",
        "$329": "underline double",
        "$349": None,
        "$328": "underline",
        }),

    "$24": Prop("text-decoration-color"),

    "$68": Prop("visibility", {
        False: "hidden",
        True: "visible"}),

    "$716": Prop("white-space", {
        "$715": "nowrap",
        }),

    "$56": Prop("width"),

    "$569": Prop("word-break", {
        "$570": "break-all",
        "$350": "normal",
        }),

    "$33": Prop("word-spacing"),

    "$560": Prop("writing-mode", {
        "$557": "horizontal-tb",
        "$559": "vertical-rl",
        "$558": "vertical-lr"}),

    "$650": Prop("-amzn-shape-outside"),

    "$646": Prop("-kfx-collision"),

    "$616": Prop("-kfx-attrib-epub-type", {
        "$617": "noteref"}),

    "$658": Prop("yj-float-align", {
        "$58": None,
        }),

    "$672": Prop("yj-float-bias", {
        "$671": None,
        }),

    "$628": Prop("clear", {
        "$59": "left",
        "$61": "right",
        "$421": "both",
        "$349": "none",
        }),

    "$673": Prop("yj-float-to-block", {
        False: None}),

    "$644": Prop("-amzn-page-footer", {
        "$442": "disable",
        "$441": "overlay",
        }),

    "$643": Prop("-amzn-page-header", {
        "$442": "disable",
        "$441": "overlay",
        }),

    "$645": Prop("-amzn-max-crop-percentage"),

    "$790": Prop("-kfx-heading-level"),

    "$640": Prop("-kfx-user-margin-bottom-percentage"),
    "$641": Prop("-kfx-user-margin-left-percentage"),
    "$642": Prop("-kfx-user-margin-right-percentage"),
    "$639": Prop("-kfx-user-margin-top-percentage"),

    "$633": Prop("-kfx-table-vertical-align", {
        "$350": "baseline",
        "$60": "bottom",
        "$320": "middle",
        "$58": "top",
        }),

    "$649": Prop("-kfx-attrib-epub-type", {
        "$442": "amzn:decorative",
        "$441": "amzn:not-decorative",
        }),

    "$788": Prop("page-break-after", {
        "$352": "always",
        "$383": "auto",
        "$353": "avoid",
        }),

    "$789": Prop("page-break-before", {
        "$352": "always",
        "$383": "auto",
        "$353": "avoid",
        }),
    }

YJ_PROPERTY_NAMES = set(YJ_PROPERTY_INFO.keys())


YJ_LENGTH_UNITS = {
    "$506": "ch",
    "$315": "cm",
    "$308": "em",
    "$309": "ex",
    "$317": "in",
    "$310": "lh",
    "$316": "mm",
    "$314": "%",
    "$318": "pt",
    "$319": "px",
    "$505": "rem",
    "$312": "vh",
    "$507": "vmax",
    "$313": "vmin",
    "$311": "vw",
    }


COLOR_YJ_PROPERTIES = {
    "$83", "$86", "$85", "$87", "$84", "$498",
    "$116", "$70", "$121", "$105", "$555",
    "$28", "$75", "$21", "$19", "$718",
    "$24",
    }

COLOR_NAME = {
    "#000000": "black",
    "#000080": "navy",
    "#0000ff": "blue",
    "#008000": "green",
    "#008080": "teal",
    "#00ff00": "lime",
    "#00ffff": "cyan",
    "#800000": "maroon",
    "#800080": "purple",
    "#808000": "olive",
    "#808080": "gray",
    "#ff0000": "red",
    "#ff00ff": "magenta",
    "#ffff00": "yellow",
    "#ffffff": "white",
    }

COLOR_HEX = {}
for h, n in COLOR_NAME.items():
    COLOR_HEX[n] = h

COLOR_NAMES = set(COLOR_HEX.keys())


GENERIC_FONT_NAMES = {
    "serif", "sans-serif", "cursive", "fantasy", "monospace",

    "Arial", "Caecilia", "Courier", "Georgia", "Lucida", "Times New Roman", "Trebuchet",

    "Amazon Ember", "Amazon Ember Bold", "Baskerville", "Bookerly", "Caecilia",
    "Caecilia Condensed", "Futura", "Helvetica", "OpenDyslexic", "Palatino",

    "Helvetica Light", "Helvetica Neue LT", "Noto Sans",

    "明朝",
    "ゴシック",

    "Source Code Pro",

    "Droid Sans", "Droid Serif", "Verdana",

    "Book Antiqua", "Calibri", "Calibri Light", "Cambria", "Comic Sans MS", "Courier New", "Lucida Sans Unicode",
    "Palatino Linotype", "Tahoma", "Trebuchet MS",

    "CCL", "Helvetica Neue", "Malabar", "Merriweather", "Sorts Mill Goudy",

    "Code2000", "Palatino LT Std", "Times",

    "ＭＳ 明朝",
    "@ＭＳ 明朝",
    "MS Mincho",
    "@MS Mincho",

    "AmazonEmber Medium",
    "Bookerly Italic",
    "BookerlyDisplay Regular",
    }

DEFAULT_FONT_NAMES = {"default", "$amzn_fixup_default_font$"}


MISSPELLED_FONT_NAMES = {
    "san-serif": "sans-serif",
    "ariel": "Arial",
    }


KNOWN_STYLES = {
    "-amzn-float": {"bottom", "top", "top,bottom"},
    "-amzn-max-crop-percentage": {
            "0 0 0 0"},
    "-amzn-page-align": {
            "all", "bottom", "bottom,left", "bottom,left,right", "bottom,right", "left", "left,right",
            "right", "top", "top,bottom,left", "top,bottom,right", "top,left", "top,left,right", "top,right"},
    "-amzn-page-footer": {"disable"},
    "-amzn-page-header": {"disable"},
    "-amzn-shape-outside": {"*"},
    "-webkit-border-horizontal-spacing": {"0"},
    "-webkit-border-vertical-spacing": {"0"},
    "-webkit-box-decoration-break": {"clone", "slice"},
    "-webkit-hyphens": {"auto", "manual", "none"},
    "-webkit-line-break": {"anywhere", "auto", "loose", "normal", "strict"},
    "-webkit-ruby-position": {"over", "under"},
    "-webkit-text-combine": {"horizontal", "none"},
    "-webkit-text-emphasis-color": {"#0"},
    "-webkit-text-emphasis-position": {"over right", "under left"},
    "-webkit-text-emphasis-style": {
        "filled", "filled circle", "filled dot", "filled double-circle", "filled sesame", "filled triangle",
        "open", "open circle", "open dot", "open double-circle", "open sesame", "open triangle"},
    "-webkit-text-orientation": {"mixed", "sideways", "upright"},
    "-webkit-text-stroke-color": {"#0"},
    "-webkit-text-stroke-width": {"0"},
    "-webkit-transform": {"*"},
    "-webkit-transform-origin": {"0 0"},
    "-webkit-writing-mode": {"horizontal-tb", "vertical-lr", "vertical-rl"},
    "background-clip": {"border-box", "content-box", "padding-box"},
    "background-color": {"#0"},
    "background-image": {"*"},
    "background-origin": {"border-box", "content-box"},
    "background-position": {"0 0"},
    "background-size": {"0 0", "auto 0", "0 auto", "contain", "cover"},
    "background-repeat": {"no-repeat", "repeat-x", "repeat-y"},
    "border-bottom-color": {"#0"},
    "border-bottom-left-radius": {"0"},
    "border-bottom-right-radius": {"0"},
    "border-bottom-style": {"dashed", "dotted", "double", "groove", "inset", "none", "outset", "ridge", "solid"},
    "border-bottom-width": {"0"},
    "border-collapse": {"collapse", "separate"},
    "border-color": {"#0"},
    "border-left-color": {"#0"},
    "border-left-style": {"dashed", "dotted", "double", "groove", "inset", "none", "outset", "ridge", "solid"},
    "border-left-width": {"0"},
    "border-right-color": {"#0"},
    "border-right-style": {"dashed", "dotted", "double", "groove", "inset", "none", "outset", "ridge", "solid"},
    "border-right-width": {"0"},
    "border-spacing": {"0 0"},
    "border-style": {"dashed", "dotted", "double", "groove", "inset", "none", "outset", "ridge", "solid"},
    "border-top-color": {"#0"},
    "border-top-left-radius": {"0"},
    "border-top-right-radius": {"0"},
    "border-top-style": {"dashed", "dotted", "double", "groove", "inset", "none", "outset", "ridge", "solid"},
    "border-top-width": {"0"},
    "border-width": {"0"},
    "bottom": {"0"},
    "box-decoration-break": {"clone", "slice"},
    "box-shadow": {"0 0 #0", "0 0 #0 inset", "0 0 0 #0", "0 0 0 #0 inset", "0 0 0 0 #0", "0 0 0 0 #0 inset"},
    "box-sizing": {"border-box", "content-box"},
    "clear": {"both", "left", "none", "right"},
    "color": {"#0"},
    "column-count": {"0", "auto"},
    "direction": {"ltr", "rtl"},
    "display": {"block", "inline", "inline-block", "none", "oeb-page-foot", "oeb-page-head", "inline-table"},
    "float": {"left", "right", "snap-block"},
    "font-family": {"*"},
    "font-size": {"0"},
    "font-style": {"italic", "normal", "oblique"},
    "font-variant": {"normal", "small-caps"},
    "font-weight": {"0", "bold", "normal"},
    "height": {"0"},
    "hyphens": {"auto", "manual", "none"},
    "left": {"0"},
    "letter-spacing": {"0", "normal"},
    "line-break": {"anywhere", "auto", "loose", "normal", "strict"},
    "line-height": {"0", "normal"},
    "list-style-image": {"*"},
    "list-style-position": {"inside", "outside"},
    "list-style-type": {
        "circle", "cjk-ideographic", "decimal", "decimal-leading-zero", "disc", "georgian", "katakana", "katakana-iroha",
        "lower-alpha", "lower-armenian", "lower-greek", "lower-roman", "none", "square", "upper-alpha", "upper-armenian",
        "upper-greek", "upper-roman"},
    "margin": {"0"},
    "margin-bottom": {"0"},
    "margin-left": {"0", "auto"},
    "margin-right": {"0", "auto"},
    "margin-top": {"0"},
    "max-height": {"0"},
    "max-width": {"0"},
    "min-height": {"0"},
    "min-width": {"0"},
    "orphans": {"0"},
    "outline-color": {"#0"},
    "outline-offset": {"0"},
    "outline-style": {"dashed", "dotted", "double", "groove", "inset", "none", "outset", "ridge", "solid"},
    "outline-width": {"0"},
    "overflow": {"hidden"},
    "padding": {"0"},
    "padding-bottom": {"0"},
    "padding-left": {"0"},
    "padding-right": {"0"},
    "padding-top": {"0"},
    "page-break-after": {"always", "auto", "avoid"},
    "page-break-before": {"always", "auto", "avoid"},
    "page-break-inside": {"auto", "avoid"},
    "position": {"absolute", "fixed", "relative"},
    "right": {"0"},
    "ruby-align": {"center", "space-around", "space-between", "start"},
    "ruby-position": {"over", "under"},
    "src": {"*"},
    "text-align": {"center", "justify", "left", "right"},
    "text-align-last": {"auto", "center", "end", "justify", "left", "right", "start"},
    "text-combine-upright": {"all", "none"},
    "text-decoration": {
            "line-through", "none !important", "overline", "underline",
            "overline dashed", "overline dotted", "overline double",
            "underline dashed", "underline dotted", "underline double",
            "line-through dashed", "line-through dotted", "line-through double",
            "overline underline", "line-through overline", "line-through underline",
            "line-through overline underline"},
    "text-decoration-color": {"#0"},
    "text-emphasis-color": {"#0"},
    "text-emphasis-position": {"over right", "under left"},
    "text-emphasis-style": {
        "filled", "filled circle", "filled dot", "filled double-circle", "filled sesame", "filled triangle",
        "open", "open circle", "open dot", "open double-circle", "open sesame", "open triangle"},
    "text-indent": {"0"},
    "text-orientation": {"mixed", "sideways", "upright"},
    "text-shadow": {"*"},
    "text-transform": {"capitalize", "none", "lowercase", "uppercase"},
    "top": {"0"},
    "transform": {"*"},
    "transform-origin": {"0 0"},
    "unicode-bidi": {"bidi-override", "embed", "isolate", "isolate-override", "normal", "plaintext"},
    "vertical-align": {"0", "baseline", "bottom", "middle", "sub", "super", "text-bottom", "text-top", "top"},
    "visibility": {"hidden", "visible"},
    "white-space": {"normal", "nowrap"},
    "widows": {"0"},
    "width": {"0"},
    "word-break": {"break-all", "normal"},
    "word-spacing": {"0", "normal"},
    "writing-mode": {"horizontal-tb", "vertical-lr", "vertical-rl"},
    "z-index": {"0"},
    }


ARBITRARY_VALUE_PROPERTIES = {
    "-amzn-shape-outside", "-kfx-attrib-xml-lang", "-kfx-layout-hints", "-kfx-style-name",
    "background-image", "font-family", "list-style-image", "src",
    }


COMPOSITE_SIDE_STYLES = [
    ["border-color", ["border-bottom-color", "border-left-color", "border-right-color", "border-top-color"]],
    ["border-style", ["border-bottom-style", "border-left-style", "border-right-style", "border-top-style"]],
    ["border-width", ["border-bottom-width", "border-left-width", "border-right-width", "border-top-width"]],
    ["margin", ["margin-bottom", "margin-left", "margin-right", "margin-top"]],
    ["padding", ["padding-bottom", "padding-left", "padding-right", "padding-top"]],
    ]

CONFLICTING_PROPERTIES = {
    "background": {
        "background-attachment", "background-clip", "background-color", "background-image",
        "background-origin", "background-position", "background-repeat", "background-size"},
    "border-color": {"border-bottom-color", "border-left-color", "border-right-color", "border-top-color"},
    "border-style": {"border-bottom-style", "border-left-style", "border-right-style", "border-top-style"},
    "border-width": {"border-bottom-width", "border-left-width", "border-right-width", "border-top-width"},
    "font": {"font-family", "font-size", "font-style", "font-variant", "font-weight"},
    "list-style": {"list-style-type", "list-style-position", "list-style-image"},
    "margin": {"margin-bottom", "margin-left", "margin-right", "margin-top"},
    "outline": {"outline-width", "outline-style", "outline-color"},
    "padding": {"padding-bottom", "padding-left", "padding-right", "padding-top"},
    }

for name, conf_set in list(CONFLICTING_PROPERTIES.items()):
    for conf in conf_set:
        if conf not in CONFLICTING_PROPERTIES:
            CONFLICTING_PROPERTIES[conf] = set()

        CONFLICTING_PROPERTIES[conf].add(name)


ALTERNATE_EQUIVALENT_PROPERTIES = {
    "box-decoration-break": "-webkit-box-decoration-break",
    "hyphens": "-webkit-hyphens",
    "line-break": "-webkit-line-break",
    "ruby-position": "-webkit-ruby-position",
    "text-combine-upright": "-webkit-text-combine",
    "text-emphasis-color": "-webkit-text-emphasis-color",
    "text-emphasis-position": "-webkit-text-emphasis-position",
    "text-emphasis-style": "-webkit-text-emphasis-style",
    "text-orientation": "-webkit-text-orientation",
    "transform": "-webkit-transform",
    "transform-origin": "-webkit-transform-origin",
    "writing-mode": "-webkit-writing-mode",
    }


HERITABLE_DEFAULT_PROPERTIES = {
    "-amzn-page-align": "none",
    "-kfx-attrib-xml-lang": None,
    "-kfx-link-color": None,
    "-kfx-user-margin-bottom-percentage": "100",
    "-kfx-user-margin-left-percentage": "100",
    "-kfx-user-margin-right-percentage": "100",
    "-kfx-user-margin-top-percentage": "100",
    "-kfx-visited-color": None,
    "-webkit-border-horizontal-spacing": "2px",
    "-webkit-border-vertical-spacing": "2px",
    "-webkit-text-stroke-color": None,
    "-webkit-text-stroke-width": "0",
    "border-collapse": "separate",
    "border-spacing": "2px 2px",
    "caption-side": "top",
    "color": None,
    "cursor": "auto",
    "direction": "ltr",
    "empty-cells": "show",
    "font": None,
    "font-family": DEFAULT_DOCUMENT_FONT_FAMILY,
    "font-feature-settings": None,
    "font-language-override": "normal",
    "font-kerning": "auto",
    "font-size": "1rem",
    "font-size-adjust": "none",
    "font-stretch": "normal",
    "font-style": "normal",
    "font-synthesis": "weight style",
    "font-variant": "normal",
    "font-weight": "normal",
    "hanging-punctuation": "none",
    "hyphens": None,
    "letter-spacing": "normal",
    "line-break": "auto",
    "line-height": "normal",
    "list-style": None,
    "list-style-image": "none",
    "list-style-position": "outside",
    "list-style-type": "disc",
    "orphans": "2",
    "overflow-wrap": "normal",
    "quotes": None,
    "ruby-align": "space-around",
    "ruby-merge": "separate",
    "ruby-position": "over",
    "tab-size": "8",
    "text-align": None,
    "text-align-last": "auto",
    "text-combine-upright": "none",
    "text-justify": "auto",
    "text-shadow": "none",
    "text-transform": "none",
    "text-indent": "0",
    "text-orientation": "mixed",
    "text-underline-position": "auto",
    "unicode-bidi": "normal",
    "visibility": "visible",
    "white-space": "normal",
    "widows": "2",
    "word-break": "normal",
    "word-spacing": "normal",
    "word-wrap": "normal",
    "writing-mode": "horizontal-tb",
    }

HERITABLE_PROPERTIES = set(HERITABLE_DEFAULT_PROPERTIES.keys())
HERITABLE_DEFAULTS_FILTERED = dict([(k, v) for k, v in HERITABLE_DEFAULT_PROPERTIES.items() if v is not None])


REVERSE_HERITABLE_PROPERTIES = HERITABLE_PROPERTIES - {
    "-amzn-page-align",
    "-kfx-user-margin-bottom-percentage",
    "-kfx-user-margin-left-percentage",
    "-kfx-user-margin-right-percentage",
    "-kfx-user-margin-top-percentage",
    "font-size",
    "line-height",
    }

NON_HERITABLE_DEFAULT_PROPERTIES = {
    "background-clip": "border-box",
    "background-color": "transparent",
    "background-image": "none",
    "background-origin": "padding-box",
    "background-position": "0% 0%",
    "background-repeat": "repeat",
    "background-size": "auto auto",
    "box-sizing": "content-box",
    "box-decoration-break": "slice",
    "column-count": "auto",
    "float": "none",
    "margin-bottom": "0",
    "margin-left": "0",
    "margin-right": "0",
    "margin-top": "0",
    "overflow": "visible",
    "padding-bottom": "0",
    "padding-left": "0",
    "padding-right": "0",
    "padding-top": "0",
    "page-break-after": "auto",
    "page-break-before": "auto",
    "page-break-inside": "auto",
    "position": "static",
    "text-decoration": "none",
    "text-emphasis-position": "over right",
    "transform": "none",
    "transform-origin": "50% 50% 0",
    "vertical-align": "baseline",
    "z-index": "auto",
    }


BUNDLED_STYLES_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "styles")

_bundled_style_cache = {}


def read_bundled_style(filename):
    if filename not in _bundled_style_cache:
        resource_name = "styles/%s" % filename
        data = None

        if __package__:
            try:
                data = pkgutil.get_data(__package__, resource_name)
            except (IOError, OSError):
                pass

        if data is None:
            base_path = os.path.dirname(os.path.abspath(__file__))
            zip_pos = base_path.lower().rfind(".zip")
            if zip_pos >= 0:
                zip_filename = base_path[:zip_pos + 4]
                resource_path = os.path.join(base_path[zip_pos + 5:], "styles", filename).replace(os.sep, "/")
                try:
                    with zipfile.ZipFile(zip_filename, "r") as zf:
                        data = zf.read(resource_path)
                except (IOError, OSError, KeyError, zipfile.BadZipFile):
                    pass

        if data is None:
            path = os.path.join(BUNDLED_STYLES_DIR, filename)
            with open(path, "rb") as f:
                data = f.read()

        _bundled_style_cache[filename] = data

    return _bundled_style_cache[filename]


AMAZON_SPECIAL_CLASSES = [
    "app-amzn-magnify",
    ]


INLINE_ELEMENTS = {"a", "bdo", "br", "img", "object", "rp", "ruby", "span"}


style_cache = {}


class KFX_EPUB_Properties(object):
    def __init__(self):
        self.css_rules = {}
        self.extra_css_rules = {}
        self.missing_special_classes = set()
        self.media_queries = collections.defaultdict(dict)
        self.present_font_names = set()
        self.used_font_names = set()
        self.font_name_replacements = {}
        self.fixed_font_names = {}
        self.font_faces = []
        self.incorrect_font_quoting = set()

        for name in GENERIC_FONT_NAMES:
            self.fix_font_name(name, add=True, generic=True)

    def Style(self, x):
        return Style(x)

    def process_content_properties(self, content):
        content_properties = {}
        for property_name in list(content.keys()):
            if property_name in YJ_PROPERTY_NAMES:
                content_properties[property_name] = content.pop(property_name)

        return self.convert_yj_properties(content_properties)

    def convert_yj_properties(self, yj_properties):
        declarations = {}

        for yj_property_name, yj_value in yj_properties.items():
            value = self.property_value(yj_property_name, yj_value)

            if yj_property_name in YJ_PROPERTY_INFO:
                property = YJ_PROPERTY_INFO[yj_property_name].name
            else:
                log.warning("unknown property name: %s" % yj_property_name)
                property = str(yj_property_name).replace("_", "-")

            if property == "position" and value in ["oeb-page-foot", "oeb-page-head"]:
                property = "display" if self.generate_epub2 and EMIT_OEB_PAGE_PROPS else None

            if (property is not None) and (value is not None):
                decl_value = declarations.get(property)
                if decl_value is not None and decl_value != value:
                    if property == "-kfx-attrib-epub-type":
                        vals = set(decl_value.split() + value.split())
                        for val in vals:
                            if not val.startswith("amzn:"):
                                log.error("Property %s has multiple incompatible values: \"%s\" and \"%s\"" % (
                                    property, decl_value, value))
                                break
                        else:
                            value = " ".join(sorted(list(vals)))

                    elif property == "text-decoration":
                        vals = set(decl_value.split() + value.split())
                        value = " ".join(sorted(list(vals)))

                    else:
                        log.error("Property %s has multiple values: \"%s\" and \"%s\"" % (property, decl_value, value))

                declarations[property] = value

        if declarations.get("text-combine-upright") == "all":
            self.text_combine_in_use = True
            if declarations.get("writing-mode") == "horizontal-tb":
                declarations.pop("writing-mode")

        if "-kfx-background-positionx" in declarations or "-kfx-background-positiony" in declarations:
            declarations["background-position"] = "%s %s" % (
                    declarations.pop("-kfx-background-positionx", "50%"), declarations.pop("-kfx-background-positiony", "50%"))

        if "-kfx-background-sizex" in declarations or "-kfx-background-sizey" in declarations:
            declarations["background-size"] = "%s %s" % (
                    declarations.pop("-kfx-background-sizex", "auto"), declarations.pop("-kfx-background-sizey", "auto"))

        if "-kfx-fill-color" in declarations or "-kfx-fill-opacity" in declarations:
            declarations["background-color"] = self.add_color_opacity(
                    declarations.pop("-kfx-fill-color", "#ffffff"),
                    declarations.pop("-kfx-fill-opacity", None))

        if "-kfx-text-emphasis-position-horizontal" in declarations or "-kfx-text-emphasis-position-vertical" in declarations:
            declarations["text-emphasis-position"] = " ".join(
                    [v for v in [declarations.pop("-kfx-text-emphasis-position-horizontal", None),
                                 declarations.pop("-kfx-text-emphasis-position-vertical", None)] if v])

        if "-kfx-keep-lines-together" in declarations:
            for prop, val in zip(["orphans", "widows"], declarations.pop("-kfx-keep-lines-together").split()):
                if val != "inherit":
                    declarations[prop] = val

        if ("text-decoration-color" in declarations and "text-decoration" not in declarations and
                declarations["text-decoration-color"] == "rgba(255,255,255,0)"):
            declarations.pop("text-decoration-color")
            declarations["text-decoration"] = "none !important"

        if FIX_NONSTANDARD_FONT_WEIGHT and "font-weight" in declarations and re.match(r"^[0-9]+$", declarations["font-weight"]):
            weight_num = int(declarations["font-weight"])
            declarations["font-weight"] = "normal" if weight_num <= 500 else "bold"

        for combined_prop, individual_props in COMPOSITE_SIDE_STYLES:
            val = declarations.get(combined_prop, None)
            if val is not None:
                for individual_prop in individual_props:
                    if individual_prop not in declarations:
                        declarations[individual_prop] = val

                declarations.pop(combined_prop)

        return self.Style(declarations)

    def property_value(self, yj_property_name, yj_value, svg=False, transform_matrix_swap=False):
        property_info = YJ_PROPERTY_INFO.get(yj_property_name, None)
        value_map = property_info.values if property_info is not None else None

        val_type = ion_type(yj_value)

        if val_type is IonStruct:
            if "$307" in yj_value:
                magnitude = yj_value.pop("$307")
                unit = yj_value.pop("$306")
                if unit not in YJ_LENGTH_UNITS:
                    log.error("Property %s has unknown unit: %s" % (yj_property_name, unit))

                if DETECT_LAYOUT_ENHANCER and yj_property_name == "$65" and unit == "$318" and magnitude in [216, 270, 324, 360]:
                    log.warning("YJImageLayoutEnhancer detected, max_width=%s" % value_str(magnitude, YJ_LENGTH_UNITS.get(unit, unit)))

                if FIX_PT_TO_PX and unit == "$318" and magnitude > 0:
                    if int(magnitude * 1000) % 225 == 0:
                        px_value = int(magnitude * 1000) / 450.0
                        magnitude = px_value
                        unit = "$319"

                if unit == "$319":
                    magnitude = self.adjust_pixel_value(magnitude)

                value = value_str(magnitude, YJ_LENGTH_UNITS.get(unit, unit))

                if USE_NORMAL_LINE_HEIGHT and yj_property_name == "$42" and value == NORMAL_LINE_HEIGHT_EM:
                    value = "normal"

            elif "$19" in yj_value:
                value = self.fix_color_value(yj_value.pop("$19"))

            elif "$499" in yj_value and "$500" in yj_value:
                values = []
                for sub_property in ["$499", "$500", "$501", "$502", "$498"]:
                    if sub_property in yj_value:
                        values.append(self.property_value(sub_property, yj_value.pop(sub_property)))

                if yj_value.pop("$336", False):
                    values.append("inset")

                value = " ".join(values)

            elif "$58" in yj_value and yj_property_name == "$549":
                values = []
                for sub_property in ["$59", "$58"]:
                    if sub_property in yj_value:
                        values.append(self.property_value(sub_property, yj_value.pop(sub_property)))
                    else:
                        log.error("Missing sub-property %s for %s" % (sub_property, yj_property_name))
                        values.append("50%")

                value = " ".join(values)

            elif "$58" in yj_value:
                values = []
                for sub_property in ["$58", "$61", "$60", "$59"]:
                    if sub_property in yj_value:
                        val = self.property_value(sub_property, yj_value.pop(sub_property))
                        if val != "0" and not val.endswith("%"):
                            log.error("Property %s has unexpected value: %s" % (yj_property_name, val))
                        values.append(val.replace("%", ""))
                    else:
                        log.error("Missing sub-property %s for %s" % (sub_property, yj_property_name))

                value = " ".join(values)

            elif "$175" in yj_value:
                yj_value.pop("$57", None)
                yj_value.pop("$56", None)
                value = self.property_value("$175", yj_value.pop("$175"))

            elif "$131" in yj_value or "$132" in yj_value:
                value = "%s %s" % (yj_value.pop("$131", "inherit"), yj_value.pop("$132", "inherit"))

            else:
                log.error("Property %s has unknown dict value content: %s" % (yj_property_name, repr(yj_value)))
                yj_value = {}
                value = "?"

            self.check_empty(yj_value, "Property %s value" % yj_property_name)

        elif val_type is IonString:
            if yj_property_name == "$11":
                value = self.fix_and_quote_font_family_list(yj_value)
            elif yj_property_name == "$10":
                value = self.fix_language(yj_value)
            else:
                value = yj_value

        elif val_type is IonSymbol:
            if yj_property_name == "$173":
                value = self.unique_part_of_local_symbol(yj_value)
                if not value:
                    value = None

            elif yj_property_name in {"$479", "$175", "$528"}:
                value = self.css_url(urlrelpath(
                        self.process_external_resource(yj_value).filename, ref_from=self.STYLES_CSS_FILEPATH))

            elif yj_property_name in COLOR_YJ_PROPERTIES:
                if yj_value == "$349":
                    value = self.fix_color_value(0)
                else:
                    log.warning("unexpected symbolic property value for %s: %s" % (yj_property_name, yj_value))

            else:
                if value_map is not None:
                    if yj_value in value_map:
                        value = value_map[yj_value]
                    else:
                        log.warning("unknown property value for %s: %s" % (yj_property_name, yj_value))
                        value = str(yj_value)
                else:
                    log.warning("unexpected symbolic property value for %s: %s" % (yj_property_name, yj_value))
                    value = str(yj_value)

                if yj_property_name == "$11":
                    value = self.fix_and_quote_font_family_list(value)

        elif val_type in [IonInt, IonFloat, IonDecimal]:
            value = value_str(yj_value)

            if yj_property_name in COLOR_YJ_PROPERTIES:
                if yj_property_name == "$70" and int(yj_value) & ALPHA_MASK == 0:
                    value = int(yj_value) | ALPHA_MASK

                value = self.fix_color_value(value)

            elif value != "0" and yj_property_name not in {
                    "$112", "$13",
                    "$148", "$149", "$645", "$647",
                    "$648", "$790", "$640",
                    "$641", "$642",
                    "$639", "$72",
                    "$126", "$125", "$42"} and not svg:

                value = value_str(self.adjust_pixel_value(yj_value))

                value += "px"

            else:
                value = value_str(yj_value)

        elif val_type is IonBool:
            if value_map is not None:
                if yj_value in value_map:
                    value = value_map[yj_value]
                else:
                    log.warning("unknown property value for %s: %s" % (yj_property_name, yj_value))
                    value = str(yj_value)
            else:
                log.warning("unexpected boolean property value for %s: %s" % (yj_property_name, yj_value))
                value = str(yj_value)

        elif val_type is IonList:
            if yj_property_name == "$650":
                value = self.process_polygon(yj_value)

            elif yj_property_name == "vertex_list" or yj_property_name == "$851":
                value = self.process_vertex_list(yj_value)

            elif yj_property_name == "$646" and len(yj_value) > 0:
                values = []
                for collision in yj_value:
                    if collision in COLLISIONS:
                        values.append(COLLISIONS[collision])
                    else:
                        log.error("Unexpected yj.collision value: %s" % str(collision))

                value = " ".join(sorted(values))

            elif yj_property_name == "$98":
                value = self.process_transform(yj_value, svg=svg, transform_matrix_swap=transform_matrix_swap)

            elif yj_property_name == "$497":
                values = []
                for text_shadow in yj_value:
                    values.append(self.property_value(yj_property_name, text_shadow))

                value = ", ".join(values)

            elif yj_property_name == "$761":
                values = []
                for layout_hint in yj_value:
                    element_name = LAYOUT_HINT_ELEMENT_NAMES.get(layout_hint)
                    if element_name:
                        values.append(element_name)
                    else:
                        log.warning("Unexpected layout_hint %s" % layout_hint)

                value = " ".join(values)

            elif yj_property_name == "$531":
                value = " ".join([self.property_value(yj_property_name, val_) for val_ in yj_value])

            else:
                log.error("unexpected IonList property value for %s: %s" % (yj_property_name, yj_value))
                value = "?"

        else:
            log.error("Property %s has unknown value format (%s): %s" % (
                            yj_property_name, type_name(yj_value), repr(yj_value)))
            value = "?"

        if (yj_property_name in {
                "$640", "$641",
                "$642", "$639"} and
                value not in ["100", "-100"]):
            log.error("Property %s has disallowed value: %s" % (yj_property_name, value))

        if yj_property_name in {"$32", "$33"} and value in {"0em", "0"}:
            value = "normal"

        return value

    def fixup_styles_and_classes(self):
        if STYLE_TEST:
            log.warning("Style simplification disabled")
            return

        self.css_rules = {}

        self.non_heritable_default_properties = self.Style(NON_HERITABLE_DEFAULT_PROPERTIES)
        # 轉成電書協 utility class 時，若該 class 不存在於樣式表（例如 95% 字級），補在 style-kfx.css。
        self.extra_css_rules = {}

        for book_part in self.book_parts:
            self.last_kfx_heading_level = "1"
            heritable_default_properties = self.Style(HERITABLE_DEFAULTS_FILTERED)
            lang = book_part.html.get(XML_LANG)
            if lang:
                heritable_default_properties.update(self.Style({"-kfx-attrib-xml-lang": lang}), replace=True)

            self.page_writing_mode = (self.get_style(book_part.body()).get("writing-mode", self.writing_mode) or
                                      self.writing_mode)
            self.simplify_styles(book_part.body(), book_part, heritable_default_properties)

            self.expand_block_margins(book_part)

            self.add_composite_and_equivalent_styles(book_part.body(), book_part)

        self.create_conditional_page_templates()

        style_counts = collections.defaultdict(lambda: 0)

        for book_part in self.book_parts:
            body = book_part.body()
            for e in body.iter("*"):
                class_name = e.get("class")
                if class_name and (class_name not in AMAZON_SPECIAL_CLASSES):
                    selector = class_selector(class_name)
                    if selector not in self.missing_special_classes:
                        log.error("Unexpected class found: %s" % class_name)
                        self.missing_special_classes.add(selector)

                if "style" in e.attrib:

                    style = self.get_style(e)
                    style_modified = False

                    style_attribs = style.partition(name_prefix="-kfx-attrib-", remove_prefix=True)
                    if style_attribs:
                        style_modified = True
                        for name, value in style_attribs.items():
                            if name.startswith("epub-"):
                                name = qname(EPUB_NS_URI, name.partition("-")[2])
                                if name == EPUB_TYPE:
                                    if self.generate_epub2:
                                        continue

                            elif name.startswith("xml-"):
                                name = qname(XML_NS_URI, name.partition("-")[2])

                            if name in ["colspan", "rowspan", "valign"] and e.tag not in ["tbody", "tr", "td"]:
                                log.error("Unexpected class_attribute in %s: %s" % (e.tag, name))

                            e.set(name, value)

                    if style_modified:
                        self.set_style(e, style)

                    if (style.get("writing-mode") == "vertical-rl" or
                            style.get("-webkit-writing-mode") == "vertical-rl" or
                            style.get("-epub-writing-mode") == "vertical-rl"):
                        book_part.has_vertical_rl_class = True

                    if (CVT_DIRECTION_PROPERTY_TO_MARKUP or not self.generate_epub2) and ("direction" in style or "unicode-bidi" in style):
                        unicode_bidi = style.get("unicode-bidi", "normal")

                        has_block = False
                        has_content = e.text
                        for ex in e.iterfind(".//*"):
                            if ex.tag in {
                                    "aside", "div", "figure", "h1", "h2", "h3", "h4", "h5", "h6", "iframe", "li",
                                    "ol", "p", "table", "td", "ul"}:
                                has_block = True

                            if ex.text or ex.tail or ex.tag in {"audio", "img", "li", MATH, "object", SVG, "video"}:
                                has_content = True

                            if has_block and has_content:
                                break

                        if not has_content:
                            style.pop("direction", None)
                            style.pop("unicode-bidi", None)
                            self.set_style(e, style)

                        elif unicode_bidi in ["embed", "normal"] or has_block:
                            if "direction" in style:
                                e.set("dir", style.pop("direction"))

                            style.pop("unicode-bidi", None)
                            self.set_style(e, style)

                        elif unicode_bidi in ["isolate", "bidi-override", "isolate-override"]:

                            bdx = etree.Element("bdo" if "override" in unicode_bidi else "bdi")
                            if "direction" in style:
                                bdx.set("dir", style.pop("direction"))

                            if e.tag != "img":
                                bdx.text = e.text
                                e.text = None

                                while len(e) > 0:
                                    ec = e[0]
                                    e.remove(ec)
                                    bdx.append(ec)

                                e.insert(0, bdx)

                            style.pop("unicode-bidi", None)
                            self.set_style(e, style)

                        else:
                            log.error("Cannot produce EPUB3 equivalent for: unicode-bidi:%s direction:%s" % (
                                    unicode_bidi, style.get("direction", "?")))

                    if REMOVE_EMPTY_NAMED_CLASSES and len(style.partition({"-kfx-style-name", "-kfx-layout-hints"}, keep_all=True)) == 0:
                        style.pop("-kfx-style-name", None)
                        style.pop("-kfx-layout-hints", None)
                        self.set_style(e, style)

                if "style" in e.attrib:
                    self.strip_reset_default_properties(e)

                if "style" in e.attrib:
                    style_counts[e.get("style")] += 1

        sorted_style_data = []
        known_class_name_count = collections.defaultdict(lambda: 0)

        for class_name in AMAZON_SPECIAL_CLASSES:
            known_class_name_count[class_name] = 2

        for style_str, count in sorted(style_counts.items(), key=lambda sc: -sc[1]):
            style = self.Style(style_str)

            class_name = re.sub(r"[^A-Za-z0-9_-]", "_", style.pop("-kfx-style-name", ""))

            if "-kfx-layout-hints" in style:
                class_name_prefix = "-".join(sorted(list(style.pop("-kfx-layout-hints").split())))
            else:
                class_name_prefix = DEFAULT_CLASS_NAME_PREFIX

            known_class_name_count[class_name_prefix] += 1

            class_name = self.prefix_unique_part_of_symbol(class_name, class_name_prefix)
            known_class_name_count[class_name] += 1
            sorted_style_data.append((style_str, style, class_name))

        classes = {}
        style_class_names = {}
        used_class_name_count = collections.defaultdict(lambda: 0)
        referenced_classes = set()
        selector_classes = set()

        for style_str, style, class_name in sorted_style_data:
            if known_class_name_count[class_name] > 1 or class_name in classes:
                while True:
                    unique = used_class_name_count[class_name]
                    used_class_name_count[class_name] += 1
                    class_name = "%s-%d" % (class_name, unique)
                    if known_class_name_count[class_name] == 0 and class_name not in classes:
                        break

            for prop_name_prefix, selector_suffix in [
                        ("-kfx-firstline-", "::first-line"),
                        ("-kfx-link-", ":link"),
                        ("-kfx-visited-", ":visited")]:

                selector_style = style.partition(name_prefix=prop_name_prefix, remove_prefix=True)
                if selector_style:
                    self.css_rules[class_selector(class_name) + selector_suffix] = selector_style
                    selector_classes.add(class_name)

            classes[class_name] = style
            style_class_names[style_str] = class_name

        style_usage = self.collect_style_usage(style_class_names)
        self._dump_classes_debug(classes, style_usage)
        self.prune_redundant_class_styles(sorted_style_data, style_class_names, style_usage)

        # 記錄轉換前的樣式，供 fixed-layout 頁面直接內嵌（那些頁面不套用樣式表）。
        original_styles = {name: self.Style(dict(style.items())) for name, style in classes.items()}
        class_utilities = self.map_class_styles_to_utility(classes, style_usage)

        for book_part in self.book_parts:
            body = book_part.body()
            for e in body.iter("*"):
                style_str = e.get("style", "")
                if style_str in style_class_names:
                    class_name = style_class_names[style_str]
                    class_style = classes[class_name]

                    if ((KEEP_STYLES_INLINE or book_part.is_fxl) and
                            class_name not in selector_classes and
                            "-kfx-media-query" not in class_style):
                        e.set("style", original_styles[class_name].tostring())
                        self.inventory_style(class_style)
                        continue

                    self._dump_element_debug(book_part, e, class_name, class_style, class_utilities)

                    for utility_class in class_utilities.get(class_name, []):
                        self.add_class(e, utility_class)

                    if len(class_style) == 0 and class_name not in selector_classes:
                        e.attrib.pop("style", None)
                    else:
                        self.add_class(e, class_name, before=True)
                        referenced_classes.add(class_name)
                        e.attrib.pop("style", None)

                elif style_str:
                    log.warning("Style has no class name: %s" % style_str)

        for class_name, class_style in classes.items():
            if class_name in referenced_classes:
                media_query = class_style.pop("-kfx-media-query")
                if class_style:
                    target = self.media_queries[media_query] if media_query else self.css_rules
                    target[class_selector(class_name)] = class_style

        self.update_vertical_rl_class_usage()

        for class_style in list(self.css_rules.values()) + self.font_faces:
            self.inventory_style(class_style)

        for mq_classes in self.media_queries.values():
            for class_style in mq_classes.values():
                self.inventory_style(class_style)

    def _dump_element_debug(self, book_part, e, class_name, class_style, class_utilities):
        path = os.environ.get("KFX_DUMP_ELEMS")
        if not path:
            return
        row = {
            "page": getattr(book_part, "filename", ""),
            "tag": str(e.tag).rsplit("}", 1)[-1],
            "text": "".join(e.itertext())[:24],
            "kfx_class": class_name,
            "utilities": list(class_utilities.get(class_name, [])),
            "remaining": sorted(k for k in class_style.keys() if not k.startswith("-kfx-")),
        }
        with io.open(path, "a", encoding="utf-8") as f:
            f.write(json.dumps(row, ensure_ascii=False) + "\n")

    def _dump_classes_debug(self, classes, usage):
        path = os.environ.get("KFX_DUMP_CLASSES")
        if not path:
            return
        rows = []
        for class_name, style in classes.items():
            info = usage.get(class_name)
            rows.append({
                "class": class_name,
                "decls": {k: v for k, v in style.items() if not k.startswith("-kfx-")},
                "tags": sorted(info.tags) if info else [],
                "direction": info.direction if info else None,
                "font_px": info.font_px if info else None,
            })
        with io.open(path, "a", encoding="utf-8") as f:
            for row in rows:
                f.write(json.dumps(row, ensure_ascii=False) + "\n")

    def collect_style_usage(self, style_class_names):
        usage = {}
        for book_part in self.book_parts:
            direction = None
            if not (book_part.is_fxl or book_part.is_nav or not book_part.writing_mode_value):
                direction = "vrtl" if book_part.writing_mode_value.startswith("vertical") else "hltr"

            for e in book_part.body().iter("*"):
                style_str = e.get("style", "")
                if style_str not in style_class_names:
                    continue
                class_name = style_class_names[style_str]
                info = usage.get(class_name)
                if info is None:
                    info = usage[class_name] = StyleUsage()

                tag = str(e.tag).rsplit("}", 1)[-1]
                info.tags.add(tag)
                info.all_headings = info.all_headings and tag in HEADING_TAGS
                info.all_links = info.all_links and self.text_only_inside_links(e)
                info.all_hr = info.all_hr and tag == "hr"
                info.body_only = info.body_only and tag == "body"
                if tag == "body":
                    inline_children = any(str(c.tag).rsplit("}", 1)[-1] in INLINE_TAGS for c in e)
                    info.all_body_no_text = (info.all_body_no_text and
                                             not (e.text or "").strip() and
                                             not inline_children and len(e) > 0)

                if direction:
                    if not info.has_direction:
                        info.has_direction = True
                        info.direction = direction
                    elif info.direction != direction:
                        info.direction = None

                font_px = self.style_font_px(self.get_style(e))
                if font_px is not None:
                    info.font_px = font_px

        return usage

    @staticmethod
    def style_font_px(style):
        value = style.get("font-size")
        if not value:
            return None
        match = re.fullmatch(r"(\d+(?:\.\d+)?)(rem|em|%|px)", value)
        if not match:
            return None
        magnitude = float(match.group(1))
        unit = match.group(2)
        if unit in ("rem", "em"):
            return magnitude * 16.0
        if unit == "%":
            return magnitude / 100.0 * 16.0
        return magnitude

    def map_class_styles_to_utility(self, classes, usage):
        """把可以完整表達的宣告換成電書協 utility class，剩下的宣告留在原規則。"""
        class_utilities = {}
        for class_name, style in classes.items():
            info = usage.get(class_name) or StyleUsage()

            # <hr>：電書協的 .hltr/.vrtl hr 特異度更高，會覆蓋樣式與邊距；
            # 若寬度、顏色又與 hr 基本規則相同，整個樣式就是多餘的。
            if info.all_hr and self.is_redundant_hr_style(style):
                style.clear()
                continue

            # 標題上「一行高」的段前距（1rem）：bw 沒有這個宣告。
            if (info.all_headings and info.font_px > 0 and
                    self.is_one_rem_margin(style.get("margin-top"), info.font_px)):
                style.pop("margin-top", None)

            utilities = []

            hanging = self.try_map_hanging_indent(style, info.direction)
            if hanging:
                utilities.append(hanging)

            # body 的 text-indent 只會被後方的 p/div reset 蓋掉（沒有直接文字時）。
            if not hanging and info.body_only and info.all_body_no_text:
                style.pop("text-indent", None)

            utilities.extend(self.try_map_borders(style))

            image_only = info.tags == {"img"}
            font_scale = (info.font_px if info.font_px > 0 else 16.0) / 16.0
            for name in list(style.keys()):
                mapping = self.map_utility_property(name, style.get(name), info.direction,
                                                    image_only=image_only, font_scale=font_scale)
                if mapping is None:
                    continue
                utility_class, extra_declaration = mapping
                utilities.append(utility_class)
                style.pop(name, None)
                selector = class_selector(utility_class)
                if extra_declaration is not None and selector not in self.extra_css_rules:
                    self.extra_css_rules[selector] = extra_declaration

            if utilities:
                class_utilities[class_name] = utilities

        return class_utilities

    def inline_remaining_generated_styles(self):
        """還留在生成 class 裡的宣告直接寫進元素的 style，不再產生 style-kfx.css。"""
        for book_part in self.book_parts:
            if book_part.is_nav or book_part.is_fxl:
                continue
            for elem in book_part.body().iter("*"):
                names = []
                for name in (elem.get("class") or "").split():
                    selector = class_selector(name)
                    if selector not in self.css_rules and selector not in self.extra_css_rules:
                        continue
                    if any(key.startswith(selector + ":") for key in self.css_rules):
                        continue
                    names.append(name)
                if not names:
                    continue

                merged = self.Style(elem.get("style") or "")
                for name in names:
                    selector = class_selector(name)
                    if selector in self.css_rules:
                        merged.update(self.css_rules[selector], replace=True)
                    if selector in self.extra_css_rules:
                        merged.update(self.Style(self.extra_css_rules[selector]), replace=True)
                        del self.extra_css_rules[selector]
                    self.remove_class(elem, name)
                # 固定屬性順序（其他屬性 → class → style），讓 C# 與 Python 的輸出一致。
                class_value = elem.get("class")
                style_value = merged.tostring()
                others = [(k, v) for k, v in elem.attrib.items() if k not in ("class", "style")]
                elem.attrib.clear()
                for key, value in others:
                    elem.attrib[key] = value
                if class_value is not None:
                    elem.set("class", class_value)
                elem.set("style", style_value)

    @staticmethod
    def is_one_rem_margin(value, font_px):
        match = re.fullmatch(r"(\d+(?:\.\d+)?)em", value or "")
        if not match:
            return False
        return abs(float(match.group(1)) * font_px - 16.0) <= 0.5

    @staticmethod
    def is_redundant_hr_style(style):
        has_border = False
        for name, value in style.items():
            if name.startswith("margin-"):
                continue
            if name == "border-width" or name.endswith("-width"):
                if value != "1px":
                    return False
                continue
            if name == "border-color" or name.endswith("-color"):
                if value not in ("black", "#000000", "#000"):
                    return False
                continue
            if name == "border-style" or name.endswith("-style"):
                if value not in ("solid", "none"):
                    return False
                has_border = True
                continue
            return False
        return has_border

    # 負 text-indent 與同值 padding 合起來才是電書協的 h-indent，不能拆開處理。
    def keeps_hanging_indent(self, sty):
        match = re.fullmatch(r"-(\d+(?:\.\d+)?)em", sty.get("text-indent", "") or "")
        if not match:
            return False
        writing_mode = sty.get("writing-mode", self.page_writing_mode)
        pad_side = "padding-top" if self.is_vertical_writing_mode(writing_mode) else "padding-left"
        return sty.get(pad_side) == match.group(1) + "em"

    # KFX 百分比邊距的版面基準寬度；由 10%↔4em、15%↔6em、2.5%↔1em 三組對照推得。
    KFX_LAYOUT_WIDTH_EM = 40

    @classmethod
    def try_map_hanging_indent(cls, style, direction):
        """padding-top/left 與負 text-indent 同值 → 電書協的 h-indent（方向相依）。
        KFX 也可能用百分比表示同一件事：margin M% + text-indent -M/(1-M)%。"""
        if not direction:
            return None

        percent_indent = re.fullmatch(r"-(\d+(?:\.\d+)?)%", style.get("text-indent") or "")
        if percent_indent:
            start_margin = "margin-top" if direction == "vrtl" else "margin-left"
            margin = re.fullmatch(r"(\d+(?:\.\d+)?)%", style.get(start_margin) or "")
            if not margin:
                return None
            margin_percent = float(margin.group(1))
            indent_percent = float(percent_indent.group(1))
            if margin_percent <= 0 or margin_percent >= 100:
                return None
            # 百分比 text-indent 是相對元素自己的內容寬度，因此要扣掉 margin 後的寬度。
            if abs(indent_percent - margin_percent / (1 - margin_percent / 100.0)) > 0.01:
                return None

            em_magnitude = margin_percent * cls.KFX_LAYOUT_WIDTH_EM / 100.0
            if abs(em_magnitude - round(em_magnitude)) > 1e-6:
                return None
            em_value = int(round(em_magnitude))
            if not (1 <= em_value <= 10):
                return None
            name = "h-indent-%dem" % em_value
            if name not in utility_class_names():
                return None
            style.pop(start_margin, None)
            style.pop("text-indent", None)
            return name

        match = re.fullmatch(r"-(\d+(?:\.\d+)?)em", style.get("text-indent") or "")
        if not match:
            return None
        magnitude = float(match.group(1))
        pad_side = "padding-top" if direction == "vrtl" else "padding-left"
        padding = re.fullmatch(r"(\d+(?:\.\d+)?)em", style.get(pad_side) or "")
        if not padding or abs(float(padding.group(1)) - magnitude) > 1e-6:
            return None
        if magnitude != int(magnitude) or not (1 <= magnitude <= 10):
            return None
        name = "h-indent-%dem" % int(magnitude)
        if name not in utility_class_names():
            return None
        style.pop("text-indent", None)
        style.pop(pad_side, None)
        return name

    @staticmethod
    def try_map_borders(style):
        """框線：四邊 solid、單邊 solid、上下／左右 solid。"""
        sides = ["top", "right", "bottom", "left"]
        styles = {side: style.get("border-%s-style" % side) for side in sides}
        width = style.get("border-width") or style.get("border-top-width")
        color = style.get("border-color") or style.get("border-top-color")

        shorthand = style.get("border-style")
        if shorthand:
            parts = shorthand.split()
            if len(parts) == 1:
                expanded = parts * 4
            elif len(parts) == 2:
                expanded = [parts[0], parts[1], parts[0], parts[1]]
            elif len(parts) == 4:
                expanded = parts
            else:
                return []
            for i, side in enumerate(sides):
                styles[side] = expanded[i]
        elif any(v is None for v in styles.values()):
            return []

        if any(v not in ("solid", "none") for v in styles.values()):
            return []
        if width is not None and width != "1px":
            return []
        if color is not None and color not in ("black", "#000000", "#000"):
            return []

        solid = [side for side in sides if styles[side] == "solid"]
        classes = []
        if len(solid) == 4:
            classes.append("k-solid")
        elif solid == ["top", "bottom"]:
            classes.append("k-solid-topbottom")
        elif solid == ["left", "right"]:
            classes.append("k-solid-rightleft")
        elif len(solid) == 1:
            classes.extend(["k-solid-" + solid[0], "k-1px"])
        else:
            return []

        if len(solid) != 4:
            if width == "1px" and "k-1px" not in classes:
                classes.append("k-1px")
            if color in ("black", "#000000", "#000"):
                classes.append("k-black")

        for side in sides:
            style.pop("border-%s-style" % side, None)
            style.pop("border-%s-width" % side, None)
            style.pop("border-%s-color" % side, None)
        for name in ("border-style", "border-width", "border-color",
                     "border-top-width", "border-top-color"):
            style.pop(name, None)
        return list(dict.fromkeys(classes))

    def map_utility_property(self, name, value, direction=None, image_only=False, font_scale=1.0):
        """回傳 (utility class, 需補的宣告或 None)；無法對應時回傳 None。"""
        value = (value or "").lower()

        if name.startswith(("margin", "padding")):
            return self.map_logical_box(name, value, direction, font_scale)

        if name == "text-align":
            utility_class = {"right": "align-end", "left": "align-left",
                             "center": "align-center", "justify": "align-justify"}.get(value)
            return (utility_class, None) if utility_class else None

        if name == "font-weight":
            utility_class = {"bold": "bold", "normal": "font-weight-normal"}.get(value)
            return (utility_class, None) if utility_class else None

        if name == "font-family":
            if "sans-serif" in value:
                return "gfont", None
            if "serif" in value:
                return "mfont", None
            return None

        if name == "font-size":
            return self.map_font_size(value)

        if name in ("height", "width", "max-height", "max-width"):
            return self.map_box_size(name, value, image_only)

        if name == "word-break":
            utility_class = {"break-all": "word-break-break-all", "keep-all": "word-break-keep-all",
                             "normal": "word-break-normal"}.get(value)
            return (utility_class, None) if utility_class else None

        if name == "line-break":
            return ("line-break-" + value, None) if value in ("auto", "loose", "normal", "strict") else None

        if name == "text-orientation":
            return (value, None) if value in ("sideways", "upright") else None

        if name == "text-emphasis-style":
            utility_class = {"filled sesame": "em-sesame", "open sesame": "em-sesame-open"}.get(value)
            return (utility_class, None) if utility_class else None

        # text-combine-upright 保留給 normalize_tcy_spans 判斷（會改寫成 tcy class）。
        return None

    @classmethod
    def map_logical_box(cls, name, value, direction, font_scale=1.0):
        """邊距／內距：依頁面方向換成邏輯 class（m-start / p-after …）；行首字下げ用 start-*。

        值不在電書協網格時，會再用字級比例試一次：KFX 有些長度是以元素自身字級為單位，
        例如 1.3em 標題的 0.769231em 其實就是 1em（bw 用 start-1em）。
        """
        if not direction:
            return None

        mapped = cls.map_logical_box_value(name, value, direction)
        if mapped is not None:
            return mapped

        if abs(font_scale - 1.0) < 1e-6:
            return None
        match = re.fullmatch(r"(\d+(?:\.\d+)?)(em|%)", value or "")
        if not match:
            return None
        scaled = float(match.group(1)) * font_scale
        scaled_text = ("%.4f" % scaled).rstrip("0").rstrip(".") + match.group(2)
        return cls.map_logical_box_value(name, scaled_text, direction)

    @staticmethod
    def map_logical_box_value(name, value, direction):
        if not direction:
            return None

        if name in ("margin", "padding"):
            size = box_size_name(value)
            if not size:
                return None
            shorthand = ("m-" if name == "margin" else "p-") + size
            return (shorthand, None) if shorthand in utility_class_names() else None

        side = name.rsplit("-", 1)[-1]
        if side not in ("top", "bottom", "left", "right"):
            return None
        if direction == "vrtl":
            logical = {"top": "start", "bottom": "end", "left": "after", "right": "before"}[side]
        else:
            logical = {"top": "before", "bottom": "after", "left": "start", "right": "end"}[side]

        is_margin = name.startswith("margin")
        size = box_size_name(value)
        if not size:
            return None

        candidates = []
        if is_margin and logical == "start" and size.endswith("em"):
            candidates.append("start-" + size)
        candidates.append(("%s-%s-%s" % ("m" if is_margin else "p", logical, size)))

        names = utility_class_names()
        for candidate in candidates:
            if candidate in names:
                return candidate, None
        return None

    def map_font_size(self, value):
        match = re.fullmatch(r"(\d+(?:\.\d+)?)(rem|%|em)", value)
        if not match:
            return None
        magnitude = float(match.group(1))

        if match.group(2) == "em":
            em_name = self.em_size_name(magnitude)
            if em_name not in UTILITY_FONT_EM_SIZES:
                return None
            return "font-" + em_name, None

        # 根字級 16px、body 100%：rem 與百分比等價（已確認沒有巢狀字級）。
        percent = round(magnitude * 100 if match.group(2) == "rem" else magnitude, 4)
        if percent != int(percent):
            return None
        digits = "%03d" % int(percent)
        class_name = "font-%sper" % digits
        extra = None if class_name in utility_class_names() else "font-size: %d%%" % int(percent)
        return class_name, extra

    def map_box_size(self, name, value, image_only=False):
        if value == "auto" and name in ("height", "width"):
            return name + "-auto", None
        if value == "none" and name in ("max-height", "max-width"):
            return name + "-none", None

        match = re.fullmatch(r"(\d+(?:\.\d+)?)(em|%)", value)
        if not match:
            return None
        magnitude = float(match.group(1))

        if match.group(2) == "%":
            magnitude = round(magnitude, 4)
            if magnitude != int(magnitude):
                return None
            # 圖片寬度百分比在 KFX 是相對 40em 版面的；電書協用 em 表示同一件事。
            if image_only and name == "width" and magnitude < 100:
                em_name = self.em_size_name(float(magnitude) * self.KFX_LAYOUT_WIDTH_EM / 100.0)
                if ("width-" + em_name) in utility_class_names():
                    return "width-" + em_name, None

            digits = "%03d" % int(magnitude)
            class_name = "%s-%sper" % (name, digits)
            extra = None if class_name in utility_class_names() else "%s: %d%%" % (name, int(magnitude))
            return class_name, extra

        em_name = self.em_size_name(magnitude)
        if em_name not in UTILITY_BOX_EM_SIZES:
            return None
        return "%s-%s" % (name, em_name), None

    @staticmethod
    def em_size_name(magnitude):
        if magnitude == int(magnitude):
            return "%dem" % int(magnitude)
        return ("%.2f" % magnitude).replace(".", "em")

    def prune_redundant_class_styles(self, sorted_style_data, style_class_names, usage):
        """移除與電書協樣式表重複、或原版 EPUB 沒有的宣告。"""
        for style_str, style, class_name in sorted_style_data:
            info = usage.get(style_class_names.get(style_str))
            if info is None:
                continue

            # page-break-inside 仍用於圖片外框的辨識（normalize_fit_image_wrappers），只在標題上省略。
            if info.all_headings:
                style.pop("page-break-inside", None)

            if not info.all_links:
                continue

            # style-standard.css 的 a:link 已是 #0000ff。
            if style.get("color") in ("blue", "#0000ff"):
                style.pop("color", None)
            # .vrtl a / .hltr a 已提供方向正確的 overline / underline。
            if style.get("text-decoration") == "overline":
                style.pop("text-decoration", None)

    @staticmethod
    def text_only_inside_links(elem):
        """判斷元素的可見文字是否全部位於 <a> 之內。"""
        has_text = [False]

        def walk(node, in_link):
            if node.text and node.text.strip():
                has_text[0] = True
                if not in_link:
                    return False

            for child in node:
                child_is_link = str(child.tag) in ("a", "{http://www.w3.org/1999/xhtml}a")
                if not walk(child, in_link or child_is_link):
                    return False
                if child.tail and child.tail.strip():
                    has_text[0] = True
                    if not in_link:
                        return False

            return True

        is_link = str(elem.tag) in ("a", "{http://www.w3.org/1999/xhtml}a")
        return walk(elem, is_link) and has_text[0]

    def update_vertical_rl_class_usage(self):
        vertical_rl_classes = set()

        for selector, style in self.css_rules.items():
            if (selector.startswith(".") and
                    not any(c in selector for c in " ,:#[]>") and
                    (style.get("writing-mode") == "vertical-rl" or
                     style.get("-webkit-writing-mode") == "vertical-rl" or
                     style.get("-epub-writing-mode") == "vertical-rl")):
                vertical_rl_classes.add(selector[1:])

        self.vertical_rl_classes = vertical_rl_classes

        for book_part in self.book_parts:
            if not vertical_rl_classes or book_part.has_vertical_rl_class:
                continue

            body = book_part.body()
            for elem in [body] + list(body.iter("*")):
                if vertical_rl_classes.intersection(elem.get("class", "").split()):
                    book_part.has_vertical_rl_class = True
                    break

    def inventory_style(self, style):
        reported = set()
        for key, value in style.items():
            simple_value = " ".join(zero_quantity(v) for v in value.split())
            if ((simple_value not in KNOWN_STYLES.get(key, set())) and ("*" not in KNOWN_STYLES.get(key, set())) and
                    (key, value) not in reported):
                log.error("Unexpected style definition: %s: %s" % (key, value))

                reported.add((key, value))

    def update_default_font_and_language(self):

        content_font_families = collections.defaultdict(lambda: 0)
        content_languages = collections.defaultdict(lambda: 0)

        def scan_content(elem, font_family, lang):

            style = elem.get("style", "")
            if "font-family" in style or "-kfx-attrib-xml-lang" in style:
                sty = self.Style(style)
                font_family = sty.get("font-family", font_family)
                lang = sty.get("-kfx-attrib-xml-lang", lang)

            text_len = len(elem.text or "")

            for e in elem.iterfind("*"):
                if e.tag != "head":
                    text_len += scan_content(e, font_family, lang)

            if text_len:
                content_font_families[font_family] += text_len
                content_languages[lang] += text_len

            return len(elem.tail or "")

        for book_part in self.book_parts:
            scan_content(book_part.html, None, None)

        best_font_family, best_merit = self.default_font_family, 0
        default_font_family_pattern = re.compile(re.escape(self.default_font_family) + "(,.+)?$")

        for font_family, merit in content_font_families.items():
            if merit > best_merit and font_family and re.match(default_font_family_pattern, font_family):
                best_font_family, best_merit = font_family, merit

        self.default_font_family = best_font_family

        best_language, best_merit = self.language, 0

        if self.language:
            default_language_pattern = re.compile(re.escape(self.language) + "(-.+)?$", re.IGNORECASE)

            for language, merit in content_languages.items():
                if merit > best_merit and language and re.match(default_language_pattern, language):
                    best_language, best_merit = language, merit

            self.language = best_language

    def set_html_defaults(self):
        for book_part in self.book_parts:
            body = book_part.body()
            body_sty = self.get_style(body)

            # 封面頁也要記錄書寫方向，輸出 <html class="vrtl|hltr"> 時才有依據。
            book_part.writing_mode_value = body_sty.get("writing-mode", self.writing_mode)

            if book_part.is_cover_page:
                continue

            if "font-family" not in body_sty and self.default_font_family:
                body_sty["font-family"] = self.default_font_family

            if "font-size" not in body_sty:
                body_sty["font-size"] = self.default_font_size

            if "line-height" not in body_sty:
                body_sty["line-height"] = self.default_line_height

            if "writing-mode" not in body_sty:
                body_sty["writing-mode"] = self.writing_mode

            self.set_style(body, body_sty)

            if self.language:
                book_part.html.set(XML_LANG, self.language)

    def simplify_styles(self, elem, book_part, inherited_properties, default_ordered_list_value=None, known_width=True):
        inherited_properties = inherited_properties.copy()

        if split_value(inherited_properties.get("font-size", ""))[1] == "em":
            inherited_properties["font-size"] = "1em"

        sty = inherited_properties.copy().update(self.get_style(elem), replace=True)
        orig_sty = sty.copy()

        sty.pop("-kfx-render", None)

        # 電書協的中央寄せ樣式（span + div）靠 inline 元素上的 width/height 生效，
        # 因此可重排頁也要補上 display:inline-block（不限 fixed-layout）。
        if (elem.tag in {"a", "span"} and sty.get("display", "inline") == "inline" and
                ("height" in sty or "width" in sty)):
            sty["display"] = "inline-block"

        if sty.get("position", "static") == "static":
            for positioning in ["top", "bottom", "left", "right"]:
                if positioning in sty:
                    if sty[positioning] == "0":
                        sty.pop(positioning)
                    else:
                        log.warning("%s style with unexpected %s position in %s: %s" % (
                            elem.tag, positioning, book_part.filename, sty.tostring()))

        sty["border-spacing"] = "%s %s" % (sty["-webkit-border-horizontal-spacing"], sty["-webkit-border-vertical-spacing"])

        sides = []
        for s in ["top", "bottom", "left", "right"]:
            if sty.pop("-kfx-user-margin-%s-percentage" % s, "100") == "-100":
                sides.append(s)

        page_align = sty["-amzn-page-align"] = (",".join(sides) if len(sides) < 4 else "all") if len(sides) > 0 else "none"

        for name, val in list(sty.items()):
            if name in {"padding", "padding-top", "padding-bottom", "padding-left", "padding-right"} and val.startswith("-"):
                log.warning("Discarding invalid %s: %s" % (name, val))
                sty.pop(name)

            elif name not in ARBITRARY_VALUE_PROPERTIES:
                quantity, unit = split_value(val)

                if unit == "lh" and quantity is not None:
                    if name == "line-height":
                        # 與 bw 一致：不輸出 KFX 的 line-height，讓元素繼承 body 的 1.75。
                        sty.pop(name)
                        continue

                    writing_mode = sty.get("writing-mode", self.page_writing_mode)
                    side = self.block_margin_side(name, writing_mode)
                    if side is not None:
                        lines = self.count_blank_lines(elem, name, quantity, sty, writing_mode)
                        if lines > 0:
                            sty.pop(name)
                            self.add_blank_marker(sty, name, side, lines, quantity * LINE_HEIGHT_SCALE_FACTOR)
                            continue

                    sty[name] = value_str(quantity * LINE_HEIGHT_SCALE_FACTOR, "em")
                    quantity, unit = split_value(sty[name])

                elif unit == "em" and self.has_box_border_or_padding(sty):
                    # 框線方塊本身的 em margin 也可能帶一個空行（例如特典框）。
                    writing_mode = sty.get("writing-mode", self.page_writing_mode)
                    side = self.block_margin_side(name, writing_mode)
                    if side is not None and self.is_vertical_writing_mode(writing_mode):
                        font_size_rem = self.resolve_font_size_rem(sty.get("font-size"))
                        exact_lines = quantity * font_size_rem / BLANK_LINE_REM
                        rounded = int(exact_lines + decimal.Decimal("0.5"))
                        lines = max(0, rounded - 1)
                        if lines > 0:
                            sty.pop(name)
                            self.add_blank_marker(sty, name, side, lines, quantity)
                            continue

                if unit == "rem" and quantity is not None and (self.generate_epub2 or self.GENERATE_EPUB2_COMPATIBLE):
                    if name == "font-size":
                        base_font_size = inherited_properties["font-size"]
                    else:
                        base_font_size = orig_sty["font-size"]

                    base_font_size_quantity, base_font_size_unit = split_value(base_font_size)

                    if base_font_size_unit == "rem":
                        quantity = quantity / base_font_size_quantity
                        unit = "em"
                    elif base_font_size_unit == "em":
                        unit = "em"
                    else:
                        log.error("Cannot convert %s:%s with incorrect base font size units %s" % (name, val, base_font_size))

                    if name == "line-height" and (MINIMUM_LINE_HEIGHT is not None) and (quantity < MINIMUM_LINE_HEIGHT):
                        quantity = MINIMUM_LINE_HEIGHT

                    sty[name] = value_str(quantity, unit)

                if (unit == "vh" or unit == "vw") and quantity is not None:

                    if page_align != "none" and name in ["height", "width"]:
                        if name[0] != unit[1]:
                            if not ("height" in sty and "width" in sty):
                                if elem.tag == "img":
                                    img_filename = get_url_filename(urlabspath(elem.get("src"), ref_from=book_part.filename))
                                    img_file = self.oebps_files[img_filename]
                                    orig_prop = name
                                    sty.pop(orig_prop)

                                    if name == "width":
                                        quantity = (quantity * img_file.height) / img_file.width
                                        name = "height"
                                    else:
                                        quantity = (quantity * img_file.width) / img_file.height
                                        name = "width"

                                    if quantity > 99.0 and quantity < 101.0:
                                        quantity = 100.0
                                    else:
                                        log.warning("converted %s:%s for img %dw x %dh to %s:%f%%" % (
                                                orig_prop, val, img_file.width, img_file.height, name, quantity))

                                else:
                                    log.error("viewport-based units with wrong property on non-image: %s:%s" % (name, val))
                            else:
                                log.error("viewport-based units with wrong property: %s:%s" % (name, val))

                        sty[name] = value_str(quantity, "%")
                        quantity, unit = split_value(sty[name])
                    else:
                        log.error("viewport-based units with wrong property or without page-align: %s:%s" % (name, val))

        if "width" in sty:
            known_width = True
        elif sty.get("display", "") == "inline-block":
            known_width = False

        if not known_width:
            for name in (["margin-left", "margin-right"] if sty.get("writing-mode", "horizontal-tb") == "horizontal-tb" else
                         ["margin-bottom", "margin-top"]):
                if name in sty:
                    val = sty.get(name)
                    quantity, unit = split_value(val)
                    if unit == "%" and quantity is not None:
                        sty[name] = value_str(round(float(quantity) * PX_PER_PERCENT), "px")

        if ("outline-width" in sty) and (sty.get("outline-style", "none") == "none"):
            sty.pop("outline-width")

        if elem.tag == "ol":
            if "start" in elem.attrib:
                default_ordered_list_value = int(elem.get("start"))
                if default_ordered_list_value == 1:
                    elem.attrib.pop("start")
            else:
                default_ordered_list_value = 1

        elif elem.tag == "ul":
            if "start" in elem.attrib:
                elem.attrib.pop("start")

            default_ordered_list_value = False

        elif elem.tag == "li":
            if "value" in elem.attrib:
                ordered_list_value = int(elem.get("value"))
                if (default_ordered_list_value is False) or (ordered_list_value == default_ordered_list_value):
                    elem.attrib.pop("value")

            default_ordered_list_value = None

        else:
            default_ordered_list_value = None

        if elem.tag == "div":
            sty.update(self.non_heritable_default_properties, replace=False)

        if sty.get("background-image", "none") != "none" and "-amzn-max-crop-percentage" in sty:
            if sty["-amzn-max-crop-percentage"] == "0 0 0 0":
                sty.pop("-amzn-max-crop-percentage")
                sty["background-size"] = "contain"
            elif sty["-amzn-max-crop-percentage"] == "0 100 100 0":
                sty.pop("-amzn-max-crop-percentage")
                sty["background-size"] = "cover"

        if (elem.tag == "a" and "color" not in sty and "-kfx-link-color" in sty and "-kfx-visited-color" in sty and
                sty["-kfx-link-color"] == sty["-kfx-visited-color"]):
            sty["color"] = sty.pop("-kfx-link-color")
            sty.pop("-kfx-visited-color")

        parent_sty = sty.copy()

        for name in ["font-size", "-kfx-user-margin-bottom-percentage", "-kfx-user-margin-left-percentage",
                     "-kfx-user-margin-right-percentage", "-kfx-user-margin-top-percentage"]:
            parent_sty[name] = orig_sty[name]

        for name, val in list(parent_sty.items()):
            if name not in HERITABLE_PROPERTIES:
                parent_sty.pop(name)
            elif name not in ARBITRARY_VALUE_PROPERTIES and name != "line-height" and split_value(val)[1] == "%":
                parent_sty.pop(name)

        kfx_layout_hints = sty.get("-kfx-layout-hints", "").split()
        self.last_kfx_heading_level = kfx_heading_level = sty.pop("-kfx-heading-level", self.last_kfx_heading_level)

        contains_block_elem = contains_text = contains_image = False
        for child in elem.findall("*"):
            contains_block_elem_, contains_text_, contains_image_ = self.simplify_styles(
                child, book_part, parent_sty, default_ordered_list_value, known_width)
            contains_block_elem = contains_block_elem or contains_block_elem_
            contains_text = contains_text or contains_text_
            contains_image = contains_image or contains_image_

            if (child.tag == "li") and (default_ordered_list_value not in [None, False]):
                default_ordered_list_value += 1

        num_children = len(elem)
        if (REVERSE_INHERITANCE and (not self.book_has_illustrated_layout_conditional_page_template or self.fixed_layout) and
                num_children > 0):

            if elem.text or elem.tail:
                log.error("elem %s with children has text or tail in %s" % (elem.tag, book_part.filename))
            else:
                child_props = collections.defaultdict(dict)
                for child in elem.findall("*"):
                    child_sty = self.get_style(child)
                    for name, val in child_sty.items():
                        if name in REVERSE_HERITABLE_PROPERTIES:
                            if val not in child_props[name]:
                                child_props[name][val] = 1
                            else:
                                child_props[name][val] += 1

                new_heritable_sty = {}
                for name, vals in child_props.items():
                    total = 0
                    most_common = None
                    for val, count in vals.items():
                        total += count
                        if most_common is None or count > vals[most_common]:
                            most_common = val

                    if total < num_children and sty.get(name) is None:
                        pass
                    elif vals[most_common] >= num_children * REVERSE_INHERITANCE_FRACTION:
                        new_heritable_sty[name] = most_common

                if len(new_heritable_sty) > 0:

                    for child in elem.findall("*"):
                        child_sty = self.get_style(child)

                        for name, new_heritable_val in new_heritable_sty.items():
                            val = child_sty.get(name)
                            if val is not None:
                                # 負 text-indent 與同值 padding 是電書協的 h-indent 組合，
                                # 提升到父元素會讓子元素只剩 padding，所以留在子元素上。
                                if (val == new_heritable_val and
                                        not (name == "text-indent" and self.keeps_hanging_indent(child_sty))):
                                    child_sty.pop(name)
                            elif sty[name] != new_heritable_val:
                                child_sty[name] = sty[name]

                        self.set_style(child, child_sty)

                    sty.update(new_heritable_sty, replace=True)

        inherited_properties.update(self.non_heritable_default_properties)

        if elem.tag == "div" and not (self.fixed_layout or self.illustrated_layout or self.has_conditional_content):
            if "heading" in kfx_layout_hints and not contains_block_elem:
                elem.tag = "h" + kfx_heading_level
                inherited_properties.pop("font-size")
                inherited_properties.pop("font-weight")

                if sty.get("writing-mode", "horizontal-tb") == "horizontal-tb":
                    inherited_properties.pop("margin-top")
                    inherited_properties.pop("margin-bottom")
                else:
                    inherited_properties.pop("margin-left")
                    inherited_properties.pop("margin-right")

            elif "figure" in kfx_layout_hints and contains_image and not self.epub2_desired:
                elem.tag = "figure"
                if sty.get("writing-mode", "horizontal-tb") == "horizontal-tb":
                    inherited_properties.update({"margin-top": "1em", "margin-bottom": "1em"}, replace=True)
                else:
                    inherited_properties.update({"margin-left": "1em", "margin-right": "1em"}, replace=True)

            elif contains_text and not contains_block_elem:
                elem.tag = "p"
                if sty.get("writing-mode", "horizontal-tb") == "horizontal-tb":
                    inherited_properties.update({"margin-top": "1em", "margin-bottom": "1em"}, replace=True)
                else:
                    inherited_properties.update({"margin-left": "1em", "margin-right": "1em"}, replace=True)

        if split_value(sty.get("font-size", ""))[1] == "em":
            inherited_properties["font-size"] = "1em"

        if sty.get("background-color", "transparent") == "transparent" and sty.get("background-image", "none") == "none":
            for name in ["background-clip", "background-origin", "background-position", "background-repeat", "background-size"]:
                sty.pop(name, None)

        remove_from = inherited_properties if elem.tag == "a" else sty
        remove_from.pop("-kfx-link-color", None)
        remove_from.pop("-kfx-visited-color", None)

        for name, val in list(sty.items()):
            if val == inherited_properties.get(name, ""):
                sty.pop(name)

        # 與 bw 一致：不輸出 KFX 的 line-height，讓元素繼承 reset 的 body 1.75。
        sty.pop("line-height", None)

        self.set_style(elem, sty)

        contains_block_elem = contains_block_elem or elem.tag in {
                "aside", "caption", "div", "figure", "h1", "h2", "h3", "h4", "h5", "h6", "li", "p", "td",
                IDX_ENTRY}
        contains_text = contains_text or elem.text is not None and len(elem.text) > 0
        contains_image = contains_image or elem.tag == "img" or elem.tag == "svg"

        return (contains_block_elem, contains_text, contains_image)

    # 依「區塊方向」判斷 margin 是否是空行。
    # 直排的區塊方向是水平（右→左），橫排是垂直（上→下）。
    @staticmethod
    def block_margin_side(name, writing_mode):
        mode = writing_mode or "horizontal-tb"
        if mode.startswith("vertical"):
            rtl = mode.endswith("-rl")
            if name == ("margin-right" if rtl else "margin-left"):
                return "before"
            if name == ("margin-left" if rtl else "margin-right"):
                return "after"
            return None

        if name == "margin-top":
            return "before"
        if name == "margin-bottom":
            return "after"
        return None

    @staticmethod
    def is_vertical_writing_mode(writing_mode):
        return writing_mode is not None and writing_mode.startswith("vertical")

    @staticmethod
    def resolve_font_size_rem(value):
        quantity, unit = split_value(value or "")
        if quantity is None:
            return decimal.Decimal(1)
        if unit in {"rem", "em"}:
            return quantity
        if unit == "%":
            return quantity / 100
        if unit == "px":
            return quantity / 16
        return decimal.Decimal(1)

    @staticmethod
    def has_box_border_or_padding(sty):
        has_border = False
        has_padding = False
        for name, value in sty.items():
            if value is None:
                continue
            if name in {"border", "border-top", "border-right", "border-bottom", "border-left"}:
                if value not in {"none", "0", "0px"}:
                    has_border = True
            elif name in {"border-style", "border-top-style", "border-right-style", "border-bottom-style",
                          "border-left-style"}:
                if value not in {"none", "hidden"}:
                    has_border = True
            elif name in BOX_PADDING_PROPERTIES:
                if value not in {"0", "0px", "0em", "0%"}:
                    has_padding = True

        if has_border:
            return True
        if not has_padding:
            return False

        # 只有 padding 沒有框線時，通常只是版面間距（例如口絵頁的字下げ）；
        # 有背景色/背景圖才視為真正的方塊。
        background_color = sty.get("background-color", "transparent")
        return (background_color not in {None, "transparent", "#0", "rgba(0,0,0,0)"} or
                sty.get("background-image", "none") not in {None, "none"})

    @staticmethod
    def is_first_block_after_hr(elem):
        previous = elem.getprevious()
        return previous is not None and previous.tag == "hr"

    def count_blank_lines(self, elem, name, raw_lh, sty, writing_mode):
        is_box = elem.tag != "hr" and self.has_box_border_or_padding(sty)
        font_size_rem = self.resolve_font_size_rem(sty.get("font-size"))
        exact_lines = raw_lh * LINE_HEIGHT_SCALE_FACTOR * font_size_rem / BLANK_LINE_REM
        rounded = int(exact_lines + decimal.Decimal("0.5"))

        if is_box:
            # 橫排頁面的框線方塊 margin 是版面間距，不是空行。
            if not self.is_vertical_writing_mode(writing_mode):
                return 0
            # 直排頁面的框線方塊 margin 會含一份方塊本身的間距；扣掉一行再判斷。
            return max(0, rounded - 1)

        if rounded < 1:
            return 0

        if self.is_vertical_writing_mode(writing_mode):
            return rounded

        # 橫排頁面的 margin-top/bottom 也常用於 colophon 的細部間距；
        # 只接受接近整行的值，以及 hr 與緊接 hr 的區塊。
        if elem.tag == "hr" or self.is_first_block_after_hr(elem):
            return rounded
        return rounded if abs(exact_lines - rounded) <= decimal.Decimal("0.05") * max(1, rounded) else 0

    @staticmethod
    def add_blank_marker(sty, prop_name, side, lines, em_value):
        before = side == "before"
        key = BLANK_BEFORE_PROPERTY if before else BLANK_AFTER_PROPERTY
        orig_key = BLANK_ORIG_BEFORE_PROPERTY if before else BLANK_ORIG_AFTER_PROPERTY
        prop_key = BLANK_PROP_BEFORE_PROPERTY if before else BLANK_PROP_AFTER_PROPERTY
        existing = int(sty[key]) if key in sty else 0
        sty[key] = str(existing + lines)
        sty[orig_key] = value_str(em_value, "em")
        sty[prop_key] = prop_name

    def expand_block_margins(self, book_part):
        for elem in list(book_part.body().iter("*")):
            if "style" not in elem.attrib:
                continue

            sty = self.get_style(elem)
            if BLANK_BEFORE_PROPERTY not in sty and BLANK_AFTER_PROPERTY not in sty:
                continue

            before = sty.pop(BLANK_BEFORE_PROPERTY, None)
            after = sty.pop(BLANK_AFTER_PROPERTY, None)
            orig_before = sty.pop(BLANK_ORIG_BEFORE_PROPERTY, None)
            orig_after = sty.pop(BLANK_ORIG_AFTER_PROPERTY, None)
            prop_before = sty.pop(BLANK_PROP_BEFORE_PROPERTY, None)
            prop_after = sty.pop(BLANK_PROP_AFTER_PROPERTY, None)

            if not self.can_host_blank_paragraphs(elem):
                if orig_before is not None and prop_before is not None:
                    sty[prop_before] = orig_before
                if orig_after is not None and prop_after is not None:
                    sty[prop_after] = orig_after
                self.set_style(elem, sty)
                continue

            self.set_style(elem, sty)
            parent = elem.getparent()
            if parent is None:
                continue

            index = parent.index(elem)
            if before is not None and int(before) > 0:
                for i in range(int(before)):
                    parent.insert(index + i, self.create_blank_paragraph())
                index += int(before)

            if after is not None and int(after) > 0:
                for i in range(int(after)):
                    parent.insert(index + 1 + i, self.create_blank_paragraph())

    @staticmethod
    def can_host_blank_paragraphs(elem):
        parent = elem.getparent()
        if parent is None or parent.tag not in {
                "body", "div", "section", "article", "aside", "blockquote", "figure"}:
            return False

        if elem.tag not in {
                "p", "div", "hr", "figure", "blockquote", "aside", "section", "article", "pre",
                "h1", "h2", "h3", "h4", "h5", "h6"}:
            return False

        ancestor = elem.getparent()
        while ancestor is not None and ancestor.tag != "body":
            if ancestor.tag in {"table", "tr", "td", "th", "p", "span", "a", "li"}:
                return False
            ancestor = ancestor.getparent()

        return True

    @staticmethod
    def create_blank_paragraph():
        p = etree.Element("p")
        etree.SubElement(p, "br")
        return p

    def add_composite_and_equivalent_styles(self, elem, book_part):
        sty = self.get_style(elem)

        for combined_prop, individual_props in COMPOSITE_SIDE_STYLES:
            val = sty.get(individual_props[0], None)
            if val is not None:
                for individual_prop in individual_props[1:]:
                    if sty.get(individual_prop, None) != val:
                        break
                else:
                    sty[combined_prop] = val
                    for individual_prop in individual_props:
                        sty.pop(individual_prop)

        for name, val in list(sty.items()):
            alt_name = ALTERNATE_EQUIVALENT_PROPERTIES.get(name)
            if alt_name is not None:
                if not (EMIT_WEBKIT_EQUIVALENTS or self.generate_epub2 or self.GENERATE_EPUB2_COMPATIBLE):
                    continue
                if name == "text-combine-upright" and val == "all":
                    sty[alt_name] = "horizontal"
                else:
                    sty[alt_name] = val

        display = sty.get("display", "")
        ineffective_properties = []

        if display == "inline" or (elem.tag in INLINE_ELEMENTS and display not in {"block", "inline-block"}):
            ineffective_properties.extend([
                    "list-style-image", "list-style-position", "list-style-type",
                    "column-count", "text-align", "text-align-last", "text-indent"])

            if elem.tag != "img":
                ineffective_properties.extend([
                        "height", "width", "max-height", "max-width",
                        "-amzn-page-align", "-amzn-page-footer", "-amzn-page-header"])

                if sty.get("white-space", "") != "nowrap":
                    ineffective_properties.extend(["overflow"])

        if ineffective_properties:
            ineffective_sty = sty.partition(property_names=ineffective_properties, modify=False)

            if ineffective_sty:
                log.warning("Ineffective property in %s element for kfx-style %s in %s: %s" % (
                        elem.tag, sty.get("-kfx-style-name", "?"), book_part.filename, ineffective_sty.tostring()))

        self.set_style(elem, sty)

        for child in elem.findall("*"):
            self.add_composite_and_equivalent_styles(child, book_part)

    def fix_and_quote_font_family_list(self, value):
        font_family = ",".join(remove_duplicates(
            [self.quote_font_name(name) for name in self.split_and_fix_font_family_list(value)]))
        return font_family or None

    def split_and_fix_font_family_list(self, value):
        return list(filter(None, [self.fix_font_name(name) for name in value.split(",")]))

    def strip_font_name(self, name):
        name = name.strip()

        if name and (name[0] == "'" or name[0] == "\""):
            name = name[1:]

        if name and (name[-1] == "'" or name[-1] == "\""):
            name = name[:-1]

        return name.strip()

    def fix_font_name(self, name, add=False, generic=False):

        name = self.strip_font_name(name)
        if not name:
            return name

        orig_name = name.lower()

        fixed_name = self.fixed_font_names.get(orig_name)
        if fixed_name is not None:
            return fixed_name

        name = name.replace("\\", "")

        name = re.sub(r"-(oblique|italic|bold|regular|roman|medium)$", r" \1", name, flags=re.IGNORECASE)
        name = MISSPELLED_FONT_NAMES.get(name.lower(), name)

        has_prefix = "-" in name and name != "sans-serif"
        if has_prefix:
            name = name.replace("sans-serif", "sans_serif")
            name = name.rpartition("-")[2]
            name = name.replace("sans_serif", "sans-serif")

        name = name.strip()

        if add:
            lower_name = ("?-" if has_prefix else "") + name.lower()
            if lower_name not in self.font_name_replacements:
                self.font_name_replacements[lower_name] = name
            elif self.font_name_replacements[lower_name] != name:
                log.warning("KFX font family name %s changed from %s to %s" % (
                    orig_name, name, self.font_name_replacements[lower_name]))
                name = self.font_name_replacements[lower_name]

            if not generic:
                self.present_font_names.add(name)

        else:
            name = self.font_name_replacements.get(name.lower(), capitalize_font_name(name))

            if name not in GENERIC_FONT_NAMES:
                self.used_font_names.add(name)

        self.fixed_font_names[orig_name] = name

        return name

    def fix_language(self, lang):
        if len(lang) > 2 and lang[2] == "_":
            lang = lang.replace("_", "-")

        prefix, sep, suffix = lang.partition("-")

        if len(suffix) < 4:
            suffix = suffix.upper()
        else:
            suffix = suffix[0].upper() + suffix[1:]

        return prefix + sep + suffix

    def fix_color_value(self, value):
        if isstring(value) and not re.match(r"^-?[0-9]+$", value):
            return value

        color = int(value) & 0xffffffff
        return self.color_str(color, self.int_to_alpha(color >> 24))

    def add_color_opacity(self, value, opacity):
        if opacity is None or opacity == "1":
            return value

        color = self.color_int(value)
        orig_alpha = self.int_to_alpha(color >> 24)

        if orig_alpha not in {0.0, 1.0}:
            log.error("Unexpected combination of alpha (%s) and opacity (%s) for color %s" % (orig_alpha, opacity, value))

        return self.color_str(color, float(opacity))

    def color_str(self, rgb_int, alpha=1.0):
        if alpha == 1.0:
            hex_color = "#%06x" % (rgb_int & 0x00ffffff)
            if hex_color in COLOR_NAME:
                return COLOR_NAME[hex_color]

            return "#000" if hex_color == "#000000" else ("#fff" if hex_color == "#ffffff" else hex_color)

        red = (rgb_int & 0x00ff0000) >> 16
        green = (rgb_int & 0x0000ff00) >> 8
        blue = (rgb_int & 0x000000ff)
        alpha_str = "0" if alpha == 0.0 else "%0.3f" % alpha

        return "rgba(%d,%d,%d,%s)" % (red, green, blue, alpha_str)

    def color_int(self, color):

        if color in COLOR_HEX:
            color = COLOR_HEX[color]

        m = re.match(r"^#([0-9a-f]{3,6})$", color, flags=re.IGNORECASE)
        if m:
            rgb = m.group(1)
            if len(rgb) == 3:
                rgb = rgb[0] + rgb[0] + rgb[1] + rgb[1] + rgb[2] + rgb[2]

            return 0xff000000 + int(rgb, 16)

        m = re.match(r"^rgba\(([0-9]+),([0-9]+),([0-9]+),([0-9.]+)\)$", color, flags=re.IGNORECASE)
        if m:
            red = int(m.group(1))
            green = int(m.group(2))
            blue = int(m.group(3))
            alpha_int = self.alpha_to_int(float(m.group(4)))
            return (alpha_int << 24) + (red << 16) + (green << 8) + blue

        raise Exception("cannot parse color value: %s" % color)

    def int_to_alpha(self, alpha_int):
        if alpha_int < 2:
            return 0.0

        if alpha_int > 253:
            return 1.0

        return max(min(float(alpha_int + 1) / 256.0, 1.0), 0.0)

    def alpha_to_int(self, alpha):
        if alpha < 0.012:
            return 0

        if alpha > 0.996:
            return 255

        return max(min(int(alpha * 256.0 + 0.5) - 1, 255), 0)

    def pixel_value(self, value, adjust=True):
        if value is None:
            return None

        if ion_type(value) is IonStruct:
            unit = value.pop("$306")
            if unit != "$319":
                log.error("%s Expected px value, found %s" % (self.content_context, unit))

            value = value.pop("$307")

        if ion_type(value) in [IonDecimal, IonFloat]:
            value = int(value)

        if ion_type(value) is not IonInt:
            log.error("%s Expected int for px value, found %s" % (self.content_context, type_name(value)))
            return value

        if adjust:
            value = self.adjust_pixel_value(value)

        return value

    def adjust_pixel_value(self, value):
        if self.is_pdf_backed:
            px_value = round(value / 100, 2)

            value = px_value

        return value

    def add_class(self, elem, class_name, before=False):
        classes = elem.get("class", "").split()
        if class_name and class_name not in classes:
            if before:
                classes.insert(0, class_name)
            else:
                classes.append(class_name)

            elem.set("class", " ".join(classes))

    def strip_reset_default_properties(self, elem):
        style = self.get_style(elem)
        modified = False

        if elem.tag in RESET_DEFAULT_MARGIN_TAGS:
            for name in ["margin-left", "margin-right", "margin-top", "margin-bottom"]:
                if style.get(name) == "0":
                    style.pop(name)
                    modified = True

        if style.get("text-align") == "justify" and not self.ancestor_overrides_text_align(elem):
            style.pop("text-align")
            modified = True

        if style.get("font-family") in RESET_DEFAULT_FONT_FAMILIES and not self.ancestor_overrides_font_family(elem):
            style.pop("font-family")
            modified = True

        if style.get("hyphens") == "auto" and not self.ancestor_overrides_property(elem, "hyphens", "auto"):
            style.pop("hyphens")
            modified = True

        if style.get("line-break") == "normal" and not self.ancestor_overrides_property(elem, "line-break", "normal"):
            style.pop("line-break")
            modified = True

        if elem.tag == "body" and style.get("writing-mode") == "vertical-rl":
            style.pop("writing-mode")
            modified = True

        if modified:
            self.set_style(elem, style)

    def ancestor_overrides_text_align(self, elem):
        parent = elem.getparent()
        while parent is not None:
            ta = self.Style(parent.get("style", "")).get("text-align")
            if ta is not None:
                return ta != "justify"
            if parent.tag == "body":
                break
            parent = parent.getparent()

        return False

    def ancestor_overrides_property(self, elem, property_name, reset_value):
        parent = elem.getparent()
        while parent is not None:
            value = self.Style(parent.get("style", "")).get(property_name)
            if value is not None:
                return value != reset_value
            if parent.tag == "body":
                break
            parent = parent.getparent()

        return False

    def ancestor_overrides_font_family(self, elem):
        parent = elem.getparent()
        while parent is not None:
            font_family = self.Style(parent.get("style", "")).get("font-family")
            if font_family is not None:
                return font_family not in RESET_DEFAULT_FONT_FAMILIES
            if parent.tag == "body":
                break
            parent = parent.getparent()

        return False

    def get_style(self, elem, remove=False):
        return self.Style(elem.attrib.pop("style", "") if remove else elem.get("style", ""))

    def set_style(self, elem, new_style):
        if type(new_style) is not Style:
            raise Exception("set_style: type %s" % type_name(new_style))

        style_str = new_style.tostring()
        if style_str:
            elem.set("style", style_str)
        else:
            elem.attrib.pop("style", None)

    def add_style(self, elem, new_style, replace=None):
        if type(new_style) is not Style and type(new_style) is not dict:
            raise Exception("add_style: type %s" % type_name(new_style))

        if new_style:
            orig_style_str = elem.get("style", "")

            if orig_style_str:
                new_style = self.Style(orig_style_str).update(new_style, replace)
            elif type(new_style) is not Style:
                new_style = self.Style(new_style)

            self.set_style(elem, new_style)

    def used_html_classes(self):
        classes = set()
        for book_part in self.book_parts:
            for elem in book_part.html.iter("*"):
                classes.update(elem.get("class", "").split())

        return classes

    def css_selector_is_used(self, selector, used_classes):
        selectors = [s.strip() for s in selector.split(",")]
        for sel in selectors:
            if not sel.startswith("."):
                return True

            class_match = re.match(r"\.([A-Za-z0-9_-]+)(?:$|[\s:\.\[#])", sel)
            if class_match is None or class_match.group(1) in used_classes:
                return True

        return False

    def prune_unused_css_rules(self):
        used_classes = self.used_html_classes()
        self.css_rules = dict((selector, style) for selector, style in self.css_rules.items()
                              if self.css_selector_is_used(selector, used_classes))

        for mq in list(self.media_queries.keys()):
            css_rules = self.media_queries[mq]
            self.media_queries[mq] = dict((selector, style) for selector, style in css_rules.items()
                                          if self.css_selector_is_used(selector, used_classes))
            if not self.media_queries[mq]:
                self.media_queries.pop(mq)

    def generated_class_style(self, elem):
        style = self.Style({})
        for class_name in elem.get("class", "").split():
            class_style = self.css_rules.get(class_selector(class_name))
            # style-kfx.css 以外（電書協 utility class）的樣式不在 css_rules 中，忽略即可。
            if class_style is None:
                continue
            style.update(class_style, replace=True)

        return style

    def normalize_tcy_spans(self):
        for book_part in self.book_parts:
            for span in list(book_part.body().iter("span")):
                class_style = self.generated_class_style(span)
                if class_style is None:
                    continue

                classes = span.get("class", "").split()
                if "tcy" not in classes and class_style.get("text-combine-upright") != "all":
                    continue

                if (class_style.get("text-combine-upright") == "all" and
                        all(name in {"text-combine-upright", "word-break", "-webkit-word-break", "-epub-word-break"}
                            for name in class_style.keys())):
                    # word-break 對 2 字元的縦中横沒有作用；bw 的 tcy 也不帶這個屬性。
                    # 電書協 utility class（不在 css_rules 中）要保留，例如字級。
                    kept = [c for c in classes if class_selector(c) not in self.css_rules]
                    span.set("class", " ".join(kept + ["tcy"]))
                elif "tcy" not in classes:
                    # 保留原本的樣式 class，再補上 tcy；consolidate 會據此避免合併。
                    span.set("class", " ".join(classes + ["tcy"]))

                self.split_tcy_span(span)

    # 縦中横以 2 個符號為一組；KFX 有時會給出 4 個字元一組，需拆回 2+2。
    def split_tcy_span(self, span):
        if len(span) > 0:
            return
        text = span.text or ""
        if len(text) <= 2:
            return

        parent = span.getparent()
        if parent is None:
            return

        index = parent.index(span)
        classes = span.get("class")
        style = span.get("style")
        tail = span.tail

        parent.remove(span)
        last = None
        for offset in range(0, len(text), 2):
            tcy_span = etree.Element("span")
            if classes:
                tcy_span.set("class", classes)
            if style:
                tcy_span.set("style", style)
            tcy_span.text = text[offset:offset + 2]
            parent.insert(index, tcy_span)
            index += 1
            last = tcy_span

        if tail and last is not None:
            last.tail = tail

    def normalize_fit_image_wrappers(self):
        for book_part in self.book_parts:
            for div in list(book_part.body().iter("div")):
                if div.text or len(div) != 1:
                    continue

                img = div[0]
                if img.tag != "img" or img.tail:
                    continue

                div_style = self.generated_class_style(div)
                img_style = self.generated_class_style(img)
                if div_style is None or img_style is None:
                    continue

                if dict(div_style.items()) != {"page-break-inside": "avoid"}:
                    continue

                allowed_img_style = {
                    "height": {"100%", "auto"},
                    "max-height": {"100%"},
                    "max-width": {"100%"},
                    "width": {"100%", "auto"},
                    }
                if any(value not in allowed_img_style.get(name, set()) for name, value in img_style.items()):
                    continue

                div.tag = "p"
                div.attrib.pop("class", None)
                div.attrib.pop("style", None)
                img.set("class", "fit")
                img.attrib.pop("style", None)

    def kfx_style_css_data(self):
        css_lines = ["@charset \"UTF-8\";"]

        if self.extra_css_rules:
            css_lines.extend(["%s {%s}" % (selector, self.extra_css_rules[selector])
                              for selector in sorted(self.extra_css_rules)])

        if self.font_faces:
            css_lines.extend(["@font-face {%s}" % ff.tostring() for ff in sorted(self.font_faces)])

        if self.css_rules:
            css_lines.extend(merge_css_rules(self.css_rules))

        for mq, css_rules in sorted(self.media_queries.items()):
            css_lines.append("@media %s {" % mq)
            css_lines.extend(["    %s" % line for line in merge_css_rules(css_rules)])
            css_lines.append("}")

        return "\n".join(css_lines).encode("utf-8")

    def has_kfx_css_rules(self):
        """style-kfx.css 只有在真的產生規則時才輸出並 @import。"""
        return bool(self.css_rules or self.media_queries or self.font_faces or self.extra_css_rules)

    def uses_publisher_css(self):
        """出版社樣式表只在角川系的書插入（bw 的 3 本角川書都有 style-kadokawa.css / style-karc.css）。"""
        publisher = unicodedata.normalize("NFKC", self.publisher or "").lower()
        return "kadokawa" in publisher or "角川" in publisher

    @staticmethod
    def book_style_css_data(include_publisher_css, has_kfx_css):
        text = read_bundled_style("book-style.css").decode("utf-8")
        text = re.sub(r"^@import\s+\"style-kfx\.css\";\r?\n", "", text, flags=re.M)

        imports = ""
        if include_publisher_css:
            imports += '@import "style-karc.css";\r\n'
            imports += '@import "style-kadokawa.css";\r\n'
        if has_kfx_css:
            imports += '@import "style-kfx.css";\r\n'

        if imports:
            text = re.sub(r'^(@import\s+"style-advance\.css";\r?\n)', r"\1" + imports, text, flags=re.M)

        return text.encode("utf-8")

    def refresh_book_style_css(self):
        if self.KFX_CSS_FILEPATH not in self.oebps_files:
            return

        self.prune_unused_css_rules()
        self.add_oebps_file(self.KFX_CSS_FILEPATH, self.kfx_style_css_data(), "text/css")

    def apply_page_type_classes(self):
        """在產生 CSS 之前補上頁型 class，讓出版社 CSS 能命中，並移除重複的 body 邊距。"""
        publisher_css = self.uses_publisher_css()
        colophon = next((bp for bp in self.book_parts if bp.filename == self.COLOPHON_FILEPATH), None)
        colophon_spacing = (self.find_body_spacing_class(colophon.body()) if colophon is not None else None)

        for book_part in self.book_parts:
            if book_part.is_nav:
                continue

            body = book_part.body()
            if (book_part.is_fxl or self.should_use_fixed_layout_svg(book_part, body) or
                    self.is_image_only_body(body)):
                continue

            if book_part.filename == self.TOC_FILEPATH:
                page_class = "p-toc"
            elif book_part.filename == self.COLOPHON_FILEPATH:
                page_class = "p-colophon"
            else:
                page_class = "p-text"

            if publisher_css and page_class != "p-toc":
                spacing_class = self.find_body_spacing_class(body)
                if spacing_class is not None:
                    if page_class == "p-colophon":
                        self.remove_class(body, spacing_class)
                    elif self.has_center_text_align(body):
                        page_class = "p-titlepage"
                        self.remove_class(body, spacing_class)
                    elif spacing_class == colophon_spacing:
                        page_class = "p-colophon2"
                        self.remove_class(body, spacing_class)

            self.add_class(body, page_class)

    # body 上的樣式若只有 margin-left/right + padding-top/bottom 且左右為百分比，
    # 代表它是 KFX 模擬 .p-colophon .main 等頁面內距的樣式。
    @staticmethod
    def is_body_spacing_style(style):
        if not style:
            return False
        if any(name not in ("margin-left", "margin-right", "padding-top", "padding-bottom")
               for name in style.keys()):
            return False
        return (style.get("margin-left", "").endswith("%") or
                style.get("margin-right", "").endswith("%"))

    def find_body_spacing_class(self, body):
        for class_name in body.get("class", "").split():
            style = self.css_rules.get(class_selector(class_name))
            if style is not None and self.is_body_spacing_style(style):
                return class_name
        return None

    @staticmethod
    def has_center_text_align(body):
        if "align-center" in body.get("class", "").split():
            return True
        return bool(re.search(r"(^|;)\s*text-align\s*:\s*center", body.get("style", "") or ""))

    @staticmethod
    def remove_class(elem, class_name):
        classes = [name for name in elem.get("class", "").split() if name != class_name]
        if classes:
            elem.set("class", " ".join(classes))
        else:
            elem.attrib.pop("class", None)

    def apply_publisher_class_mapping(self):
        """把奥付／扉上還沒對應的列換成 author／label 等出版社語意 class。"""
        rules = publisher_rules()
        if not rules or not self.uses_publisher_css():
            return

        for book_part in self.book_parts:
            if book_part.is_nav or book_part.is_fxl:
                continue
            body = book_part.body()
            page_type = self.page_type_class(body)
            if page_type is None:
                continue

            used = set()
            mapped_parents = []
            # publisher class 在樣式表裡是固定順序，和頁面上的區塊順序一致。
            # 依序掃描區塊、只在順序往後推進時取用；沒有殘餘宣告的區塊也推進游標。
            container = next((e for e in body.iter("*") if "main" in (e.get("class") or "").split()), body)
            blocks = list(container)
            block_fonts = [None] * len(blocks)
            block_rules = [None] * len(blocks)

            cursor = -1
            for index, block in enumerate(blocks):
                generated = self.generated_class_name(block)
                residual = self.css_rules.get(class_selector(generated)) if generated else None
                font_pct = element_font_percent(block)
                block_fonts[index] = font_pct

                chosen = None
                for page, parent, tag, class_name, decls, order in rules:
                    if page != page_type or parent is not None or class_name is None:
                        continue
                    if order <= cursor:
                        continue
                    if class_name in PUBLISHER_STRUCTURAL_CLASSES or class_name in used:
                        continue
                    if not font_compatible(decls, font_pct):
                        continue
                    if residual:
                        ratios = covers_residual(residual, decls, font_pct)
                        if not ratios or max(ratios) - min(ratios) > 1e-3:
                            continue
                    chosen = (class_name, order, decls)
                    break

                if chosen is None:
                    continue
                class_name, cursor, decls = chosen
                if not residual:
                    continue
                block_rules[index] = decls
                self.add_class(block, class_name)
                self.remove_class(block, generated)
                used.add(class_name)
                mapped_parents.append((block, class_name))

            # 出版社樣式把同一組的後續行包在一個 div 裡，讓它們繼承 line-height（多為 1.0）。
            # 我們的結構是平鋪的，所以直接把同樣的行高套在後續同字級的區塊上。
            for index in range(len(blocks)):
                decls = block_rules[index]
                if decls is None:
                    continue
                line_height = line_height_class(decls.get("line-height"))
                if line_height is None:
                    continue
                for other in range(index + 1, len(blocks)):
                    if block_rules[other] is not None:
                        break
                    if block_fonts[other] != block_fonts[index]:
                        break
                    self.add_class(blocks[other], line_height)

            for parent_elem, class_name in mapped_parents:
                for child in parent_elem.iterdescendants():
                    generated = self.generated_class_name(child)
                    if generated is None:
                        continue
                    residual = self.css_rules.get(class_selector(generated))
                    if residual is None:
                        continue
                    for page, parent, tag, child_class, decls, _order in rules:
                        if page != page_type or parent != class_name:
                            continue
                        if tag is not None and tag != str(child.tag).rsplit("}", 1)[-1]:
                            continue
                        ratios = covers_residual(residual, decls, element_font_percent(child))
                        if not ratios or max(ratios) - min(ratios) > 1e-3:
                            continue
                        if child_class is not None and child_class not in used:
                            self.add_class(child, child_class)
                            used.add(child_class)
                        self.remove_class(child, generated)
                        break

    @staticmethod
    def page_type_class(body):
        for name in (body.get("class") or "").split():
            if name in ("p-colophon", "p-colophon2", "p-titlepage"):
                return name
        return None

    def generated_class_name(self, elem):
        for name in (elem.get("class") or "").split():
            if class_selector(name) in self.css_rules:
                return name
        return None

    def create_css_files(self):
        self.prune_unused_css_rules()
        linked_css_files = set()

        for book_part in self.book_parts:
            for link in book_part.head().findall("link"):
                if link.get("rel") == "stylesheet" and "href" in link.attrib:
                    linked_css_files.add(get_url_filename(urlabspath(link.get("href"), ref_from=book_part.filename)))

        for css_file in sorted(linked_css_files):
            if css_file == self.FIXED_LAYOUT_CSS_FILEPATH:
                self.manifest_resource(
                    self.FIXED_LAYOUT_CSS_FILEPATH, data=read_bundled_style("fixed-layout-jp.css"), mimetype="text/css")

            elif css_file == self.STYLES_CSS_FILEPATH:
                self.manifest_resource(
                    self.RESET_CSS_FILEPATH, data=read_bundled_style("style-reset.css"), mimetype="text/css")
                self.manifest_resource(
                    self.STANDARD_CSS_FILEPATH, data=read_bundled_style("style-standard.css"), mimetype="text/css")
                self.manifest_resource(
                    self.ADVANCE_CSS_FILEPATH, data=read_bundled_style("style-advance.css"), mimetype="text/css")
                self.manifest_resource(
                    self.CHECK_CSS_FILEPATH, data=read_bundled_style("style-check.css"), mimetype="text/css")

                include_publisher_css = self.uses_publisher_css()
                has_kfx_css = self.has_kfx_css_rules()
                self.manifest_resource(
                    self.STYLES_CSS_FILEPATH,
                    data=self.book_style_css_data(include_publisher_css, has_kfx_css), mimetype="text/css")

                if include_publisher_css:
                    self.manifest_resource(
                        self.KARC_CSS_FILEPATH, data=read_bundled_style("style-karc.css"), mimetype="text/css")
                    self.manifest_resource(
                        self.KADOKAWA_CSS_FILEPATH, data=read_bundled_style("style-kadokawa.css"), mimetype="text/css")

                if has_kfx_css:
                    self.manifest_resource(
                        self.KFX_CSS_FILEPATH, data=self.kfx_style_css_data(), mimetype="text/css")

    def ensure_linked_css_files(self):
        # 整頁圖片頁的 fixed-layout-jp.css 是在 save_book_parts 期間才掛上，
        # create_css_files 已於建構子執行完畢，這裡補登記到 manifest。
        for book_part in self.book_parts:
            for link in book_part.head().findall("link"):
                if link.get("rel") != "stylesheet" or "href" not in link.attrib:
                    continue

                css_file = get_url_filename(urlabspath(link.get("href"), ref_from=book_part.filename))
                if (css_file == self.FIXED_LAYOUT_CSS_FILEPATH and
                        css_file not in self.manifest_files):
                    self.manifest_resource(
                        self.FIXED_LAYOUT_CSS_FILEPATH, data=read_bundled_style("fixed-layout-jp.css"),
                        mimetype="text/css")

    def quote_font_name(self, value):
        for ident in value.split(" "):
            if len(ident) == 0:
                break

            if re.match("^(-?[0-9]|--)", ident) or not re.match("^[-_a-zA-Z0-9]*$", ident):
                break
        else:
            return value

        return self.quote_css_str(value)

    def css_url(self, val):
        return "url(%s)" % (self.quote_css_str(val) if re.search("[ ()'\"\\\\]", val) else val)

    def quote_css_str(self, val):

        if not re.search("['\\\\]", val):
            return "'%s'" % val

        return "\"%s\"" % re.sub("([\"\\\\])", "\\\\\\1", val)


@functools.total_ordering
class Style(object):
    def __init__(self, src, sstr=None):
        self.style_str = self.properties = None

        if type(src) is etree.Element:
            src = src.get("style", "")

        if isinstance(src, bytes):
            src = src.decode("ascii")

        if isinstance(src, str):
            src = self.get_properties(src)

        if isinstance(src, dict):
            self.properties = dict(src)
            self.style_str = sstr
        else:
            raise Exception("Cannot create style from %s" % type_name(src))

    def get_properties(self, style_str):
        if style_str == "None":
            raise Exception("Unexpected 'None' encountered in style")

        if style_str not in style_cache:
            style_cache[style_str] = properties = {}

            for property in re.split(r"((?:[^;\(]|\([^\)]*\))+)", style_str)[1::2]:
                property = property.strip()
                if property:
                    name, sep, value = property.partition(":")
                    name = name.strip()
                    value = value.strip()

                    if sep != ":":
                        log.error("Malformed property %s in style: %s" % (name, style_str))
                    else:
                        if name in properties and properties[name] != value:
                            log.error("Conflicting property %s values in style: %s" % (name, style_str))

                        properties[name] = value

        return dict(style_cache[style_str])

    def tostring(self):
        if self.style_str is None:
            self.style_str = "; ".join(["%s: %s" % s for s in sorted(self.properties.items())])

        return self.style_str

    def keys(self):
        return self.properties.keys()

    def items(self):
        return self.properties.items()

    def get(self, key, default=None):
        return self.properties.get(key, default)

    def __len__(self):
        return len(self.properties)

    def __repr__(self):
        return self.tostring()

    def __str__(self):
        return self.tostring()

    def __eq__(self, other):
        if not isinstance(other, Style):
            raise Exception("Style __eq__: comparing with %s" % type_name(other))

        if self.style_str is not None and other.style_str is not None:
            return self.style_str == other.style_str

        return self.properties == other.properties

    def __lt__(self, other):
        if not isinstance(other, Style):
            raise Exception("Style __lt__: comparing with %s" % type_name(other))

        return self.tostring() < other.tostring()

    def __hash__(self):
        return hash(self.tostring())

    def __getitem__(self, key):
        return self.properties[key]

    def __contains__(self, key):
        return key in self.properties

    def __setitem__(self, key, value):
        self.properties[key] = value
        self.style_str = None

    def pop(self, key, default=None):
        value = self.properties.pop(key, default)
        self.style_str = None
        return value

    def clear(self):
        self.properties = {}
        self.style_str = None
        return self

    def copy(self):
        return Style(self.properties, self.style_str)

    def update(self, other, replace=None):
        if type(other) is Style:
            other = other.properties

        for name, value in other.items():
            if (name in CONFLICTING_PROPERTIES) and not CONFLICTING_PROPERTIES[name].isdisjoint(set(self.properties.keys())):
                log.error("Setting conflicting property: %s with %s" % (name, list_symbols(self.properties.keys())))

            if name in self.properties and self.properties[name] != value:
                if replace is Exception:
                    raise Exception("Setting conflicting property value: %s = %s >> %s" % (name, self.properties[name], value))

                if replace is None:
                    log.error("Setting conflicting property value: %s = %s >> %s" % (name, self.properties[name], value))
                elif not replace:
                    continue

            self.properties[name] = value
            self.style_str = None

        return self

    def partition(self, property_names=None, name_prefix=None, remove_prefix=False, add_prefix=False, keep=False, keep_all=False, modify=True):

        match_props = {}
        other_props = {}

        for name, value in self.properties.items():
            if name_prefix is not None:
                if add_prefix:
                    name = "%s-%s" % (name_prefix, name)
                    match = True
                else:
                    match = name.startswith(name_prefix)

                if match and remove_prefix:
                    name = name[len(name_prefix):]
            else:
                match = (name in property_names)

            if match:
                match_props[name] = value
            else:
                other_props[name] = value

        if keep and modify:
            self.properties = match_props
            self.style_str = None

        if keep or keep_all:
            return Style(other_props)

        if modify:
            self.properties = other_props
            self.style_str = None

        return Style(match_props)

    def remove_default_properties(self, default_style):
        defaults = default_style.properties

        for name, value in self.properties.items():
            if value == defaults.get(name, ""):
                self.properties.pop(name)
                self.style_str = None

        return self


def zero_quantity(val):

    if re.match(r"^#[0-9a-f]+$", val) or re.match(r"^rgba\([0-9]+,[0-9]+,[0-9]+,[0-9.]+\)$", val) or val in COLOR_NAMES:
        return "#0"

    num_match = re.match(r"^([+-]?[0-9]+\.?[0-9]*)(|em|ex|ch|rem|vw|vh|vmin|vmax|%|cm|mm|in|px|pt|pc)$", val)
    if num_match:
        return "0"

    return val


def capitalize_font_name(name):
    return " ".join([(word.capitalize() if len(word) > 2 else word.upper()) for word in name.split(" ")])


def class_selector(class_name):
    return "." + class_name


def merge_css_rules(css_rules):
    selectors_by_body = collections.OrderedDict()

    for selector in sorted(css_rules.keys(), key=natural_sort_key):
        body = str(css_rules[selector])
        selectors_by_body.setdefault(body, []).append(selector)

    return ["%s {%s}" % (", ".join(selectors), body) for body, selectors in selectors_by_body.items()]
