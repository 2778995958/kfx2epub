import argparse
from concurrent.futures import ProcessPoolExecutor, as_completed
import multiprocessing
import os
from pathlib import Path
import sys


def app_dir():
    if getattr(sys, "frozen", False):
        return Path(sys.executable).parent.resolve()
    return Path(__file__).parent.resolve()


APP_DIR = app_dir()
if str(APP_DIR) not in sys.path:
    sys.path.insert(0, str(APP_DIR))

from kfxlib import YJ_Book
from kfxlib.yj_to_epub import KFX_EPUB

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")


def get_default_workers():
    """Calculate half of the available CPU cores (at least 1)."""
    total_cores = os.cpu_count() or 1
    return max(1, total_cores // 2)


def sanitize_filename(name):
    """Convert characters that are not allowed in filesystem filenames to safe full-width variants."""
    table = str.maketrans(
        {
            "\\": "＼",
            "/": "／",
            ":": "：",
            "*": "＊",
            "?": "？",
            '"': "＂",
            "<": "＜",
            ">": "＞",
            "|": "｜",
        }
    )
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


def convert_single_file(file_path, output_path):
    """Worker function: handles conversion for a single KFX file."""
    file = Path(file_path)
    try:
        book = YJ_Book(str(file))
        book.decode_book(retain_yj_locals=True)

        converter = KFX_EPUB(book)

        title = converter.title if converter.title else "Unknown"
        author = converter.authors[0] if converter.authors else "Unknown"

        raw_filename = f"[{author}] {title}"
        clean_filename = sanitize_filename(raw_filename) + ".epub"
        output_file = unique_output_file(output_path, clean_filename)

        epub_data = converter.decompile_to_epub()
        book.final_actions()

        if epub_data:
            with open(output_file, "wb") as f:
                f.write(epub_data)
            return True, file.name, output_file.name, None
        else:
            return False, file.name, None, "Generated EPUB data is empty."

    except Exception as e:
        return False, file.name, None, str(e)


def batch_convert_kfx_to_epub(input_dir, output_dir, max_workers=None, recursive=True):
    """Convert KFX/KFX-ZIP files to EPUB using multi-processing."""
    input_path = Path(input_dir).resolve()
    output_path = Path(output_dir).resolve()

    if max_workers is None:
        max_workers = get_default_workers()

    print(f"📂 Input path: {input_path}")
    print(f"📂 Output folder: {output_path}")
    print(f"⚡ Active workers: {max_workers} (Defaulting to half CPU cores)")

    if not input_path.exists():
        print(f"❌ Error: input path not found: '{input_path}'")
        return

    output_path.mkdir(parents=True, exist_ok=True)

    if input_path.is_file():
        if (
            input_path.suffix.lower() == ".kfx"
            or input_path.name.lower().endswith(".kfx-zip")
        ):
            kfx_files = [input_path]
        else:
            print(
                "💡 Hint: the input is a file, but it is not a .kfx or .kfx-zip file."
            )
            return
    else:
        if recursive:
            kfx_files = list(input_path.rglob("*.kfx-zip")) + list(
                input_path.rglob("*.kfx")
            )
        else:
            kfx_files = list(input_path.glob("*.kfx-zip")) + list(
                input_path.glob("*.kfx")
            )

    if not kfx_files:
        if recursive:
            print("💡 Hint: no .kfx-zip or .kfx files were found (including subfolders).")
        else:
            print("💡 Hint: no .kfx-zip or .kfx files were found (top level only).")
            print(
                "   Drag a folder onto the script or pass a folder path to include subfolders."
            )
        return

    print(
        f"🔍 Found {len(kfx_files)} file(s)"
        f"{' (including subfolders)' if recursive else ' (top level only)'}. "
        f"Starting parallel conversion...\n"
    )
    print("-" * 50)

    success_count = 0
    fail_count = 0

    with ProcessPoolExecutor(max_workers=max_workers) as executor:
        futures = {
            executor.submit(convert_single_file, file, output_path): file
            for file in kfx_files
        }

        for future in as_completed(futures):
            success, src_name, out_name, err_msg = future.result()
            if success:
                print(f"✅ [{src_name}] -> {out_name}")
                success_count += 1
            else:
                print(f"❌ [{src_name}] Error: {err_msg}")
                fail_count += 1

    print("-" * 50)
    print("🎉 Batch processing finished.")
    print(f"📊 Result: {success_count} succeeded, {fail_count} failed")


if __name__ == "__main__":
    # Essential for PyInstaller / cx_Freeze packaging support
    multiprocessing.freeze_support()

    script_dir = APP_DIR
    default_workers = get_default_workers()

    parser = argparse.ArgumentParser(
        description="Convert KFX/KFX-ZIP files to EPUB in parallel using half CPU cores by default."
    )

    parser.add_argument(
        "input_dir",
        nargs="?",
        default=None,
        help=(
            "Input folder or single file. Defaults to archived_kfx next to this script "
            "(top level only). A folder passed here or dragged onto the script is searched "
            "recursively."
        ),
    )

    parser.add_argument(
        "output_dir",
        nargs="?",
        default=str(script_dir / "archived_epub"),
        help="Output folder.",
    )

    parser.add_argument(
        "-w",
        "--workers",
        type=int,
        default=default_workers,
        help=f"Number of parallel workers (default: {default_workers}, which is half CPU cores).",
    )

    args = parser.parse_args()
    recursive = args.input_dir is not None
    input_dir = args.input_dir if recursive else str(script_dir / "archived_kfx")
    batch_convert_kfx_to_epub(
        input_dir,
        args.output_dir,
        max_workers=args.workers,
        recursive=recursive,
    )