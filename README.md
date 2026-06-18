# kfx2epub

將 Kindle 的 `KFX` / `KFX-ZIP` 轉換為 `EPUB` 的工具。

本工具目前只針對日本 Kindle 書籍使用與測試，其他地區或語系的 KFX 書籍不保證能正常轉換或取得理想排版結果。

## 語言版本

- [繁體中文](README.md)
- [English](README-en.md)
- [日本語](README-jp.md)

## 目前版本功能

- 修正 EPUB3 XHTML 輸出，保留簡化的 `<!DOCTYPE html>`
- 加入預設 `style-reset.css`，由 `book-style.css` 透過 `@import "style-reset.css";` 載入，集中管理一般文字排版基礎樣式
- 固定版式頁面改用獨立的 `fixed-layout-jp.css`，和一般文字頁樣式分開
- 避免 fixed-layout 頁面同時連結多餘的 `book-style.css`
- 只產生實際被 XHTML 連結的 CSS 檔案，減少孤兒 CSS
- 支援資料夾批次轉換 `*.kfx` 與 `*.kfx-zip`
- 支援單一 `.kfx` / `.kfx-zip` 檔案轉換
- 支援將單一檔案或整個資料夾拖曳到 `convert_kfx2epub.py` 上執行
- 拖曳檔案或資料夾時，輸出仍固定寫入 `convert_kfx2epub.py` 同目錄下的 `output_epub`
- 自動以 `[作者] 書名.epub` 格式命名輸出
- 自動將檔名中的非法字元轉為全型安全字元
- EPUB 內部目錄、XHTML、OPF、CSS 與圖片檔名改用規律命名，例如 `item/standard.opf`、`navigation-documents.xhtml`、`p-cover.xhtml`、`p-toc.xhtml`、`p-fmatter-001.xhtml`、`p-colophon.xhtml`、`book-style.css`、`style-reset.css`、`image/cover.ext`、`image/i-000.ext`、`image/p-000.ext`

## 功能

- 可批次掃描資料夾中的 `*.kfx` 與 `*.kfx-zip`
- 可直接處理單一 `*.kfx` / `*.kfx-zip` 檔案
- 可拖曳單一檔案或整個資料夾到 `convert_kfx2epub.py` 執行
- 轉換為 EPUB
- 以 `[作者] 書名.epub` 的格式自動命名輸出
- 會自動處理檔名中的非法字元

## 使用方法

### 執行方式

```bash
python convert_kfx2epub.py [input_dir_or_file] [output_dir]
```

### 參數

- `input_dir_or_file`
  - 輸入資料夾，或單一 `.kfx` / `.kfx-zip` 檔案
  - 預設為腳本同目錄下的 `output_kfx-zip`
- `output_dir`
  - 輸出目錄
  - 預設為腳本同目錄下的 `output_epub`

### 範例

#### 1. 使用預設目錄

```bash
python convert_kfx2epub.py
```

預設會讀取：

- `./output_kfx-zip`

輸出到：

- `./output_epub`

#### 2. 處理單一檔案或資料夾

```bash
python convert_kfx2epub.py D:/books/sample.kfx-zip
```

或直接把單一 `.kfx` / `.kfx-zip` 檔案、或整個資料夾拖曳到 `convert_kfx2epub.py` 上執行。

這時候輸出仍會寫到 `convert_kfx2epub.py` 同目錄下的 `output_epub`。

#### 3. 指定輸入與輸出目錄

```bash
python convert_kfx2epub.py D:/books/kfx D:/books/epub
```

## 輸入檔案

工具會處理以下檔案：

- `*.kfx-zip`
- `*.kfx`

## 輸出結果

輸出的檔名格式如下：

```text
[作者] 書名.epub
```

例如：

```text
[山田太郎] 範例書名.epub
```

## EPUB 內部檔名規則

轉換後的 EPUB 會盡量使用規律、易讀的內部檔名，避免沿用 KFX 原始 section/resource 名稱造成看似亂碼的檔名。

### 主要目錄與檔案

- EPUB 內容根目錄使用 `item/`，取代一般常見的 `OEBPS/`。
- OPF 套件檔使用 `item/standard.opf`。
- EPUB3 navigation document 使用 `item/navigation-documents.xhtml`。
- 一般樣式檔使用 `item/style/book-style.css`。
- 預設 reset 樣式檔使用 `item/style/style-reset.css`。

### XHTML

- 封面頁依 EPUB3 `navigation-documents.xhtml` landmarks 中的 `epub:type="cover"` 判斷，命名為 `item/xhtml/p-cover.xhtml`。
- 書內目錄頁依 EPUB3 `navigation-documents.xhtml` landmarks 中的 `epub:type="toc"` 判斷，命名為 `item/xhtml/p-toc.xhtml`。
- 若 spine 中能判斷出 `p-toc.xhtml`，則 cover 與 toc 中間的 XHTML 依 spine 順序命名為 `item/xhtml/p-fmatter-001.xhtml` 起。
- `p-toc.xhtml` 之後的一般內容頁依 spine 順序命名為 `item/xhtml/p-001.xhtml` 起。
- 若沒有 toc，則一般內容頁依 spine 順序使用 `item/xhtml/p-001.xhtml` 起的 `p-` 序列。
- XHTML 序號至少使用 3 位數；若數量超過 999，會自動擴展為 4 位、5 位等。
- 若 EPUB3 toc nav 中有標題文字為 `奥付` 的項目，該目標 XHTML 會命名為 `item/xhtml/p-colophon.xhtml`。
- `epub:type="bodymatter"` / Beginning 不作為封面或目錄判斷依據，避免和封面同頁時誤判。

### 圖片

- 圖片目錄使用 `item/image/`。
- 封面圖片使用 `item/image/cover.ext`。
- 插圖型圖片使用 `item/image/i-000.ext` 起依序命名。
- 其他一般圖片使用 `item/image/p-000.ext` 起依序命名。
- 圖片序號至少使用 3 位數；若同類圖片數量超過 999，會自動擴展為 4 位、5 位等。
- `ext` 代表保留實際圖片副檔名，例如 `.jpg`、`.png`、`.webp`。

插圖型圖片會依最終 XHTML DOM 判斷：若圖片位於類似 `body > div > (如有) svg > img/image` 的結構中，會歸類為插圖圖片並使用 `i-` 序列；其他圖片則使用 `p-` 序列。

## 注意事項

- 輸入資料夾應只放要轉換的 KFX 檔案。
- 為避免轉錯檔、輸入來源不乾淨或轉換結果不如預期，建議保留原始 `.kfx` / `.kfx-zip` 檔案，方便重新轉換。
- 若遇到重複 fragment 或容器異常，通常是KFX打包了2份azw檔，建議重新取得原始檔案。

## 專案結構

- `convert_kfx2epub.py`：主程式入口
- `kfxlib/`：KFX 解析與 EPUB 產生核心

## 致謝

本工具基於 KFX Input / kfxlib 相關成果整理與使用，感謝作者 jhowell 的開發與維護。

相關討論區：

- [MobileRead: KFX Input plugin](https://www.mobileread.com/forums/showthread.php?t=291290)