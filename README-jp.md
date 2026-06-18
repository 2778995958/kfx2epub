# kfx2epub

Kindle の `KFX` / `KFX-ZIP` を `EPUB` に変換するためのツールです。

## 言語

- [繁體中文](README.md)
- [English](README_en.md)
- [日本語](README_jp.md)

## 現在の主な機能

- EPUB3 XHTML 出力を修正し、簡略化された `<!DOCTYPE html>` を保持します。
- 既定の `reset.css` を追加し、`stylesheet.css` から `@import "reset.css";` で読み込むことで、通常テキストの基本レイアウトスタイルを集約します。
- 固定レイアウトページでは専用の `fixed-layout.css` を使用し、通常テキスト用スタイルと分離します。
- fixed-layout ページから不要な `stylesheet.css` をリンクしないようにします。
- XHTML から実際にリンクされている CSS ファイルのみを生成し、孤立した CSS ファイルを減らします。
- フォルダー内の `*.kfx` と `*.kfx-zip` を一括変換できます。
- 単一の `.kfx` / `.kfx-zip` ファイルを変換できます。
- 単一ファイルまたはフォルダー全体を `convert_books.py` にドラッグ＆ドロップして実行できます。
- ドラッグ＆ドロップで実行した場合も、出力先は `convert_books.py` と同じ場所にある `output_epub` に固定されます。
- 出力 EPUB を `[作者] 書名.epub` 形式で自動命名します。
- ファイル名に使えない文字を全角の安全な文字に自動変換します。
- EPUB 内部の XHTML と画像ファイル名を、`p-0000.xhtml`、`cover.xhtml`、`toc.xhtml`、`cover.ext`、`i-0000.ext`、`p-0000.ext` のような規則的な名前にします。

## 機能

- フォルダー内の `*.kfx` と `*.kfx-zip` をスキャンできます。
- 単一の `*.kfx` / `*.kfx-zip` ファイルを直接処理できます。
- 単一ファイルまたはフォルダー全体を `convert_books.py` にドラッグ＆ドロップできます。
- EPUB に変換します。
- `[作者] 書名.epub` 形式で出力ファイル名を自動生成します。
- ファイル名に使えない文字を自動処理します。

## 使い方

### 実行方法

```bash
python convert_books.py [input_dir_or_file] [output_dir]
```

### 引数

- `input_dir_or_file`
  - 入力フォルダー、または単一の `.kfx` / `.kfx-zip` ファイル。
  - 既定値は、スクリプトと同じ場所にある `output_kfx-zip` です。
- `output_dir`
  - 出力フォルダー。
  - 既定値は、スクリプトと同じ場所にある `output_epub` です。

### 例

#### 1. 既定フォルダーを使う

```bash
python convert_books.py
```

既定の入力元：

- `./output_kfx-zip`

既定の出力先：

- `./output_epub`

#### 2. 単一ファイルまたはフォルダーを処理する

```bash
python convert_books.py D:/books/sample.kfx-zip
```

単一の `.kfx` / `.kfx-zip` ファイル、またはフォルダー全体を `convert_books.py` にドラッグ＆ドロップして実行することもできます。

この場合も、出力先は `convert_books.py` と同じ場所にある `output_epub` になります。

#### 3. 入力先と出力先を指定する

```bash
python convert_books.py D:/books/kfx D:/books/epub
```

## 入力ファイル

このツールは以下のファイルを処理します。

- `*.kfx-zip`
- `*.kfx`

## 出力結果

出力ファイル名は以下の形式になります。

```text
[作者] 書名.epub
```

例：

```text
[山田太郎] サンプル書名.epub
```

## EPUB 内部ファイル名の規則

変換後の EPUB では、KFX の元の section/resource 名をそのまま使わず、できるだけ規則的で読みやすい内部ファイル名を使用します。これにより、文字化けのように見えるファイル名を避けます。

### XHTML

- 通常の本文ページは `xhtml/p-0000.xhtml` から順番に命名されます。
- 表紙ページは EPUB3 の `nav.xhtml` landmarks 内にある `epub:type="cover"` から判定し、`xhtml/cover.xhtml` になります。
- 書籍内の目次ページは EPUB3 の `nav.xhtml` landmarks 内にある `epub:type="toc"` から判定し、`xhtml/toc.xhtml` になります。
- `epub:type="bodymatter"` / Beginning は表紙や目次の判定には使いません。表紙と同じページを指している場合の誤判定を避けるためです。

### 画像

- 表紙画像は `images/cover.ext` を使用します。
- 挿絵型の画像は `images/i-0000.ext` から順番に命名されます。
- その他の通常画像は `images/p-0000.ext` から順番に命名されます。
- `ext` は実際の画像形式の拡張子を保持するという意味です。例：`.jpg`、`.png`、`.webp`。

挿絵型画像は、最終的な XHTML DOM から判定します。画像が `body > div > (任意) svg > img/image` に近い構造の中にある場合は挿絵画像として扱い、`i-` 系列を使用します。それ以外の画像は `p-` 系列を使用します。

## 注意事項

- 入力フォルダーには、変換したい KFX ファイルだけを入れてください。
- 間違ったファイルの変換、入力元の混在、または予期しない変換結果を避けるため、元の `.kfx` / `.kfx-zip` ファイルを保存しておくことを推奨します。必要に応じて再変換できます。
- 重複 fragment やコンテナー異常が発生する場合、KFX パッケージ内に AZW ファイルが 2 つ含まれていることが多いです。元ファイルを再取得することを推奨します。

## プロジェクト構成

- `convert_books.py`：メインプログラムの入口。
- `kfxlib/`：KFX 解析と EPUB 生成のコア。

## 謝辞

本ツールは KFX Input / kfxlib 関連の成果を基に整理・利用しています。開発と保守を行っている jhowell 氏に感謝します。

関連スレッド：

- [MobileRead: KFX Input plugin](https://www.mobileread.com/forums/showthread.php?t=291290)
