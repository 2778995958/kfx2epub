# 上游更新整合維護指南

本文件記錄如何將新的 `KFX Input` 上游版本整合到本專案，並保留本專案針對日本 EPUB 的獨家輸出格式。

這不是一般使用說明；一般使用者請參閱 [README.md](README.md)。

## 1. 整合原則

每次上游更新都必須同時保留兩類功能：

1. **上游核心能力**：KFX/KPF 容器解析、KFX 版本、dictionary、PDF 與資源解碼。
2. **本專案獨家 overlay**：日本 EPUB 目錄、檔名、CSS、圖片分類、頁面重命名及輸出流程。

不可直接把上游的同名 `kfxlib/` 整個覆蓋目前版本。兩邊都修改過的檔案必須以三方差異為依據手動合併。

最重要的判定基準是作者原始版本 `c7e82b0`：

```text
c7e82b0 → 目前本專案       = 本地修改
c7e82b0 → 新上游快照       = 上游修改
```

不要用既有 `archived_epub` 的 binary 差異判斷上游是否改動。那些 EPUB 是經過本專案客製化後的輸出，差異可能只是本地格式、產生時間或 ZIP metadata。

## 2. 準備新上游來源

將新的上游版本放在工作樹外或明確命名的來源目錄，例如：

```text
KFX InputvYYYYMMDD/
```

來源目錄只用來讀取與比較，不應直接作為正式發佈目錄。先記錄：

- 上游版本日期；
- 上游來源目錄；
- 目前分支與基準 commit；
- 本次整合前的測試結果。

開始前先檢查工作樹：

```bash
git status --short
git log --oneline --decorate -5
```

若工作樹已有本地修改，不要用會覆蓋檔案的批次複製命令。先列出差異，確認每個檔案的處理方式。

## 3. 三方差異分析

### 3.1 取得作者原始檔案

對單一檔案，可直接取得 `c7e82b0` 的內容：

```bash
git show c7e82b0:kfxlib/<file> > /tmp/base_<file>
```

Windows PowerShell 也可以使用：

```powershell
git show c7e82b0:kfxlib/<file> | Set-Content -Encoding utf8 base_<file>
```

### 3.2 比較三個版本

每個重要檔案都要區分：

```text
BASE       = git show c7e82b0:kfxlib/<file>
LOCAL      = 目前工作樹的 kfxlib/<file>
UPSTREAM   = KFX InputvYYYYMMDD/kfxlib/<file>
```

比較時回答三個問題：

1. `LOCAL` 相對 `BASE` 改了什麼？這通常是必須保留的本地功能。
2. `UPSTREAM` 相對 `BASE` 改了什麼？這通常是要同步的上游功能。
3. `LOCAL` 與 `UPSTREAM` 是否修改同一段程式？若是，必須手動合併，而不是選擇任一整檔版本。

可使用：

```bash
git diff c7e82b0 -- kfxlib/<file>
git diff --no-index base_<file> "KFX InputvYYYYMMDD/kfxlib/<file>"
git diff --no-index "KFX InputvYYYYMMDD/kfxlib/<file>" kfxlib/<file>
```

分析範圍應排除：

```text
__pycache__/
*.pyc
archived_kfx/
archived_epub*/
dist/
build/
測試輸出與暫存檔
```

### 3.3 分類結果

每個檔案應歸入下列其中一類：

- **上游新增**：直接加入，但仍檢查 import 與路徑。
- **上游單方面修改**：以新版為基礎同步。
- **本地單方面修改**：保留目前版本。
- **雙方都修改**：以新版核心為基礎，把本地 overlay 手動套回。
- **上游 plugin 專用檔案**：只有在 standalone 執行流程實際需要時才加入；不要因為檔案存在就把 Calibre 依賴帶入 CLI。

## 4. 上游核心同步範圍

新版上游核心通常優先檢查以下模組：

```text
kfx_container.py
kpf_book.py
kpf_container.py
resources.py
unpack_container.py
yj_book.py
yj_container.py
yj_structure.py
yj_symbol_catalog.py
yj_versions.py
yj_to_epub_metadata.py
yj_to_epub_misc.py
yj_to_epub_notebook.py
yj_to_image_book.py
```

需要特別確認：

### KFX/KPF

- `.kdf`、`.kcb`、`.kdf-journal` 是否能讀取；
- SQLite schema 是否更新；
- `WITHOUT ROWID` table 是否支援；
- `open_kpf_database()` 與 container 初始化參數是否相容；
- `ignore_drm` 的預設行為不可被意外改成自動略過 DRM。

### Dictionary

- dictionary DRM-free 檢查；
- auxiliary fragment；
- `yj.dictionary.term`；
- `yj.dictionary.rules`；
- `idx:entry`、`idx:orth`、`idx:infl`；
- 未使用及未定義 inflection rule 的診斷訊息。

### PDF

- `PdfReader` 是否可重用；
- `pdf_cache` 是否以 PDF bytes 正確快取；
- page size 與 page box；
- PDF page 轉圖片時是否傳入 cache；
- fixed-layout / PDF-backed 書籍的 viewport 與頁面比例。

### 版本與共用 API

- `kfxlib/version.py` 應同步上游版本；
- `kfxlib/__init__.py` 的 export 必須與 `utilities.py` 實際函式一致；
- 新版呼叫若改變參數名稱或順序，應提供相容 wrapper，而不是破壞舊呼叫端。

## 5. 必須保留的本地 EPUB overlay

以下是本專案的獨家輸出契約。上游更新後必須逐項檢查，不能被同名上游檔案覆蓋。

### 5.1 EPUB 路徑與檔名

`kfxlib/epub_output.py` 必須繼續提供：

```text
item/standard.opf
item/navigation-documents.xhtml
item/xhtml/p-cover.xhtml
item/xhtml/p-toc.xhtml
item/xhtml/p-fmatter-001.xhtml
item/xhtml/p-001.xhtml
item/xhtml/p-colophon.xhtml
item/image/cover.ext
item/image/i-001.ext
item/image/p-001.ext
item/style/book-style.css
```

要保留的判定規則：

- cover landmark → `p-cover.xhtml`；
- toc landmark → `p-toc.xhtml`；
- cover 與 toc 之間 → `p-fmatter-*`；
- toc 後正文 → `p-*`；
- toc nav 中標題為 `奥付` → `p-colophon.xhtml`；
- XHTML 與圖片序號至少三位數，超過 999 時自動擴大；
- `bodymatter` / `Beginning` 不可單獨用來判斷 cover 或 toc。

### 5.2 圖片角色與重新命名

圖片角色為：

```text
cover       → cover.ext
illustration → i-001.ext
page        → p-001.ext
```

圖片重新命名必須更新所有相關項目：

- 實際檔案；
- XHTML `src` / `href`；
- SVG `image` reference；
- OPF manifest；
- manifest id 與 properties；
- 其他引用該資源的 DOM。

目前正文圖片由最終 XHTML DOM 統一判定，相關函式包括：

```python
prepare_dom_image_sequence_widths()
prepare_dom_image_filenames()
dom_image_role()
rename_image_file()
```

不要同時在正文 resource stage 與 DOM stage 各命名一次，否則可能把第一張 `p-001` 錯誤跳成 `p-002`。

封面若由 `force_cover` 產生，manifest entry 必須設定：

```python
manifest_entry.is_cover_image = True
```

以確保 OPF 產生：

```xml
properties="cover-image"
```

### 5.3 CSS 與排版

必須保留：

- `kfxlib/styles/` bundled CSS；
- `style-reset.css`；
- `book-style.css` 的 import 關係；
- 一般文字與 fixed-layout 使用不同 CSS；
- `fixed-layout-jp.css`；
- 只輸出實際被 XHTML 引用的 CSS；
- selector pruning 與 default property 清理；
- vertical writing / `vertical-rl` / `vrtl`；
- TCY span normalization；
- fit-image wrapper normalization。

`kfxlib/yj_to_epub_properties.py` 是 overlay 核心檔案之一，不可直接用上游同名檔取代。

### 5.4 頁面與 EPUB 後處理

必須保留：

- 頁碼 marker 清理；
- simplified `<!DOCTYPE html>`；
- XHTML、OPF、navigation 的路徑重命名；
- fixed-layout SVG normalization；
- comic spread 的預設：

```python
if self.is_comic and has_page_spread and not itemref_properties:
    itemref_properties.add("rendition:page-spread-center")
```

輸出 XHTML 時要注意 XML declaration 只出現一次。若 `format_xhtml_document_header()` 會手動加入 declaration，`etree.tostring()` 必須使用：

```python
xml_declaration=False
```

## 6. 兩個容易衝突的檔案

### `yj_to_epub_resources.py`

上游可能把：

```python
locate_raw_media(location, format)
```

改成與舊版不同的簽名，而本地曾使用：

```python
locate_raw_media(location, report_missing=True)
```

整合時應保留相容性，辨識 boolean 第二個 positional argument，並同時支援：

```python
locate_raw_media(location)
locate_raw_media(location, False)
locate_raw_media(location, report_missing=False)
locate_raw_media(location, resource_format)
```

同時保留 dictionary `$285` 的 warning 行為與一般資源的 error 行為。

### `yj_to_epub_content.py`

上游可能更新 dictionary 或 content API；本地則需要保留 `image_role` 傳遞與 layout 判定。可保留下列參數：

```python
process_story(..., image_role="page")
add_content(..., image_role="page")
process_content_list(..., image_role="page")
```

但正文 `$271` 圖片不應為了表面上完整傳遞 role，就在 resource stage 重複命名；正文命名仍交給最後 DOM 後處理。

## 7. pypdf 整合規則

若上游 bundled `pypdf` 更新，不要只拷貝幾個新增檔案。先確認版本，再以整體相容版本同步相關模組。

目前專案使用：

```text
pypdf 6.16.1
```

依賴固定為：

```text
pypdf==6.16.1
```

應確認：

- `PdfReader`、`PdfWriter`；
- `pypdf.actions`；
- `generic._image_xobject`；
- `generic._appearance_stream`；
- `_font`；
- `_codecs.core_font_metrics`；
- 現有 PDF cache 與 page box API。

舊的 private module 暫時不要任意刪除；先檢查 import graph 與外部相容性，再決定是否移除。

## 8. 驗證清單

### 8.1 靜態檢查

```bash
python -m compileall -q kfxlib convert_kfx2epub.py
git diff --check
```

另應測試：

- 所有 root Python 檔案 AST parse；
- `kfxlib` import；
- bundled pypdf import；
- 新增 API 的最小呼叫。

### 8.2 功能 smoke test

至少測試：

- 一般文字書；
- fixed-layout / 漫畫書；
- PDF-backed 書籍；
- dictionary 書籍（若有測試檔）；
- 單檔輸入；
- 資料夾批次輸入；
- `.kfx` 與 `.kfx-zip`；
- 非法檔名字元；
- 已存在輸出檔時的 `dupnameN`；
- `force_cover`。

每本 EPUB 至少檢查：

```text
ZIP testzip() 正常
item/standard.opf 存在
item/navigation-documents.xhtml 存在
封面與 manifest cover-image 正確
所有 href/src 指向存在的檔案
CSS 檔案與 XHTML link 一致
圖片符合 cover / i-### / p-### 規則
XHTML XML declaration 只有一行
```

### 8.3 與舊輸出的比較

可以比較 `archived_epub` 與新輸出，但只能將它當作回歸測試，不可當作上游差異的唯一依據。

比較時分成三層：

1. ZIP entry 名稱集合與順序；
2. 未壓縮內容的 SHA-256；
3. 語意正規化後的 XML/HTML 內容。

以下差異通常不是書籍內容變更：

- ZIP entry timestamp；
- `dcterms:modified`；
- XML declaration 的編碼寫法（但重複 declaration 應視為 bug 並修正）；
- EPUB metadata 產生時間。

若要做可重複比較，應先：

- 移除或統一 XML declaration；
- 將 `dcterms:modified` 正規化；
- 分開報告 ZIP timestamp；
- 再比較 XML 結構與文字內容。

## 9. Windows portable EXE

建置時必須確認：

```text
convert_kfx2epub.exe
kfxlib/
kfxlib/styles/
```

目前 spec 的原則是：

```python
excludes=["kfxlib"]
```

也就是 EXE 與完整外部 `kfxlib/` 一起發佈。重新建置後要檢查 portable 目錄中的 `version.py`、新版 pypdf 檔案與 `styles/` 是否都是目前版本，不要只相信既有 `dist/` 產物。

## 10. 建議的下次整合順序

1. 記錄新上游版本與目前 HEAD。
2. 以 `c7e82b0` 或最近一次確認過的共同基準做三方 diff。
3. 列出上游新增、上游修改、本地修改及雙方衝突檔案。
4. 先整合 KFX/KPF、dictionary、PDF、version 等上游核心。
5. 手動將 `epub_output.py`、`yj_to_epub_properties.py`、`yj_to_epub_content.py`、`yj_to_epub_resources.py` 的本地 overlay 套回。
6. 更新 bundled pypdf 時以整體版本處理，確認 import graph。
7. 檢查 `force_cover`、圖片命名、CSS、vertical writing、comic spread 與 XML header。
8. 執行 compile、import、smoke test、批次 KFX 回歸測試。
9. 以隔離輸出目錄比較 EPUB，不覆蓋原始測試輸出。
10. 檢查 `git diff --check`、`git status` 與 portable build 內容。
11. 確認 `.gitignore` 沒有把必要的 source 或 `styles/` 排除，再建立 commit。

## 11. 本次整合紀錄

本次上游來源：

```text
KFX Inputv260827
```

本次共同基準：

```text
c7e82b0
```

本次上游核心重點：

- KFX/KPF 新格式與 SQLite schema；
- dictionary DRM 與 inflection rules；
- PDF reader cache、頁面尺寸與 page box；
- KFX version 更新；
- bundled pypdf 6.16.1。

本次保留的本地獨家格式：

- `item/standard.opf` 與日本化 EPUB 路徑；
- `p-cover.xhtml`、`p-toc.xhtml`、`p-fmatter-*`、`p-colophon.xhtml`；
- `cover.*`、`i-*`、`p-*` 圖片命名；
- bundled CSS 與 fixed-layout CSS；
- vertical writing、頁碼清理與 comic spread；
- `force_cover` 的 `cover-image`；
- 單檔/批次 CLI、非法檔名字元全形化與 `dupnameN`；
- Windows portable EXE 與外部 `kfxlib/`。

本次比對 10 本 EPUB 後，entry 名稱、順序、CSS、圖片、字型與書籍語意內容均無實質變更；發現的重複 XML declaration 已在 `kfxlib/epub_output.py` 修正。
