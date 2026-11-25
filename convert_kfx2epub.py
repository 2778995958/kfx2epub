import sys
from pathlib import Path


def app_dir():
    if getattr(sys, "frozen", False):
        return Path(sys.executable).parent.resolve()
    return Path(__file__).parent.resolve()


APP_DIR = app_dir()
if str(APP_DIR) not in sys.path:
    sys.path.insert(0, str(APP_DIR))

for _stream_name in ("stdout", "stderr"):
    _stream = getattr(sys, _stream_name)
    if hasattr(_stream, "reconfigure"):
        try:
            _stream.reconfigure(encoding="utf-8", errors="replace", newline="\n")
        except (ValueError, OSError):
            _stream.reconfigure(encoding="utf-8", errors="replace")

import argparse
import hashlib
import json
import logging
import os
import platform
import signal
import struct
import time
import uuid
import zipfile

from kfxlib import YJ_Book
from kfxlib.utilities import KFXDRMError
from kfxlib.version import __version__ as KFXLIB_VERSION
from kfxlib.yj_to_epub import KFX_EPUB


API_VERSION = 1
DRM_SIGNATURE = b"\xeaDRMION\xee"
ZIP_SIGNATURE = b"PK\x03\x04"
KFX_SIGNATURE = b"CONT"

RESERVED_COMMANDS = {"probe", "convert", "capabilities", "version"}

ERROR_EXIT = {
    "INPUT_NOT_FOUND": 10,
    "INPUT_NOT_KFX": 10,
    "INPUT_EMPTY": 10,
    "UNSUPPORTED_FORMAT": 11,
    "UNSUPPORTED_VERSION": 11,
    "DRM_PROTECTED": 12,
    "CORRUPT_INPUT": 13,
    "OUTPUT_EXISTS": 20,
    "IO_ERROR": 20,
    "CANCELLED": 40,
    "INTERNAL_ERROR": 1,
    "USAGE_ERROR": 2,
}

FLAG_OPTIONS = {
    "--json", "--quiet", "--verbose", "--no-pause", "--help", "-h",
    "--force", "--force-cover", "--reproducible", "--api-version",
}
VALUE_OPTIONS = {"--log-file", "--out", "--format", "--progress", "--workers", "-w"}


class CliError(Exception):
    def __init__(self, code, message, detail=None, emit_json=False):
        super().__init__(message)
        self.code = code
        self.exit = ERROR_EXIT.get(code, 1)
        self.detail = detail
        self.emit_json = emit_json


class TeeWriter(object):
    def __init__(self, first, second):
        self.first = first
        self.second = second

    def write(self, text):
        self.first.write(text)
        self.second.write(text)
        return len(text)

    def flush(self):
        self.first.flush()
        self.second.flush()

    def __getattr__(self, name):
        return getattr(self.first, name)


def emit_json(value):
    sys.stdout.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def emit_text(text):
    sys.stdout.write(text + "\n")
    sys.stdout.flush()


def sanitize_filename(name):
    """
    Convert characters that are not allowed in filesystem filenames to safe full-width variants.
    """
    table = str.maketrans({
        "\\": "＼",
        "/": "／",
        ":": "：",
        "*": "＊",
        "?": "？",
        "\"": "＂",
        "<": "＜",
        ">": "＞",
        "|": "｜",
    })
    return name.translate(table)


def unique_output_file(output_path, clean_filename):
    output_file = output_path / clean_filename
    if not output_file.exists():
        return output_file

    index = 1
    while True:
        duplicate_folder = output_path / f"dupname{index}"
        output_file = duplicate_folder / clean_filename
        if not output_file.exists():
            duplicate_folder.mkdir(parents=True, exist_ok=True)
            return output_file
        index += 1


def split_option(arg):
    if not arg.startswith("-") or len(arg) <= 1:
        return None, False
    if "=" in arg:
        name, _ = arg.split("=", 1)
        return name, True
    return arg, False


def first_meaningful_token(args):
    index = 0
    while index < len(args):
        arg = args[index]
        if arg == "--":
            return None
        name, has_inline = split_option(arg)
        if name is not None:
            if name in VALUE_OPTIONS and not has_inline:
                index += 1
            index += 1
            continue
        return arg
    return None


def has_api_version(args):
    for arg in args:
        if arg == "--":
            return False
        if arg == "--api-version":
            return True
    return False


def parse_subcommand(command, args):
    globals_ = {"json": False, "quiet": False, "verbose": False, "no_pause": False, "log_file": None}
    values = {}
    flags = set()
    positionals = []
    help_requested = False
    api_version = False
    skipped_command = False
    stop_options = False

    index = 0
    while index < len(args):
        arg = args[index]
        if not stop_options and arg == "--":
            stop_options = True
            index += 1
            continue

        if not stop_options and arg.startswith("-") and len(arg) > 1:
            name, has_inline = split_option(arg)
            if name is None:
                raise CliError("USAGE_ERROR", f"未知的選項：{arg}")

            if name in VALUE_OPTIONS:
                if has_inline:
                    value = arg.split("=", 1)[1]
                else:
                    if index + 1 >= len(args):
                        raise CliError("USAGE_ERROR", f"選項 {name} 缺少值。")
                    index += 1
                    value = args[index]
                if name == "--log-file":
                    globals_["log_file"] = value
                else:
                    values[name] = value
                index += 1
                continue

            if name not in FLAG_OPTIONS:
                raise CliError("USAGE_ERROR", f"未知的選項：{arg}")
            if has_inline:
                raise CliError("USAGE_ERROR", f"選項 {name} 不接受值。")

            if name == "--json":
                globals_["json"] = True
            elif name == "--quiet":
                globals_["quiet"] = True
            elif name == "--verbose":
                globals_["verbose"] = True
            elif name == "--no-pause":
                globals_["no_pause"] = True
            elif name in ("--help", "-h"):
                help_requested = True
            elif name == "--api-version":
                api_version = True
            else:
                flags.add(name)
            index += 1
            continue

        if not skipped_command and arg == command:
            skipped_command = True
            index += 1
            continue

        positionals.append(arg)
        index += 1

    return {
        "globals": globals_,
        "values": values,
        "flags": flags,
        "positionals": positionals,
        "help": help_requested,
        "api_version": api_version,
    }


def configure_output(globals_):
    quiet = globals_.get("quiet", False)
    verbose = globals_.get("verbose", False)
    log_file = globals_.get("log_file")

    handler_stream = sys.stderr
    if log_file:
        log_path = os.path.abspath(log_file)
        parent = os.path.dirname(log_path)
        if parent:
            os.makedirs(parent, exist_ok=True)
        log_handle = open(log_path, "w", encoding="utf-8", newline="\n")
        handler_stream = TeeWriter(sys.stderr, log_handle)
        sys.stderr = handler_stream

    if quiet:
        sys.stderr = open(os.devnull, "w", encoding="utf-8")
        level = logging.CRITICAL + 1
    else:
        level = logging.INFO if verbose else logging.WARNING

    root = logging.getLogger()
    for existing in root.handlers[:]:
        root.removeHandler(existing)
    handler = logging.StreamHandler(sys.stderr if not quiet else open(os.devnull, "w", encoding="utf-8"))
    handler.setFormatter(logging.Formatter("%(message)s"))
    root.addHandler(handler)
    root.setLevel(level)


def human(message):
    print(message, file=sys.stderr)


def error_json(command, error, verbose):
    return {
        "api": API_VERSION,
        "ok": False,
        "command": command,
        "code": error.code,
        "exit": error.exit,
        "message": str(error),
        "detail": error.detail if verbose else None,
    }


def normalize_input(path):
    return os.path.abspath(path)


def detect_format(path):
    if os.path.isdir(path):
        return None
    suffix = os.path.splitext(path)[1].lower()
    if suffix in (".kfx-zip", ".zip", ".azk"):
        return "kfx-zip"
    if suffix in (".kfx", ".azw8", ".ion"):
        return "kfx"
    if suffix == ".kpf":
        return "kpf"

    try:
        with open(path, "rb") as stream:
            header = stream.read(4)
    except OSError:
        return None
    if header == ZIP_SIGNATURE:
        return "kfx-zip"
    if header == KFX_SIGNATURE:
        return "kfx"
    return None


def validate_input_file(path):
    if os.path.isdir(path):
        raise CliError("INPUT_NOT_KFX", "輸入是資料夾，probe／convert 需要單一 KFX／KFX-ZIP 檔案。")
    if not os.path.exists(path):
        raise CliError("INPUT_NOT_FOUND", f"找不到輸入檔案：{path}")
    if os.path.getsize(path) == 0:
        raise CliError("INPUT_EMPTY", f"輸入檔案是空的：{path}")


def require_supported_format(path):
    fmt = detect_format(path)
    if fmt is None:
        raise CliError("INPUT_NOT_KFX", "無法辨識輸入格式；只支援 kfx 與 kfx-zip。")
    if fmt == "kpf":
        raise CliError("UNSUPPORTED_FORMAT", "此版本不支援 KPF 輸入。")
    return fmt


def detect_drm(path):
    try:
        with open(path, "rb") as stream:
            header = stream.read(len(DRM_SIGNATURE))
            if header == DRM_SIGNATURE:
                return True
            zip_header = None
            stream.seek(0)
            zip_header = stream.read(4)
    except OSError:
        return False

    if zip_header == ZIP_SIGNATURE:
        try:
            with zipfile.ZipFile(path, "r") as archive:
                for info in archive.infolist():
                    if info.is_dir():
                        continue
                    with archive.open(info, "r") as entry:
                        if entry.read(len(DRM_SIGNATURE)) == DRM_SIGNATURE:
                            return True
        except (OSError, zipfile.BadZipFile):
            return False
    return False


def classify_exception(ex, recognized):
    if isinstance(ex, CliError):
        return ex
    if isinstance(ex, KFXDRMError):
        return CliError("DRM_PROTECTED", "此書受 DRM 保護，無法轉換。", repr(ex))
    if isinstance(ex, FileNotFoundError):
        return CliError("INPUT_NOT_FOUND", "找不到輸入檔案。", repr(ex))
    if isinstance(ex, IsADirectoryError):
        return CliError("INPUT_NOT_KFX", "輸入是資料夾，需要單一 KFX／KFX-ZIP 檔案。", repr(ex))
    if isinstance(ex, MemoryError):
        return CliError("IO_ERROR", "記憶體不足。", repr(ex))
    message = str(ex)
    if recognized and isinstance(ex, (zipfile.BadZipFile, EOFError, IndexError, KeyError, ValueError, struct.error)):
        return CliError("CORRUPT_INPUT", "KFX 檔案不完整或已損壞。", repr(ex))
    if isinstance(ex, OSError) and recognized:
        return CliError("IO_ERROR", "檔案存取失敗。", repr(ex))

    if "DRM" in message:
        return CliError("DRM_PROTECTED", "此書受 DRM 保護，無法轉換。", repr(ex))
    if any(marker in message for marker in (
            "Unknown main file type", "No KFX containers", "not in KFX format",
            "Unable to determine KFX container type", "MOBI")):
        return CliError("INPUT_NOT_KFX", "輸入不是可辨識的 KFX 檔案。", repr(ex))
    if recognized and any(marker in message for marker in (
            "too short", "signature is incorrect", "is insufficient", "not IonStruct",
            "entity signature", "Container is too short", "Container header", "entity header")):
        return CliError("CORRUPT_INPUT", "KFX 檔案不完整或已損壞。", repr(ex))
    return CliError("INTERNAL_ERROR", "未預期的內部錯誤。", repr(ex))


def probe_metadata(path):
    book = YJ_Book(str(path))
    book.decode_book()
    probe = KFX_EPUB(book, metadata_only=True)

    fixed_layout = bool(getattr(probe, "fixed_layout", False))
    page_count = None
    if fixed_layout:
        total = 0
        for order in getattr(probe, "reading_orders", []) or []:
            try:
                total += len(order.get("$170", []))
            except AttributeError:
                pass
        page_count = total or None

    kfx_version = None
    for container in getattr(book, "yj_containers", []) or []:
        container_info = getattr(container, "container_info", None)
        value = getattr(container_info, "value", None)
        if value is not None:
            version = value.get("version")
            if version is not None:
                kfx_version = str(version)
                break

    return {
        "kfx_version": kfx_version,
        "title": probe.title or None,
        "authors": list(probe.authors or []),
        "language": probe.language or None,
        "page_count": page_count,
        "fixed_layout": fixed_layout,
        "pdf_backed": bool(getattr(probe, "is_pdf_backed", False)),
    }


def run_probe(parsed):
    if len(parsed["positionals"]) != 1:
        raise CliError("USAGE_ERROR", "命令 probe 需要剛好一個輸入路徑。")

    path = normalize_input(parsed["positionals"][0])
    validate_input_file(path)
    fmt = require_supported_format(path)

    started = time.monotonic()
    drm = detect_drm(path)
    metadata = {
        "kfx_version": None, "title": None, "authors": [], "language": None,
        "page_count": None, "fixed_layout": False, "pdf_backed": False,
    }

    try:
        metadata = probe_metadata(path)
        drm = drm or False
    except Exception as ex:
        classified = classify_exception(ex, recognized=True)
        if classified.code == "DRM_PROTECTED":
            drm = True
        elif drm:
            pass
        else:
            raise classified

    result = {
        "api": API_VERSION,
        "ok": True,
        "command": "probe",
        "input": path,
        "format": fmt,
        "kfx_version": metadata["kfx_version"],
        "title": metadata["title"],
        "authors": metadata["authors"],
        "language": metadata["language"],
        "page_count": metadata["page_count"],
        "drm": bool(drm),
        "fixed_layout": metadata["fixed_layout"],
        "pdf_backed": metadata["pdf_backed"],
        "elapsed_ms": int((time.monotonic() - started) * 1000),
    }
    emit_json(result)
    return 0


def write_atomic(final_path, data, force):
    directory = os.path.dirname(os.path.abspath(final_path)) or "."
    os.makedirs(directory, exist_ok=True)

    if not force and os.path.exists(final_path):
        raise CliError("OUTPUT_EXISTS", f"輸出路徑已存在：{final_path}")

    token = uuid.uuid4().hex[:8]
    temporary = os.path.join(directory, f".{os.path.basename(final_path)}.kfx2epub-{token}.part")
    try:
        with open(temporary, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())

        if force:
            os.replace(temporary, final_path)
        elif os.name == "nt":
            try:
                os.rename(temporary, final_path)
            except FileExistsError:
                raise CliError("OUTPUT_EXISTS", f"輸出路徑已存在：{final_path}")
        else:
            try:
                os.link(temporary, final_path)
            except FileExistsError:
                raise CliError("OUTPUT_EXISTS", f"輸出路徑已存在：{final_path}")
            except OSError:
                if os.path.exists(final_path):
                    raise CliError("OUTPUT_EXISTS", f"輸出路徑已存在：{final_path}")
                os.rename(temporary, final_path)
    finally:
        if os.path.exists(temporary):
            try:
                os.unlink(temporary)
            except OSError:
                pass


def run_convert(parsed, command):
    if len(parsed["positionals"]) != 1:
        raise CliError("USAGE_ERROR", "命令 convert 需要剛好一個輸入路徑。")

    out_arg = parsed["values"].get("--out")
    if not out_arg:
        raise CliError("USAGE_ERROR", "convert 必須指定 --out <path|->。")

    fmt = parsed["values"].get("--format", "epub3")
    if fmt not in ("epub3", "epub2"):
        raise CliError("USAGE_ERROR", "--format 只接受 epub3 或 epub2。")

    if parsed["values"].get("--progress", "none") != "none":
        raise CliError("USAGE_ERROR", "此版本不支援 --progress=ndjson。", emit_json=True)

    workers = parsed["values"].get("--workers")
    if workers is not None:
        try:
            if int(workers) <= 0:
                raise ValueError
        except ValueError:
            raise CliError("USAGE_ERROR", "--workers 必須是正整數。")

    force = "--force" in parsed["flags"]
    force_cover = "--force-cover" in parsed["flags"]
    reproducible = "--reproducible" in parsed["flags"]
    to_stdout = out_arg == "-"

    path = normalize_input(parsed["positionals"][0])
    validate_input_file(path)
    require_supported_format(path)

    if reproducible:
        os.environ["KFX2EPUB_REPRODUCIBLE"] = "1"

    started = time.monotonic()
    book = None
    try:
        book = YJ_Book(str(path))
        book.decode_book(retain_yj_locals=True)
        converter = KFX_EPUB(book, epub2_desired=(fmt == "epub2"), force_cover=force_cover,
                             reproducible=reproducible)

        summary = {
            "title": converter.title or None,
            "authors": list(converter.authors or []),
            "language": converter.language or None,
            "page_count": None,
            "drm": False,
            "pdf_backed": bool(getattr(converter, "is_pdf_backed", False)),
        }
        if getattr(converter, "fixed_layout", False):
            total = 0
            for order in getattr(converter, "reading_orders", []) or []:
                try:
                    total += len(order.get("$170", []))
                except AttributeError:
                    pass
            summary["page_count"] = total or None

        data = converter.decompile_to_epub()
        if book is not None:
            book.final_actions()
            book = None

        if not data:
            raise CliError("CORRUPT_INPUT", "轉換失敗：產生的 EPUB 資料為空。")

        if to_stdout:
            sys.stdout.buffer.write(data)
            sys.stdout.buffer.flush()
            output = "-"
        else:
            if out_arg.endswith(os.sep) or out_arg.endswith("/") or os.path.isdir(out_arg):
                output_dir = Path(out_arg).resolve()
                output_dir.mkdir(parents=True, exist_ok=True)
                author = summary["authors"][0] if summary["authors"] else "Unknown"
                title = summary["title"] or "Unknown"
                output = str(unique_output_file(output_dir, sanitize_filename(f"[{author}] {title}") + ".epub"))
            else:
                output = os.path.abspath(out_arg)
            write_atomic(output, data, force)

        elapsed_ms = int((time.monotonic() - started) * 1000)
        human(f"✅ {os.path.basename(path)} → {output}（{elapsed_ms / 1000:.1f} 秒，{len(data)} bytes）")

        result = {
            "api": API_VERSION,
            "ok": True,
            "command": "convert",
            "input": path,
            "output": output,
            "bytes": len(data),
            "sha256": hashlib.sha256(data).hexdigest(),
            "elapsed_ms": elapsed_ms,
            "book": summary,
        }
        if parsed["globals"]["json"] and not to_stdout:
            emit_json(result)
        return 0
    except KeyboardInterrupt:
        raise CliError("CANCELLED", "轉換已取消。")
    finally:
        if book is not None:
            try:
                book.final_actions()
            except Exception:
                pass


def format_version():
    machine = platform.machine().lower()
    arch = {"amd64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(machine, machine)
    system = platform.system().lower()
    os_name = {"windows": "win", "darwin": "osx"}.get(system, system or "unknown")
    return {
        "api": API_VERSION,
        "tool": "kfx2epub",
        "version": KFXLIB_VERSION,
        "commit": None,
        "runtime": f"Python {platform.python_version()}",
        "platform": f"{os_name}-{arch}",
    }


def capabilities():
    return {
        "api": API_VERSION,
        "input_formats": ["kfx", "kfx-zip"],
        "output_formats": ["epub3", "epub2"],
        "probe": True,
        "progress_ndjson": False,
        "reproducible": True,
        "pdf_backed": "rasterize",
        "dictionary": False,
        "drm": "reject",
        "max_input_bytes": 2147483591,
        "platform": format_version()["platform"],
    }


def print_subcommand_usage(command):
    usage = {
        "version": ["kfx2epub version [--json]"],
        "capabilities": ["kfx2epub capabilities"],
        "probe": ["kfx2epub probe <input> [--json]"],
        "convert": [
            "kfx2epub convert <input> --out <path|-> [--force] [--format epub3|epub2]",
            "                           [--force-cover] [--reproducible] [--json]",
        ],
    }.get(command, ["kfx2epub <version|capabilities|probe|convert> ..."])
    human("使用方式：")
    for line in usage:
        human("  " + line)
    human("")
    human("全域選項：--json --quiet --verbose --no-pause --log-file <path>")
    human("舊版批次：kfx2epub [--] [-w N] [輸入] [輸出]")


def run_subcommand(command, args):
    parsed = None
    try:
        parsed = parse_subcommand(command, args)
        configure_output(parsed["globals"])

        if parsed["help"]:
            print_subcommand_usage(command)
            return 0

        if command == "version":
            if parsed["positionals"]:
                raise CliError("USAGE_ERROR", f"命令 version 不接受位置參數：{parsed['positionals'][0]}")
            info = format_version()
            if parsed["globals"]["json"]:
                emit_json(info)
            else:
                commit = f" ({info['commit']})" if info["commit"] else ""
                emit_text(f"kfx2epub {info['version']}{commit}  api {info['api']}  {info['runtime']}  {info['platform']}")
            return 0

        if command == "capabilities":
            if parsed["positionals"]:
                raise CliError("USAGE_ERROR", f"命令 capabilities 不接受位置參數：{parsed['positionals'][0]}")
            emit_json(capabilities())
            return 0

        if command == "probe":
            return run_probe(parsed)

        if command == "convert":
            return run_convert(parsed, command)

        raise CliError("USAGE_ERROR", f"未知的子命令：{command}")
    except CliError as error:
        if parsed is None or (error.code == "USAGE_ERROR" and not error.emit_json):
            print_subcommand_usage(command)
            human(f"❌ {error}")
            return 2
        verbose = parsed["globals"]["verbose"]
        to_stdout = command == "probe" or (
            command == "convert" and parsed["globals"]["json"] and parsed["values"].get("--out") != "-")
        if to_stdout:
            emit_json(error_json(command, error, verbose))
        else:
            human(f"❌ {error}")
        return error.exit
    except Exception as ex:
        recognized = True
        error = classify_exception(ex, recognized)
        if parsed is None:
            human(f"❌ {error}")
            return error.exit
        verbose = parsed["globals"]["verbose"]
        to_stdout = command == "probe" or (
            command == "convert" and parsed["globals"]["json"] and parsed["values"].get("--out") != "-")
        if to_stdout:
            emit_json(error_json(command, error, verbose))
        else:
            human(f"❌ {error}")
        return error.exit


def batch_convert_kfx_to_epub(input_dir, output_dir, recursive=True):
    """
    Convert a single KFX/KFX-ZIP file, or all KFX/KFX-ZIP files in a folder, to EPUB.
    Output EPUB files are named in the safe full-width '[Author] Book Title.epub' format.
    """
    input_path = Path(input_dir).resolve()
    output_path = Path(output_dir).resolve()

    print(f"📂 Input path: {input_path}")
    print(f"📂 Output folder: {output_path}")

    if not input_path.exists():
        print(f"❌ Error: input path not found: '{input_path}'")
        return 1

    output_path.mkdir(parents=True, exist_ok=True)

    if input_path.is_file():
        if input_path.suffix.lower() == ".kfx" or input_path.name.lower().endswith(".kfx-zip"):
            kfx_files = [input_path]
        else:
            print("💡 Hint: the input is a file, but it is not a .kfx or .kfx-zip file.")
            return 1
    else:
        if recursive:
            kfx_files = list(input_path.rglob("*.kfx-zip")) + list(input_path.rglob("*.kfx"))
        else:
            kfx_files = list(input_path.glob("*.kfx-zip")) + list(input_path.glob("*.kfx"))

    if not kfx_files:
        if recursive:
            print("💡 Hint: no .kfx-zip or .kfx files were found (including subfolders).")
        else:
            print("💡 Hint: no .kfx-zip or .kfx files were found (top level only).")
            print("   Drag a folder onto the script or pass a folder path to include subfolders.")
        return 1

    print(
        f"🔍 Found {len(kfx_files)} file(s)"
        f"{' (including subfolders)' if recursive else ' (top level only)'}. "
        f"Starting conversion...\n"
    )
    print("-" * 50)

    success_count = 0
    fail_count = 0

    for file in kfx_files:
        print(f"⏳ Parsing source file: {file.name}")

        try:
            book = YJ_Book(str(file))
            book.decode_book(retain_yj_locals=True)

            converter = KFX_EPUB(book)

            title = converter.title if converter.title else "Unknown"
            author = converter.authors[0] if converter.authors else "Unknown"

            raw_filename = f"[{author}] {title}"
            clean_filename = sanitize_filename(raw_filename) + ".epub"
            output_file = unique_output_file(output_path, clean_filename)

            if output_file.parent != output_path:
                print(f"📝 Expected output filename: {output_file.parent.name}/{clean_filename}")
            else:
                print(f"📝 Expected output filename: {clean_filename}")

            epub_data = converter.decompile_to_epub()
            book.final_actions()

            if epub_data:
                with open(output_file, "wb") as f:
                    f.write(epub_data)
                print("   ✅ EPUB generated successfully.")
                success_count += 1
            else:
                print("   ❌ Conversion failed: generated EPUB data is empty.")
                fail_count += 1

        except Exception as e:
            print(f"   ❌ Error: {e}")
            fail_count += 1

        print()

    print("-" * 50)
    print("🎉 Batch processing finished.")
    print(f"📊 Result: {success_count} succeeded, {fail_count} failed")
    return 0 if fail_count == 0 else 1


def run_old_batch(args):
    parser = argparse.ArgumentParser(
        description=(
            "Convert a single KFX/KFX-ZIP file, or KFX/KFX-ZIP files in a folder, "
            "to safe '[Author] Book Title.epub' output files."
        )
    )

    parser.add_argument(
        "input_dir",
        nargs="?",
        default=None,
        help=(
            "Input folder, or a single .kfx / .kfx-zip file. Defaults to archived_kfx next "
            "to this script (top level only). A folder passed here or dragged onto the "
            "script is searched recursively."
        ),
    )

    parser.add_argument(
        "output_dir",
        nargs="?",
        default=str(APP_DIR / "archived_epub"),
        help="Output folder. Defaults to archived_epub next to this script.",
    )

    parser.add_argument(
        "--no-pause",
        action="store_true",
        help="Accepted for CLI compatibility; this version never pauses.",
    )

    parsed = parser.parse_args(args)
    recursive = parsed.input_dir is not None
    input_dir = parsed.input_dir if recursive else str(APP_DIR / "archived_kfx")
    return batch_convert_kfx_to_epub(input_dir, parsed.output_dir, recursive=recursive)


def main(argv=None):
    args = list(sys.argv[1:] if argv is None else argv)

    # Windows 的 Ctrl+Break 預設會直接終止行程；轉成 KeyboardInterrupt，
    # 讓 convert 能以 CANCELLED（40）收尾並清掉 .part。
    if hasattr(signal, "SIGBREAK"):
        signal.signal(signal.SIGBREAK, _raise_keyboard_interrupt)

    try:
        if has_api_version(args):
            emit_text(str(API_VERSION))
            return 0

        command = first_meaningful_token(args)
        if command in RESERVED_COMMANDS:
            return run_subcommand(command, args)

        return run_old_batch(args)
    except KeyboardInterrupt:
        return 40


def _raise_keyboard_interrupt(signum, frame):
    raise KeyboardInterrupt


if __name__ == "__main__":
    sys.exit(main())
