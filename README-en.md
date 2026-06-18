# kfx2epub

A tool for converting Kindle `KFX` / `KFX-ZIP` files to `EPUB`.

## Languages

- [繁體中文](README.md)
- [English](README_en.md)
- [日本語](README_jp.md)

## Current Features

- Fixes EPUB3 XHTML output and keeps the simplified `<!DOCTYPE html>`.
- Adds a default `reset.css`, imported by `stylesheet.css` through `@import "reset.css";`, to centralize basic text layout styles.
- Uses a separate `fixed-layout.css` for fixed-layout pages, keeping it separate from normal text styles.
- Avoids linking unnecessary `stylesheet.css` from fixed-layout pages.
- Generates only CSS files that are actually linked by XHTML, reducing orphan CSS files.
- Supports batch conversion of `*.kfx` and `*.kfx-zip` files in a folder.
- Supports converting a single `.kfx` / `.kfx-zip` file.
- Supports dragging a single file or an entire folder onto `convert_books.py`.
- When using drag-and-drop, output is still written to the `output_epub` folder next to `convert_books.py`.
- Automatically names output files in the `[Author] Book Title.epub` format.
- Automatically converts illegal filename characters into full-width safe characters.
- Uses regular internal EPUB filenames for XHTML and images, such as `p-0000.xhtml`, `cover.xhtml`, `toc.xhtml`, `cover.ext`, `i-0000.ext`, and `p-0000.ext`.

## Features

- Scans folders for `*.kfx` and `*.kfx-zip` files.
- Processes a single `*.kfx` / `*.kfx-zip` file directly.
- Allows dragging a single file or an entire folder onto `convert_books.py`.
- Converts files to EPUB.
- Automatically names output files as `[Author] Book Title.epub`.
- Automatically handles illegal filename characters.

## Usage

### Command

```bash
python convert_books.py [input_dir_or_file] [output_dir]
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
python convert_books.py
```

Default input:

- `./output_kfx-zip`

Default output:

- `./output_epub`

#### 2. Process a single file or folder

```bash
python convert_books.py D:/books/sample.kfx-zip
```

You can also drag a single `.kfx` / `.kfx-zip` file, or an entire folder, onto `convert_books.py`.

In this case, output is still written to the `output_epub` folder next to `convert_books.py`.

#### 3. Specify input and output folders

```bash
python convert_books.py D:/books/kfx D:/books/epub
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

### XHTML

- Normal content pages are named sequentially from `xhtml/p-0000.xhtml`.
- The cover page is detected from `epub:type="cover"` in the EPUB3 `nav.xhtml` landmarks and named `xhtml/cover.xhtml`.
- The in-book table of contents page is detected from `epub:type="toc"` in the EPUB3 `nav.xhtml` landmarks and named `xhtml/toc.xhtml`.
- `epub:type="bodymatter"` / Beginning is not used to identify the cover or table of contents, to avoid false detection when it points to the same page as the cover.

### Images

- The cover image uses `images/cover.ext`.
- Illustration-style images are named sequentially from `images/i-0000.ext`.
- Other normal images are named sequentially from `images/p-0000.ext`.
- `ext` means the actual image extension is preserved, such as `.jpg`, `.png`, or `.webp`.

Illustration-style images are detected from the final XHTML DOM. If an image is in a structure similar to `body > div > (optional) svg > img/image`, it is treated as an illustration image and uses the `i-` sequence. Other images use the `p-` sequence.

## Notes

- The input folder should contain only the KFX files you want to convert.
- To avoid converting the wrong file, using an unclean input source, or getting unexpected output, it is recommended to keep the original `.kfx` / `.kfx-zip` files so you can convert them again.
- If duplicate fragments or container errors occur, it is usually because the KFX package contains two AZW files. Re-acquiring the original file is recommended.

## Project Structure

- `convert_books.py`: main program entry point.
- `kfxlib/`: KFX parsing and EPUB generation core.

## Credits

This tool is based on and uses work related to KFX Input / kfxlib. Thanks to jhowell for developing and maintaining it.

Related discussion:

- [MobileRead: KFX Input plugin](https://www.mobileread.com/forums/showthread.php?t=291290)
