namespace Kfx2Epub.Cli;

// CLI 契約的錯誤分類：每個失敗都固定對應一個字串碼與結束碼。
public sealed class CliException : Exception
{
    public string Code { get; }
    public int ExitCode { get; }
    public string Detail { get; }
    // 少數用法錯誤（例如不支援的 --progress）仍應以 JSON 回報給 --json 宿主。
    public bool EmitJson { get; }

    public CliException(string code, string message, string detail = null, Exception inner = null,
        bool emitJson = false)
        : base(message, inner)
    {
        Code = code;
        ExitCode = ExitCodeFor(code);
        Detail = detail;
        EmitJson = emitJson;
    }

    public static int ExitCodeFor(string code) => code switch
    {
        "INPUT_NOT_FOUND" or "INPUT_NOT_KFX" or "INPUT_EMPTY" => 10,
        "UNSUPPORTED_FORMAT" or "UNSUPPORTED_VERSION" => 11,
        "DRM_PROTECTED" => 12,
        "CORRUPT_INPUT" => 13,
        "OUTPUT_EXISTS" or "IO_ERROR" => 20,
        "CANCELLED" => 40,
        "USAGE_ERROR" => 2,
        _ => 1,
    };

    public static CliException Usage(string message)
        => new("USAGE_ERROR", message);

    // 把轉換／解析期間的例外轉成契約碼；只依例外類型與檔名判斷，不依賴訊息文字。
    public static CliException From(Exception ex)
    {
        return ex switch
        {
            CliException cli => cli,
            Kfx2Epub.Kfx.KfxDrmException => new("DRM_PROTECTED", "此書受 DRM 保護，無法轉換。", ex.ToString(), ex),
            Kfx2Epub.Kfx.KfxFormatException => new("INPUT_NOT_KFX", "輸入不是可辨識的 KFX 檔案。", ex.ToString(), ex),
            Kfx2Epub.Kfx.KfxCorruptException => new("CORRUPT_INPUT", "KFX 檔案不完整或已損壞。", ex.ToString(), ex),
            EndOfStreamException => new("CORRUPT_INPUT", "輸入檔案不完整或已損壞。", ex.ToString(), ex),
            InvalidDataException => new("CORRUPT_INPUT", "輸入檔案不完整或已損壞。", ex.ToString(), ex),
            IndexOutOfRangeException => new("CORRUPT_INPUT", "輸入檔案不完整或已損壞。", ex.ToString(), ex),
            FormatException => new("CORRUPT_INPUT", "輸入檔案格式不正確。", ex.ToString(), ex),
            FileNotFoundException => new("INPUT_NOT_FOUND", "找不到輸入檔案。", ex.ToString(), ex),
            DirectoryNotFoundException => new("INPUT_NOT_FOUND", "找不到輸入檔案。", ex.ToString(), ex),
            UnauthorizedAccessException => new("IO_ERROR", "沒有權限存取檔案。", ex.ToString(), ex),
            IOException => new("IO_ERROR", "檔案存取失敗。", ex.ToString(), ex),
            _ => new("INTERNAL_ERROR", "未預期的內部錯誤。", ex.ToString(), ex),
        };
    }
}
