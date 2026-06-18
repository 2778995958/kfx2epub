# kfx2epub

A tool for converting Kindle `KFX` / `KFX-ZIP` files to `EPUB`.

This tool is currently intended and tested only for Japanese Kindle books. KFX books from other regions or languages are not guaranteed to convert correctly or preserve ideal layout results.

## Languages

- [繁體中文](README.md)
- [English](README-en.md)
- [日本語](README-jp.md)

## Current Features

- Fixes EPUB3 XHTML output and keeps the simplified `<!DOCTYPE html>`.
- Adds a default `style-reset.css`, imported by `book-style.css` through `@import "style-reset.css";`, to centralize basic text layout styles.
- Uses a separate `fixed-layout-jp.css` for fixed-layout pages, keeping it separate from normal text styles.
- Avoids linking unnecessary `book-style.css` from fixed-layout pages.
- Generates only CSS files that are actually linked by XHTML, reducing orphan CSS files.
- Supports batch conversion of `*.kfx` and `*.kfx-zip` files in a folder.
- Supports converting a single `.kfx` / `.kfx-zip` file.
- Supports dragging a single file or an entire folder onto `convert_kfx2epub.py`.
- When using drag-and-drop, output is still written to the `output_epub` folder next to `convert_kfx2epub.py`.
- Automatically names output files in the `[Author] Book Title.epub` format.
- Automatically converts illegal filename characters into full-width safe characters.
- Uses regular internal EPUB names for folders, XHTML, OPF, CSS, and images, such as `item/standard.opf`, `navigation-documents.xhtml`, `p-cover.xhtml`, `p-toc.xhtml`, `p-fmatter-001.xhtml`, `p-colophon.xhtml`, `book-style.css`, `style-reset.css`, `image/cover.ext`, `image/i-000.ext`, and `image/p-000.ext`.

## Features

- Scans folders for `*.kfx` and `*.kfx-zip` files.
- Processes a single `*.kfx` / `*.kfx-zip` file directly.
- Allows dragging a single file or an entire folder onto `convert_kfx2epub.py`.
- Converts files to EPUB.
- Automatically names output files as `[Author] Book Title.epub`.
- Automatically handles illegal filename characters.

## Usage

### Command

```bash
python convert_kfx2epub.py [input_dir_or_file] [output_dir]
```

### Arguments

- `input_dir_or_file`
  - Input folder, or a single `.kfx` / `.kfx-zip` file.
  - Defaults to the `output_kfx-zip` folder next to the script.
- `output_dir`
  - Output folder.
  - Defaults to the `output_epub` folder next to the script.

### Examples

#### 1. Use default folders

```bash
python convert_kfx2epub.py
```

Default input:

- `./output_kfx-zip`

Default output:

- `./output_epub`

#### 2. Process a single file or folder

```bash
python convert_kfx2epub.py D:/books/sample.kfx-zip
```

You can also drag a single `.kfx` / `.kfx-zip` file, or an entire folder, onto `convert_kfx2epub.py`.

In this case, output is still written to the `output_epub` folder next to `convert_kfx2epub.py`.

#### 3. Specify input and output folders

```bash
python convert_kfx2epub.py D:/books/kfx D:/books/epub
```

## Input Files

The tool processes the following files:

- `*.kfx-zip`
- `*.kfx`

## Output Result

Output filenames use this format:

```text
[Author] Book Title.epub
```

Example:

```text
[Yamada Taro] Sample Book.epub
```

## EPUB Internal Filename Rules

The converted EPUB tries to use regular, readable internal filenames instead of the original KFX section/resource names, which can look like garbled text.

### Main Folders and Files

- The EPUB content root folder is `item/`, replacing the common `OEBPS/` folder name.
- The OPF package file is `item/standard.opf`.
- The EPUB3 navigation document is `item/navigation-documents.xhtml`.
- The normal style file is `item/style/book-style.css`.
- The default reset style file is `item/style/style-reset.css`.

### XHTML

- The cover page is detected from `epub:type="cover"` in the EPUB3 `navigation-documents.xhtml` landmarks and named `item/xhtml/p-cover.xhtml`.
- The in-book table of contents page is detected from `epub:type="toc"` in the EPUB3 `navigation-documents.xhtml` landmarks and named `item/xhtml/p-toc.xhtml`.
- If `p-toc.xhtml` can be identified in the spine, XHTML files between cover and toc are named sequentially from `item/xhtml/p-fmatter-001.xhtml` according to spine order.
- Normal content pages after `p-toc.xhtml` are named sequentially from `item/xhtml/p-001.xhtml` according to spine order.
- If there is no toc, normal content pages use the `p-` sequence from `item/xhtml/p-001.xhtml` according to spine order.
- XHTML sequence numbers use at least 3 digits. If the count exceeds 999, the width expands automatically to 4 digits, 5 digits, and so on.
- If an EPUB3 toc nav item has the title text `奥付`, its target XHTML is named `item/xhtml/p-colophon.xhtml`.
- `epub:type="bodymatter"` / Beginning is not used to identify the cover or table of contents, to avoid false detection when it points to the same page as the cover.

### Images

- The image folder is `item/image/`.
- The cover image uses `item/image/cover.ext`.
- Illustration-style images are named sequentially from `item/image/i-000.ext`.
- Other normal images are named sequentially from `item/image/p-000.ext`.
- Image sequence numbers use at least 3 digits. If the number of images in the same type exceeds 999, the width expands automatically to 4 digits, 5 digits, and so on.
- `ext` means the actual image extension is preserved, such as `.jpg`, `.png`, or `.webp`.

Illustration-style images are detected from the final XHTML DOM. If an image is in a structure similar to `body > div > (optional) svg > img/image`, it is treated as an illustration image and uses the `i-` sequence. Other images use the `p-` sequence.

## Notes

- The input folder should contain only the KFX files you want to convert.
- To avoid converting the wrong file, using an unclean input source, or getting unexpected output, it is recommended to keep the original `.kfx` / `.kfx-zip` files so you can convert them again.
- If duplicate fragments or container errors occur, it is usually because the KFX package contains two AZW files. Re-acquiring the original file is recommended.

## Project Structure

- `convert_kfx2epub.py`: main program entry point.
- `kfxlib/`: KFX parsing and EPUB generation core.

## Credits

This tool is based on and uses work related to KFX Input / kfxlib. Thanks to jhowell for developing and maintaining it.

Related discussion:

- [MobileRead: KFX Input plugin](https://www.mobileread.com/forums/showthread.php?t=291290)
