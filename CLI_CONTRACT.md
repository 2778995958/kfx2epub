# kfx2epub CLI 契約（v1）

- 狀態：**已實作（C# 與 Python）；`progress_ndjson` 目前為 `false`**
- 適用對象：`src/kfx2epub`（C# 版）與 `convert_kfx2epub.py`（Python 版）
- 目的：讓**任何語言的宿主程式**（閱讀器、轉檔服務、腳本）都能安全、可預期地呼叫本工具，不需要閱讀原始碼，也不需要解析人類訊息。
- 一句話：**這份文件規範的是「對外承諾」，不是「目前剛好如此」。**

> 本文使用 RFC 2119 的用語：**必須（MUST）**、**應（SHOULD）**、**可（MAY）**、**不得（MUST NOT）**。

---

## 1. 範圍

### 1.1 納入

- `probe`：只讀取書本 metadata，不產生 EPUB。
- `convert`：單本書 → 單一 EPUB，輸出到呼叫端指定的路徑。
- `capabilities`、`version`、`--api-version`：能力與版本查詢。
- 既有批次模式的相容性。

### 1.2 不納入

- 記憶體內的 ABI（DLL／C ABI）。
- DRM 移除（本工具只偵測並拒絕）。
- PDF-backed 書籍的頁面光柵化（C# 版目前是原樣嵌入 PDF 並加 `#page=`）。
- Calibre plugin 介面。

---

## 2. 相容性承諾

1. 既有的呼叫方式 **必須** 繼續可用：
   ```
   kfx2epub [-w N] [輸入資料夾或檔案] [輸出資料夾]
   ```
   行為（`[作者] 書名.epub` 命名、非法字元全形化、`dupnameN`、預設讀取執行檔旁的 `archived_kfx`）**不得**改變。
2. 若第一個參數是保留字（`probe`、`convert`、`capabilities`、`version`），**必須** 視為子命令。若使用者真的有一個叫 `convert` 的檔案或資料夾，**應** 以 `--` 分隔或 `.\convert`（Windows；Unix 為 `./convert`）表示路徑。
   - 由檔案總管拖放時，Windows 傳入的是**絕對路徑**（例如 `"D:\books\convert"`），不會等於保留字，因此拖放行為不受影響。
3. `api` 整數一旦發佈 **不得** 變更語意；只能新增欄位或新增命令。破壞性變更 **必須** 遞增 `api`。

---

## 3. 全域規則

### 3.1 編碼與串流

| 規則 | 內容 |
|---|---|
| 文字編碼 | 一律 UTF-8，**不得** 輸出 BOM |
| `stdout` | 機器可讀輸出（JSON／EPUB bytes）；**不得** 混入人類訊息 |
| `stderr` | 人類訊息、警告、錯誤；宿主 **可** 忽略 |
| 換行 | JSON 每行一個物件，結尾 `\n`（LF）；**不得** 輸出 CRLF |
| 二進位模式 | `--out -` 時，`stdout` **必須** 為純二進位，不得有額外位元組 |

以上規則適用於子命令與 `--json` 模式。舊批次的預設人類訊息輸出維持原樣，**不得** 為了符合本節而改變。

實作提示：Windows 的 `Console.Out` 預設使用 CRLF，JSON 模式 **必須** 明確設定 LF（例如 `Console.Out.NewLine = "\n"`）。

### 3.2 結束碼（固定，不得重排）

| 碼 | 意義 | 宿主的建議處理 |
|---|---|---|
| `0` | 成功 | 使用輸出 |
| `1` | 未預期的內部錯誤 | 記錄 stderr，回報 bug |
| `2` | 命令列用法錯誤 | 視為呼叫端 bug |
| `10` | 輸入無效（找不到、不是 KFX、空檔） | 提示使用者檔案有問題 |
| `11` | 格式或版本不支援 | 提示更新工具 |
| `12` | 受 DRM 保護 | 提示「此書有 DRM」 |
| `13` | 檔案損壞 | 提示「檔案損壞」 |
| `20` | I/O 錯誤（權限、磁碟滿、輸出路徑已存在） | 顯示系統錯誤 |
| `40` | 已取消（Ctrl+C、終端關閉） | 靜默處理 |

規則：

- 同一失敗 **必須** 對應固定結束碼；宿主 **不得** 需要解析訊息文字才能判斷原因。
- 有 `--json` 時，JSON 的 `exit` 欄位 **必須** 與實際結束碼一致。
- 被 **強制終止**（`TerminateProcess`／`Process.Kill`）時不會有任何結束碼；宿主只能依自己的逾時／取消狀態判斷，不得依賴本工具的輸出。

舊批次模式為維持既有行為，結束碼固定為 `0`（全部成功）、`1`（任一失敗）、`2`（用法錯誤），**不得** 套用 `10`–`40`；需要細分失敗原因的宿主 **必須** 改用子命令。

### 3.3 互動禁令

- **不得** 讀取 `stdin`，除非輸入參數為 `-`（舊批次的 Explorer 暫停除外，見下）。
- **不得** 等待按鍵、不得顯示視窗、不得開啟瀏覽器。
- 「從檔案總管啟動後等 Enter」**僅可** 在舊批次模式下保留，且 **必須** 同時滿足：未指定 `--no-pause`、`stdin` 與 `stdout` 皆未重導向、主控台只有本行程。雙擊（無參數）與拖放（Explorer 傳入絕對路徑）都屬此情境，既有行為 **不得** 改變。
- 子命令（`probe`、`convert`、`capabilities`、`version`）與 `--out -` **不得** 等待任何輸入，也 **不得** 輸出暫停提示。
- 轉換期間 **不得** 因缺少 `stdin`／主控台而改變行為；宿主關閉視窗時應以 `Kill` 結束行程（見 3.5）。

### 3.4 路徑

- 接受絕對與相對路徑；相對路徑以**目前工作目錄**解析。
- **必須** 支援空白、Unicode、以及超過 `MAX_PATH` 的路徑（內部可加 `\\?\`）。
- 輸出目錄不存在時 **應** 自動建立（含多層）。
- 輸出路徑 **不得** 被工具自行改名（見 5.5）。

### 3.5 原子性與覆寫

本節的原子性要求適用於 `convert` 子命令。舊批次為維持既有行為，允許直接寫入目標檔，但 **不得** 因此改變既有的命名與 `dupnameN` 規則。

- `convert` 的轉換 **必須** 先寫入同目錄的暫存檔，成功後才更名為目標檔：
  ```
  <目標目錄>\.<檔名>.kfx2epub-<8 位隨機>.part
  ```
- 正常失敗或收到 Ctrl+C 時 **必須** 刪除暫存檔；**不得** 留下半成品。
- 被 **強制終止**（`TerminateProcess`／`Process.Kill`）時無法執行清理，可能留下 `.part`；此時 **由呼叫端負責**清除。契約測試不得要求此情境「無殘留」。
- 目標檔已存在且未指定 `--force` 時 **必須** 以 `20` / `OUTPUT_EXISTS` 失敗。
- `--force` 時 **必須** 以原子更名覆寫。

### 3.6 環境

- **不得** 存取網路、**不得** 寫入登錄檔、**不得** 啟動外部程式。
- 暫存檔 **必須** 使用系統暫存目錄，且以 `kfx2epub-` 為前綴。
- §3.5 的 `.part` 檔是目標檔的一部分，**不屬於** 本節的暫存檔，**必須** 放在目標檔同目錄；兩者不得混用。
- 環境變數 `KFX2EPUB_VERBOSE=1` **應** 等同 `--verbose`（既有行為）。

### 3.7 併發

- 多個行程同時處理**不同輸入** **必須** 安全（不得共用固定檔名的暫存檔）。
- 多個行程指向**同一輸出路徑**：允許競爭，但 **必須** 由原子更名保證「不會出現半成品」；落敗者 **應** 得到 `OUTPUT_EXISTS`。
- 上述同一輸出路徑的保證適用於 `convert`；舊批次沿用現行覆寫行為。

### 3.8 安全

- 輸入視為不受信任資料。
- 產生 EPUB 時，entry 名稱 **必須** 限制在輸出命名空間內，**必須** 拒絕或改寫含有 `..`、絕對路徑、磁碟代號的名稱。
- **不得** 將輸入內容當作指令、路徑或 URL 執行。

---

## 4. 命令總覽

```
kfx2epub --api-version
kfx2epub version [--json]
kfx2epub capabilities [--json]
kfx2epub probe <input> [--json]
kfx2epub convert <input> --out <path|-> [選項]
kfx2epub [--] [-w N] [輸入資料夾或檔案] [輸出資料夾]     ← 舊版批次，保留
```

全域選項（所有子命令可用）：

| 選項 | 說明 |
|---|---|
| `--json` | 機器可讀輸出（見第 6 節） |
| `--quiet` | 抑制 stderr 的人類訊息 |
| `--verbose` | 增加 stderr 詳細度 |
| `--no-pause` | 禁止任何等待輸入的行為 |
| `--log-file <path>` | 將人類訊息另寫入檔案（UTF-8） |

全域選項 **必須** 可放在子命令前後。`--` 之後的參數 **不得** 再被解讀為選項或子命令，一律視為路徑。

---

## 5. 命令定義

### 5.1 `--api-version`

- 輸出單一整數（例如 `1`）加換行，結束碼 `0`。
- 不寫任何檔案。

### 5.2 `version`

```json
{"api":1,"tool":"kfx2epub","version":"1.0.0","commit":"a5fa5ee","runtime":".NET 8.0.20","platform":"win-x64"}
```

### 5.3 `capabilities`

**必須** 誠實回報實際能力，讓宿主決定要不要呼叫：

```json
{
  "api": 1,
  "input_formats": ["kfx", "kfx-zip"],
  "output_formats": ["epub3", "epub2"],
  "probe": true,
  "progress_ndjson": true,
  "reproducible": true,
  "pdf_backed": "pass-through",
  "dictionary": false,
  "drm": "reject",
  "max_input_bytes": 2147483591,
  "platform": "win-x64"
}
```

範例假設已支援 NDJSON 進度；尚未實作時 `progress_ndjson` **必須** 為 `false`，其餘欄位依實際能力填寫。

`pdf_backed` 的值：

| 值 | 意義 |
|---|---|
| `rasterize` | 會把 PDF 頁面轉成圖片（Python 版） |
| `pass-through` | 原樣嵌入 PDF 並加 `#page=`（C# 版現況） |
| `unsupported` | 直接拒絕 |

`capabilities` **必須** 反映實際能力，不得虛報：

- 尚未實作 `--progress=ndjson` 的版本 **必須** 回 `progress_ndjson: false`；宿主據此 **不得** 使用該選項。第 9 節的 #16–#18 僅在值為 `true` 時適用。
- `dictionary` 只有在實際支援且驗證過時才可為 `true`。
- `max_input_bytes` 是執行環境的實際上限（例如 `byte[]` 的 2 GB 等級上限），不是建議值；沒有額外業務限制時 **應** 回報該平台上限。
- `input_formats`、`output_formats` 只列實際可用的值；未驗證的格式 **不得** 列出。
- `pdf_backed` 依實作回報：C# 版為 `pass-through`，Python 版為 `rasterize`。

### 5.4 `probe <input>`

只讀取 metadata，**不得** 產生任何檔案（含暫存檔）。

- 成功時結束碼 `0`，輸出 JSON。
- **DRM 不視為 probe 失敗**：`drm: true` 仍回 `0`，讓宿主能顯示書名後再決定。
- 無法辨識格式 → `10`；格式可辨識但損壞 → `13`。
- 效能目標：≤ 500 ms（不得為了 probe 產生 EPUB）。參考實測：2.5 MB、15.1 MB、98.9 MB 的 KFX-ZIP 約為 53、139、302 ms。

```json
{
  "api": 1,
  "ok": true,
  "command": "probe",
  "input": "D:\\books\\sample.kfx-zip",
  "format": "kfx-zip",
  "kfx_version": "2",
  "title": "書名",
  "authors": ["作者"],
  "language": "ja",
  "page_count": 303,
  "drm": false,
  "fixed_layout": true,
  "pdf_backed": false,
  "elapsed_ms": 120
}
```

- 未知欄位 **必須** 輸出 `null`，**不得** 省略（方便宿主用固定 schema）。
- `authors` 為陣列，未知時為空陣列。
- `kfx_version` 是容器標頭版本，實務上為 `1` 或 `2`，不是語意化版本字串；未知時為 `null`。
- `page_count` 只在能可靠判定時給整數（固定版面書可用 reading order 的 location 數）；reflowable 書無法可靠判定時 **必須** 為 `null`，**不得** 估算。
- `probe` **不得** 建構完整 EPUB 流程（例如 C# 的 `KfxEpub` 建構子）或呼叫 `DecompileToEpub()`；只讀取 metadata fragment，且 **不得** 產生任何檔案。
- `format` 依實際容器判定，例如 `kfx`、`kfx-zip`；無法辨識 → `10`／`INPUT_NOT_KFX`，可辨識但損壞 → `13`／`CORRUPT_INPUT`。

### 5.5 `convert <input> --out <path|->`

| 選項 | 必填 | 說明 |
|---|---|---|
| `--out <path>` | 是 | 完整輸出檔路徑；`-` 表示寫到 stdout |
| `--force` | 否 | 允許覆寫既有檔 |
| `--format epub3\|epub2` | 否 | 預設 `epub3` |
| `--force-cover` | 否 | 對應既有 `forceCover` |
| `--reproducible` | 否 | 可重現輸出（見第 7 節） |
| `--progress=ndjson\|none` | 否 | 預設 `none` |
| `--workers N` | 否 | 單書轉換不使用；保留相容 |

> `--json` 與 `--progress=ndjson` 同時給時，以 `--progress=ndjson` 為準：stdout 是一串事件，最後一行 `done` 即結果，**不得**再額外輸出一個結果 JSON。

規則：

- `--out` 指向已存在的**目錄**時，**應** 依舊規則在該目錄內命名（`[作者] 書名.epub`、衝突時 `dupnameN`）。這讓舊宿主也能改用 `convert`。
- `--out -` 時：stdout **只有** EPUB bytes；所有人類訊息與進度走 stderr。
- `--out -` 與 `--json` 同時指定時，`--json` **必須** 被忽略：stdout 仍只有 EPUB bytes，結果不另輸出 JSON。
- 成功後 **應** 在結果 JSON 提供 `sha256`，讓宿主做快取鍵。
- `--progress=ndjson` 時，**必須** 依 6.3 輸出 `start` → `progress`… → `done`；`percent` 由工具算好，宿主不得自行推導。
- 若 `capabilities.progress_ndjson` 為 `false`，收到 `--progress=ndjson` **必須** 以 `2`／`USAGE_ERROR` 拒絕，**不得** 靜默忽略；同時給了 `--json` 時，**應** 以 JSON 錯誤 envelope 回報 `USAGE_ERROR`。
- `--reproducible`：若預設輸出已可重現（例如以來源 KFX-ZIP 的最晚 entry 時間為準），此選項 **可** 為 no-op，但 **必須** 保證同一輸入與版本執行兩次 SHA-256 相同。實作 **必須** 消除所有隨機與當下時間來源，例如缺少 ASIN／BookId 時的隨機 UUID、非 ZIP 輸入的時間 fallback。

```json
{
  "api": 1,
  "ok": true,
  "command": "convert",
  "input": "D:\\books\\sample.kfx-zip",
  "output": "C:\\cache\\a1b2c3d4.epub",
  "bytes": 8765432,
  "sha256": "…",
  "elapsed_ms": 1180,
  "book": { "title": "書名", "authors": ["作者"], "language": "ja", "page_count": 303, "drm": false, "pdf_backed": false }
}
```

失敗範例：

```json
{"api":1,"ok":false,"command":"convert","code":"DRM_PROTECTED","exit":12,"message":"此書受 DRM 保護，無法轉換。","detail":null}
```

### 5.6 舊版批次（保留）

```
kfx2epub [--] [-w N] [輸入] [輸出]
```

- 行為（掃描範圍、`[作者] 書名.epub` 命名、非法字元全形化、`dupnameN`、pause、stdout 人類訊息）與現行版本一致，**不得** 改變。
- **可** 支援 `--no-pause`；`--json` 為選用（輸出每本書的結果事件，最後一個 `done` 事件帶總結）。
- 結束碼固定為 `0`（全部成功）、`1`（任一失敗）、`2`（用法錯誤），**不得** 改用第 6.2 節的細分碼；需要細分原因的宿主 **必須** 改用 `convert`。

---

## 6. JSON 格式

### 6.1 共用 envelope

| 欄位 | 型別 | 說明 |
|---|---|---|
| `api` | int | 固定 `1` |
| `ok` | bool | 成功與否 |
| `command` | string | `probe` / `convert` / `version` / `capabilities` |
| `code` | string | 僅失敗時；見 6.2 |
| `exit` | int | 僅失敗時；與行程結束碼相同 |
| `message` | string | 僅失敗時；人類可讀（UTF-8） |
| `detail` | string\|null | 僅失敗時；除錯用，可為 `null` |

### 6.2 錯誤碼字串

| `code` | `exit` |
|---|---|
| `INPUT_NOT_FOUND` | 10 |
| `INPUT_NOT_KFX` | 10 |
| `INPUT_EMPTY` | 10 |
| `UNSUPPORTED_FORMAT` | 11 |
| `UNSUPPORTED_VERSION` | 11 |
| `DRM_PROTECTED` | 12 |
| `CORRUPT_INPUT` | 13 |
| `OUTPUT_EXISTS` | 20 |
| `IO_ERROR` | 20 |
| `CANCELLED` | 40 |
| `INTERNAL_ERROR` | 1 |
| `USAGE_ERROR` | 2 |

新增錯誤碼 **必須** 使用新字串；既有字串的語意 **不得** 改變。

### 6.3 NDJSON 事件（`--progress=ndjson`）

```json
{"event":"start","command":"convert","input":"...","total":303}
{"event":"progress","phase":"decode","done":42,"total":303,"percent":14}
{"event":"warning","code":"MISSING_RESOURCE","message":"..."}
{"event":"done","ok":true,"output":"...","sha256":"...","elapsed_ms":1180}
```

規則：

- `done`、`total`、`percent` 皆為整數；`total` 未知時可為 `null`，此時 `percent` 也為 `null`（宿主顯示不確定進度）。
- `percent` 為 `0–100`，等於 `done * 100 / total` 向下取整；**由工具提供**，宿主不必自行換算。
- 同一行程內：`done` **必須** 單調不減；`total` 一旦非 `null` **不得** 再改變數值。
- 事件頻率：**每 100–500 ms 一次、每秒不超過 20 次**；不得為每個 entry 各輸出一行。
- `start` **應** 在 500 ms 內輸出；若此時已知 `total`，**應** 一併帶上。
- `phase` 目前允許：`decode`、`render`、`package`；新增值不得移除舊值。
- `done` **必須** 是最後一行，且**一定**會輸出（成功或失敗）；失敗時 `ok:false` 並帶 `code`／`exit`。
- 被強制終止（`Kill`）時不會有 `done`，也不會保證清理；宿主須自行處理（見 3.5）。
- 本節僅適用於 `capabilities.progress_ndjson` 為 `true` 的實作；為 `false` 時宿主 **不得** 使用 `--progress=ndjson`，工具收到時 **必須** 以 `2`／`USAGE_ERROR` 拒絕。

---

## 7. 決定性與可重現

- 預設輸出**可**包含當下時間（ZIP entry timestamp、`dcterms:modified`），因此同輸入可能不同 bytes。若實作預設已可重現，**應** 在 `capabilities.reproducible` 回報 `true`，此時 `--reproducible` **可** 為 no-op。
- `--reproducible` 時 **必須**：
  - ZIP entry timestamp 固定；可採用來源 KFX-ZIP 的最晚 entry 時間，或固定值（例如 `1980-01-01 00:00:00`）；
  - `dcterms:modified` 與 ZIP entry 使用同一個固定來源值；缺少時用固定值；
  - 不產生隨機 ID 或隨機排序；缺少 ASIN／BookId 時 **不得** 使用隨機 UUID，**必須** 使用由輸入內容導出的固定值；
  - 非 ZIP 輸入或 ZIP 沒有有效時間時，**不得** 使用當下時間。
- 驗收：同一輸入 + 同一版本，執行兩次，EPUB 的 SHA-256 **必須**相同。

---

## 8. 效能與資源參考值

以下為參考機器（x64、Windows）的實測值，供宿主設計超時與記憶體上限：

| 項目 | 參考值 |
|---|---|
| 2 MB 書轉換 | 1.1–1.5 秒 |
| 100 MB 書轉換 | 3–5 秒 |
| 轉換峰值記憶體 | 約輸入大小的 7 倍（99 MB → 約 714 MB） |
| probe | 約 50–300 ms（2.5–99 MB KFX-ZIP 實測；目標 ≤ 500 ms） |

- 單本書轉換為單執行緒；平行度由呼叫端決定（多行程）。
- 宿主 **應** 設定超時（建議 180 秒；小書 60 秒即可）並在超時後終止行程；本工具不提供 `--timeout`。
- 取消方式：宿主直接 `Kill` 行程；本工具不提供取消命令，也不保證強制終止後的清理（見 3.5）。

---

## 9. 契約測試清單（必須自動化）

以「外部程式呼叫 CLI」的方式測試；同一套測試 **應** 同時跑 C# 版與 Python 版。

| # | 測試 | 斷言 |
|---|---|---|
| 1 | 未知選項或超過兩個位置參數 | 結束碼 `2`，stdout 不含 JSON |
| 2 | `--api-version` | 輸出整數、換行，結束碼 `0` |
| 3 | `probe` 合法書 | 結束碼 `0`、JSON 欄位齊全、**目錄無新增檔案** |
| 4 | `probe` DRM 書 | 結束碼 `0`、`drm:true` |
| 5 | `convert` 合法書 | 結束碼 `0`、輸出存在、可解壓、`mimetype` 為第一個 entry 且不壓縮 |
| 6 | 輸出路徑已存在 | 未加 `--force` → `20`／`OUTPUT_EXISTS`；加 `--force` → `0` |
| 7 | 轉換失敗（非強制終止） | 無 `.part` 殘留、無目標檔 |
| 8 | `--json` 純度 | stdout 恰為一行合法 JSON |
| 9 | `--out -` | stdout 開頭為 `PK\x03\x04`，結尾無多餘位元組 |
| 10 | `--reproducible` | 執行兩次 SHA-256 相同 |
| 11 | 無 stdin | 不得卡住（5 秒內結束） |
| 12 | 空白／Unicode／長路徑 | 成功 |
| 13 | 兩行程同輸出 | 一成功、一 `OUTPUT_EXISTS`，無半成品 |
| 14 | 舊版批次呼叫 | 行為與現行一致 |
| 15 | 損壞輸入 | `13`／`CORRUPT_INPUT` |
| 16 | `--progress=ndjson` | `done` 單調不減、`total` 不變、`percent` 正確、最後一行 `done` |
| 17 | 進度頻率 | 每秒事件數 ≤ 20，且不是每個 entry 一行 |
| 18 | `--json` + `--progress=ndjson` | 只有事件流，最後一行 `done`，無額外結果 JSON |
| 19 | 強制終止（Kill） | 可能殘留 `.part`、不得留下目標檔；宿主可自行清除 |
| 20 | Ctrl+C（`convert`） | 結束碼 `40`、無殘留（可手動驗證） |
| 21 | 無參數（雙擊等效） | 只掃 `archived_kfx` 最上層；結束碼 `0` 或 `1`；不得因新增子命令而改變 |
| 22 | 拖放等效：單一絕對路徑（含名為 `convert` 的資料夾） | 走舊批次，不得被視為子命令 |
| 23 | `--progress=ndjson` 但 `progress_ndjson:false` | 結束碼 `2`／`USAGE_ERROR`，不得靜默忽略 |

#16–#18 僅在 `capabilities.progress_ndjson` 為 `true` 時適用；為 `false` 的實作 **必須** 跳過並改驗證 #23。C# 與 Python 目前皆回報 `false`，因此實際以 #23 驗證。

---

## 10. 實作清單（C# 版）

| 檔案 | 改動 | 估時 |
|---|---|---|
| `Program.cs` | 子命令分派、參數解析、結束碼、`--no-pause` | 0.5 天 |
| `Cli/CommandLine.cs`（新增） | 解析器（不引入外部套件） | 0.5 天 |
| `Cli/ConvertCommand.cs`（新增） | 單書轉換、原子寫入、`--out -` | 0.5 天 |
| `Cli/ProbeCommand.cs`（新增） | 只讀 metadata；不得產生 EPUB | 0.5 天 |
| `Cli/JsonOutput.cs`（新增） | 固定 schema、UTF-8、單行 | 0.5 天 |
| `BatchConverter.cs` | 抽出 `ConvertOne` 供單書使用；回傳結構化結果 | 0.5 天 |
| 可重現 | ZIP timestamp、`dcterms:modified` | 0.5 天 |
| 測試 | 第 9 節 23 項 | 1.5 天 |
| **合計** | | **約 4–5 天** |

注意事項：

- `System.Text.Json` **應** 使用 source generator，避免日後 NativeAOT 受限。
- `probe` **不得** 建構 `KfxEpub` 或呼叫 `DecompileToEpub()`；只做到 `YjBook.DecodeBook()` 後直接讀 `$490` metadata，且 **不得** 產生檔案。
- `PauseIfLaunchedFromExplorer()` 的判斷 **不得** 影響子命令模式；`--out -`、`--json`、`--no-pause` 亦同。
- JSON 模式 **必須** 將 `Console.Out.NewLine` 設為 `"\n"`，否則 Windows 會輸出 CRLF。
- 舊批次的輸出、命名、`dupnameN` 與結束碼 **不得** 因新增子命令而改變。
- 若尚未實作 `--progress=ndjson`，`capabilities` **必須** 回 `progress_ndjson: false`，並在收到該選項時以 `2`／`USAGE_ERROR` 拒絕。

---

## 11. 附錄 A：宿主整合範例

### A.1 C#

以下範例假設 `capabilities.progress_ndjson` 為 `true`；若為 `false`，請省略 `--progress=ndjson`，改用 `--json` 並讀取最後的結果 JSON。

```csharp
static string ConvertToCachedEpub(string kfxPath, Action<int> onPercent)
{
    var psi = new ProcessStartInfo(KfxExe)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    psi.ArgumentList.Add("convert");
    psi.ArgumentList.Add(kfxPath);
    psi.ArgumentList.Add("--out");
    psi.ArgumentList.Add(target);
    psi.ArgumentList.Add("--progress=ndjson");   // 最後一行 done 即結果
    psi.ArgumentList.Add("--no-pause");

    var stderr = new StringBuilder();
    using var p = new Process { StartInfo = psi };
    p.OutputDataReceived += (s, e) =>
    {
        if (e.Data == null) return;
        // {"event":"progress",...,"percent":14}
        var i = e.Data.IndexOf("\"percent\":", StringComparison.Ordinal);
        if (i > 0 && int.TryParse(e.Data.Substring(i + 10).Split(',', '}')[0], out var pct))
            onPercent(pct);
    };
    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

    p.Start();
    p.BeginOutputReadLine();   // 兩個串流都要非同步讀，否則會 deadlock
    p.BeginErrorReadLine();
    p.WaitForExit();
    p.WaitForExit();           // 第二次確保非同步事件已送完

    if (p.ExitCode != 0) throw new KfxException(p.ExitCode, stderr.ToString());
    return target;
}
```

### A.2 Python

```python
def probe(path: str) -> dict:
    r = subprocess.run([KFX, "probe", path, "--json", "--no-pause"],
                       capture_output=True, timeout=180)
    if r.returncode != 0:
        raise KfxError(r.returncode, r.stdout.decode("utf-8"), r.stderr.decode("utf-8"))
    return json.loads(r.stdout)
```

### A.3 建議的快取鍵

```
cache/<sha256(來源絕對路徑 | 大小 | mtime | kfx2epub.exe 大小 | mtime)>.epub
```

也可直接使用 `convert --json` 回傳的 `sha256` 作為內容鍵。

---

## 12. 附錄 B：變更紀錄

| 日期 | 變更 |
|---|---|
| 2026-11-26 | 初版：定義 probe／convert／capabilities、結束碼、JSON、原子性、可重現、契約測試 |
| 2026-11-26 | 修訂：新增 `percent`、進度頻率與單調性、強制終止的清理責任、逾時建議 180 秒、測試增至 20 項 |
| 2026-11-26 | 修訂：pause 僅限舊批次（雙擊／拖放不變）、測試 #1 改為錯誤參數、新增拖放與無參數回歸測試、舊批次結束碼維持 0/1、`.part` 與暫存檔區分、`--reproducible` 允許 no-op、`kfx_version`／`page_count`／`max_input_bytes` 語意修正、`progress_ndjson:false` 的拒絕規則 |
| 2026-11-26 | 實作：C# 與 Python 皆完成子命令分派、`version`／`capabilities`／`probe`／`convert`、原子寫入、`--out -`、`sha256`、`--reproducible`、pause 收斂、JSON LF；`progress_ndjson` 誠實回報 `false` 並拒絕該選項。契約測試 C# 21 項、Python 20 項全數通過（#16–#18 依規則跳過） |
