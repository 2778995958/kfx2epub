import os
import sys
import argparse
from pathlib import Path
# 引入 YJ_Book 類別
from kfxlib import YJ_Book
# 核心：直接引入 KFX_EPUB 類別，用來在轉檔前攔截 OPF 內部的書名與作者
from kfxlib.yj_to_epub import KFX_EPUB

def sanitize_filename(name):
    """
    將電腦作業系統不允許作為檔名的禁止字元，自動轉換為安全對應的全型字元。
    """
    table = str.maketrans({
        '\\': '＼',
        '/': '／',
        ':': '：',
        '*': '＊',
        '?': '？',
        '"': '＂',
        '<': '＜',
        '>': '＞',
        '|': '｜'
    })
    return name.translate(table)

def batch_convert_kfx_to_epub(input_dir, output_dir):
    """
    批量掃描輸入目錄中的 KFX/KFX-ZIP，轉換為 EPUB，
    並自動以 '[作者] 書名.epub' 的全型安全格式命名輸出。
    """
    input_path = Path(input_dir).resolve()
    output_path = Path(output_dir).resolve()

    print(f"📂 輸入目錄：{input_path}")
    print(f"📂 輸出目錄：{output_path}")

    if not input_path.exists():
        print(f"❌ 錯誤：找不到輸入目錄 '{input_path}'")
        return

    # 建立輸出目錄
    output_path.mkdir(parents=True, exist_ok=True)

    # 搜尋目標檔案
    kfx_files = list(input_path.glob("*.kfx-zip")) + list(input_path.glob("*.kfx"))

    if not kfx_files:
        print("💡 提示：在輸入目錄中沒有找到任何 .kfx-zip 或 .kfx 檔案。")
        return

    print(f"🔍 找到 {len(kfx_files)} 個檔案，開始進行命名轉換...\n")
    print("-" * 50)

    success_count = 0
    fail_count = 0

    for file in kfx_files:
        print(f"⏳ 正在解析原始檔案: {file.name}")
        
        try:
            # 1. 建立書籍物件並進行解碼
            book = YJ_Book(str(file))
            book.decode_book(retain_yj_locals=True)
            
            # 2. 實例化 KFX_EPUB 轉換器（此步驟會自動載入 OPF 元數據）
            ke = KFX_EPUB(book)
            
            # 3. 從 OPF 資訊中提取書名與作者 (作者欄位通常為 list，我們取第一位)
            title = ke.title if ke.title else "Unknown"
            author = ke.authors[0] if ke.authors else "Unknown"
            
            # 4. 組合成要求的 "[作者] 書名" 格式
            raw_filename = f"[{author}] {title}"
            
            # 5. 通過全型字元轉換過濾器，確保檔名安全
            clean_filename = sanitize_filename(raw_filename) + ".epub"
            output_file = output_path / clean_filename
            
            print(f"📝 預期輸出檔名: {clean_filename}")
            
            # 6. 呼叫底層引擎生成 EPUB 二進位數據並關閉資源
            epub_data = ke.decompile_to_epub()
            book.final_actions()
            
            # 7. 寫入實體檔案
            if epub_data:
                with open(output_file, 'wb') as f:
                    f.write(epub_data)
                print(f"   ✅ 成功生成乾淨命名的 EPUB！")
                success_count += 1
            else:
                print(f"   ❌ 轉換失敗：產生的 EPUB 數據為空。")
                fail_count += 1
                
        except Exception as e:
            print(f"   ❌ 發生錯誤：{e}")
            fail_count += 1
        print() # 換行

    print("-" * 50)
    print(f"🎉 批量處理完成！")
    print(f"📊 統計結果：成功 {success_count} 件，失敗 {fail_count} 件")


if __name__ == "__main__":
    script_dir = Path(__file__).parent.resolve()
    
    parser = argparse.ArgumentParser(
        description="使用 kfxlib 批量將 KFX / KFX-ZIP 轉換為 '[作者] 書名.epub' 的全型安全指令工具"
    )
    
    # 預設目錄更新為您要求的：output_kfx-zip
    parser.add_argument(
        "input_dir", 
        nargs="?", 
        default=str(script_dir / "output_kfx-zip"),
        help="輸入目錄路徑。預設為腳本同目錄下的 output_kfx-zip"
    )
    
    # 預設目錄更新為您要求的：output_epub
    parser.add_argument(
        "output_dir", 
        nargs="?", 
        default=str(script_dir / "output_epub"),
        help="輸出目錄路徑。預設為腳本同目錄下的 output_epub"
    )

    args = parser.parse_args()
    batch_convert_kfx_to_epub(args.input_dir, args.output_dir)