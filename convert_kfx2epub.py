import argparse
import sys
from pathlib import Path

from kfxlib import YJ_Book
from kfxlib.yj_to_epub import KFX_EPUB


if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")


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


def batch_convert_kfx_to_epub(input_dir, output_dir):
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
        return

    output_path.mkdir(parents=True, exist_ok=True)

    if input_path.is_file():
        if input_path.suffix.lower() == ".kfx" or input_path.name.lower().endswith(".kfx-zip"):
            kfx_files = [input_path]
        else:
            print("💡 Hint: the input is a file, but it is not a .kfx or .kfx-zip file.")
            return
    else:
        kfx_files = list(input_path.glob("*.kfx-zip")) + list(input_path.glob("*.kfx"))

    if not kfx_files:
        print("💡 Hint: no .kfx-zip or .kfx files were found.")
        return

    print(f"🔍 Found {len(kfx_files)} file(s). Starting conversion...\n")
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
            output_file = output_path / clean_filename

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


if __name__ == "__main__":
    script_dir = Path(__file__).parent.resolve()

    parser = argparse.ArgumentParser(
        description=(
            "Convert a single KFX/KFX-ZIP file, or KFX/KFX-ZIP files in a folder, "
            "to safe '[Author] Book Title.epub' output files."
        )
    )

    parser.add_argument(
        "input_dir",
        nargs="?",
        default=str(script_dir / "output_kfx-zip"),
        help="Input folder, or a single .kfx / .kfx-zip file. Defaults to output_kfx-zip next to this script.",
    )

    parser.add_argument(
        "output_dir",
        nargs="?",
        default=str(script_dir / "output_epub"),
        help="Output folder. Defaults to output_epub next to this script.",
    )

    args = parser.parse_args()
    batch_convert_kfx_to_epub(args.input_dir, args.output_dir)
