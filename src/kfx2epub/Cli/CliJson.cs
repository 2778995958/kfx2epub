using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Kfx2Epub.Cli;

// JSON 一律 UTF-8 無 BOM、單行、結尾 LF；直接寫 stdout 位元組，避開 Console 的 CRLF。
public static class CliJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static byte[] ToUtf8(object value)
        => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static string ToLine(object value)
        => JsonSerializer.Serialize(value, Options);

    public static void WriteLine(object value)
    {
        var bytes = ToUtf8(value);
        var stdout = Console.OpenStandardOutput();
        stdout.Write(bytes, 0, bytes.Length);
        stdout.WriteByte((byte)'\n');
        stdout.Flush();
    }

    // 純文字輸出（例如 --api-version）也固定 UTF-8 無 BOM、LF。
    public static void WriteTextLine(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        var stdout = Console.OpenStandardOutput();
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
    }

    public static Dictionary<string, object> Error(string command, CliException error, bool includeDetail)
    {
        return new Dictionary<string, object>
        {
            ["api"] = CliContract.Api,
            ["ok"] = false,
            ["command"] = command,
            ["code"] = error.Code,
            ["exit"] = error.ExitCode,
            ["message"] = error.Message,
            ["detail"] = includeDetail ? error.Detail : null
        };
    }
}

public static class CliContract
{
    public const int Api = 1;
}
