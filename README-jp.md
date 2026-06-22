# kfx2epub

`KFX` / `KFX-ZIP` を `EPUB` に変換するためのツールです。

本ツールは日本の書籍のみを対象としています。

## 言語

- [繁體中文](README.md)
- [English](README-en.md)
- [日本語](README-jp.md)

## 現在の主な機能

- EPUB3 XHTML 出力を修正し、簡略化された `<!DOCTYPE html>` を保持します。
- 既定の `style-reset.css` を追加し、`book-style.css` から `@import "style-reset.css";` で読み込むことで、通常テキストの基本レイアウトスタイルを集約します。
- 固定レイアウトページでは専用の `fixed-layout-jp.css` を使用し、通常テキスト用スタイルと分離します。
- 左右ページ spread を持つ固定レイアウト漫画では、spread 指定のないページに既定で `rendition:page-spread-center` を補います。
- fixed-layout ページから不要な `book-style.css` をリンクしないようにします。
- XHTML から実際にリンクされている CSS ファイルのみを生成し、孤立した CSS ファイルを減らします。
- フォルダー内の `*.kfx` と `*.kfx-zip` を一括変換できます。
- 単一の `.kfx` / `.kfx-zip` ファイルを変換できます。
- 単一ファイルまたはフォルダー全体を `convert_kfx2epub.py` にドラッグ＆ドロップして実行できます。
- ドラッグ＆ドロップで実行した場合も、出力先は `convert_kfx2epub.py` と同じ場所にある `archived_epub` に固定されます。
- 出力 EPUB を `[著者] タイトル.epub` 形式で自動命名します。
- ファイル名に使えない文字を全角の安全な文字に自動変換します。
- 日本の書籍向けに、EPUB 内部のフォルダー、XHTML、OPF、CSS、画像ファイル名を、`item/standard.opf`、`navigation-documents.xhtml`、`p-cover.xhtml`、`p-toc.xhtml`、`p-fmatter-001.xhtml`、`p-001.xhtml`、`p-colophon.xhtml`、`book-style.css`、`style-reset.css`、`image/cover.ext`、`image/i-001.ext`、`image/p-001.ext` のように整理します。

## 機能

- フォルダー内の `*.kfx` と `*.kfx-zip` をスキャンできます。
- 単一の `*.kfx` / `*.kfx-zip` ファイルを直接処理できます。
- 単一ファイルまたはフォルダー全体を `convert_kfx2epub.py` にドラッグ＆ドロップできます。
- EPUB に変換します。
- `[著者] タイトル.epub` 形式で出力ファイル名を自動生成します。
- ファイル名に使えない文字を自動処理します。

## 使い方

### 実行方法

```bash
python convert_kfx2epub.py [input_dir_or_file] [output_dir]
```

### 引数

- `input_dir_or_file`
  - 入力フォルダー、または単一の `.kfx` / `.kfx-zip` ファイル。
  - 既定値は、スクリプトと同じ場所にある `archived_kfx` です。
- `output_dir`
  - 出力フォルダー。
  - 既定値は、スクリプトと同じ場所にある `archived_epub` です。

### 例

#### 1. 既定フォルダーを使う

```bash
python convert_kfx2epub.py
```

既定の入力元：

- `./archived_kfx`

既定の出力先：

- `./archived_epub`

#### 2. 単一ファイルまたはフォルダーを処理する

```bash
python convert_kfx2epub.py D:/books/sample.kfx-zip
```

単一の `.kfx` / `.kfx-zip` ファイル、またはフォルダー全体を `convert_kfx2epub.py` にドラッグ＆ドロップして実行することもできます。

この場合も、出力先は `convert_kfx2epub.py` と同じ場所にある `archived_epub` になります。

#### 3. 入力先と出力先を指定する

```bash
python convert_kfx2epub.py D:/books/kfx D:/books/epub
```

## 入力ファイル

このツールは以下のファイルを処理します。

- `*.kfx-zip`
- `*.kfx`

## 出力結果

出力ファイル名は以下の形式になります。

```text
[著者] タイトル.epub
```

例：

```text
[山田太郎] サンプルタイトル.epub
```

## EPUB 内部ファイル名の規則

変換後の EPUB では、現在日本の書籍向けに整理している規則に基づき、判別しにくい元の内部名を避け、できるだけ規則的で読みやすい内部ファイル名を使用します。これらの規則は、他の地域または他言語の書籍でも正しく処理できることを保証するものではありません。

### 主なフォルダーとファイル

- EPUB 内容のルートフォルダーは、一般的な `OEBPS/` の代わりに `item/` を使用します。
- OPF パッケージファイルは `item/standard.opf` になります。
- EPUB3 navigation document は `item/navigation-documents.xhtml` になります。
- 通常スタイルファイルは `item/style/book-style.css` になります。
- 既定の reset スタイルファイルは `item/style/style-reset.css` になります。

### XHTML

- 表紙ページは EPUB3 の `navigation-documents.xhtml` landmarks 内にある `epub:type="cover"` から判定し、`item/xhtml/p-cover.xhtml` になります。
- 書籍内の目次ページは EPUB3 の `navigation-documents.xhtml` landmarks 内にある `epub:type="toc"` から判定し、`item/xhtml/p-toc.xhtml` になります。
- spine 内で `p-toc.xhtml` を判定できる場合、表紙と目次の間にある XHTML は spine 順に `item/xhtml/p-fmatter-001.xhtml` から命名されます。
- `p-toc.xhtml` より後の通常本文ページは spine 順に `item/xhtml/p-001.xhtml` から命名されます。
- toc がない場合、通常本文ページは spine 順に `item/xhtml/p-001.xhtml` からの `p-` 系列を使用します。
- XHTML の連番は最低 3 桁を使用します。数が 999 を超える場合は、自動的に 4 桁、5 桁のように拡張されます。
- EPUB3 toc nav 内にタイトル文字列が `奥付` の項目がある場合、そのリンク先 XHTML は `item/xhtml/p-colophon.xhtml` になります。
- `epub:type="bodymatter"` / Beginning は表紙や目次の判定には使いません。表紙と同じページを指している場合の誤判定を避けるためです。

### 画像

- 画像フォルダーは `item/image/` を使用します。
- 表紙画像は `item/image/cover.ext` を使用します。
- 挿絵型の画像は `item/image/i-001.ext` から順番に命名されます。
- その他の通常画像は `item/image/p-001.ext` から順番に命名されます。
- 画像の連番は最低 3 桁を使用します。同じ種類の画像数が 999 を超える場合は、自動的に 4 桁、5 桁のように拡張されます。
- `ext` は実際の画像形式の拡張子を保持するという意味です。例：`.jpg`、`.png`、`.webp`。

挿絵型画像は、最終的な XHTML DOM から判定します。画像が `body > div > (任意) svg > img/image` に近い構造の中にある場合は挿絵画像として扱い、`i-` 系列を使用します。それ以外の画像は `p-` 系列を使用します。

## 注意事項

- 入力フォルダーには、変換したい KFX ファイルだけを入れてください。
- 間違ったファイルの変換、入力元の混在、または予期しない変換結果を避けるため、元の `.kfx` / `.kfx-zip` ファイルを保存しておくことを推奨します。必要に応じて再変換できます。
- 重複 fragment やコンテナー異常が発生する場合、KFX パッケージ内に AZW ファイルが 2 つ含まれていることが多いです。元ファイルを再取得することを推奨します。

## プロジェクト構成

- `convert_kfx2epub.py`：メインプログラムの入口。
- `kfxlib/`：KFX 解析と EPUB 生成のコア。

## 謝辞

本ツールは KFX Input / kfxlib 関連の成果を基に整理・利用しています。開発と保守を行っている jhowell 氏に感謝します。

関連スレッド：

- [MobileRead: KFX Input plugin](https://www.mobileread.com/forums/showthread.php?t=291290)
