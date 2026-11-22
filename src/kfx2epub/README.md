# kfx2epub（C# 重構版）

此目錄是 `convert_kfx2epub.py` 與 `kfxlib` 的 C# 重構版本，只保留 **KFX / KFX-ZIP → EPUB** 功能。

## 範圍

已移植：

- KFX `CONT` 容器與 `ENTY` 實體解析
- Ion binary 解析與 local / shared symbol table
- YJ 書籍結構、metadata、閱讀順序與導覽
- KFX 樣式屬性到 CSS 的轉換
- XHTML、OPF、EPUB3 navigation 產生
- 圖片資源處理與 `cover` / `i-###` / `p-###` 命名
- `[作者] 書名.epub` 命名、非法字元全形化、`dupnameN`
- 單檔與資料夾批次轉換

未移植（依需求排除）：

- PDF / CBZ / 圖片書輸出
- PDF-backed KFX 的 PDF 頁面轉圖
- Scribe notebook、dictionary 的完整處理
- Calibre plugin 介面

## 建置

需要 Windows 與 .NET 8 SDK（WPF 用於 JPEG XR / 圖片解碼）：

```powershell
dotnet build src/kfx2epub/kfx2epub.csproj
```

## 執行

```powershell
dotnet run --project src/kfx2epub/kfx2epub.csproj -- <輸入資料夾或檔案> [輸出資料夾]
```

未指定參數時，會讀取執行檔旁的 `archived_kfx`，並輸出到 `archived_epub`。

也可以先發佈後直接執行：

```powershell
dotnet publish src/kfx2epub/kfx2epub.csproj -c Release -r win-x64 --self-contained false
```

## 專案結構

```text
src/kfx2epub/
  Core/
    Ion.cs / IonBinary.cs / IonSymbolTable.cs
    XhtmlDom.cs
    ImageUtil.cs
    Serialization.cs
    Utilities.cs
    YjSymbolCatalog.cs
  Kfx/
    KfxContainer.cs
    YjBook.cs
    YjContainer.cs
  Epub/
    EpubOutput.cs
    KfxEpub*.cs
    PropertyData.cs
  Styles/
    *.css
```

## 備註

本重構以原 `convert_kfx2epub.py` 與 `kfxlib` 的行為與輸出契約為基準。原始 kfxlib 由 John Howell 開發，授權為 GPL v3。
