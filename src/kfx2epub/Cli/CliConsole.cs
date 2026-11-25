using System.Text;

namespace Kfx2Epub.Cli;

public sealed class GlobalOptions
{
    public bool Json;
    public bool Quiet;
    public bool Verbose;
    public bool NoPause;
    public string LogFile;
}

internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _first;
    private readonly TextWriter _second;

    public TeeTextWriter(TextWriter first, TextWriter second)
    {
        _first = first;
        _second = second;
    }

    public override Encoding Encoding => _first.Encoding;

    public override void Write(char value)
    {
        _first.Write(value);
        _second.Write(value);
    }

    public override void Write(string value)
    {
        _first.Write(value);
        _second.Write(value);
    }

    public override void WriteLine(string value)
    {
        _first.WriteLine(value);
        _second.WriteLine(value);
    }

    public override void Flush()
    {
        _first.Flush();
        _second.Flush();
    }
}

// 人類訊息一律走 stderr；--log-file 會再複寫一份，--quiet 則整批抑制。
public static class CliConsole
{
    private static TextWriter _logWriter;
    private static bool _quiet;

    public static void Configure(GlobalOptions options)
    {
        _quiet = options.Quiet;
        Core.Log.Verbose = options.Verbose ||
                           Environment.GetEnvironmentVariable("KFX2EPUB_VERBOSE") == "1";

        if (!string.IsNullOrEmpty(options.LogFile))
        {
            var full = Path.GetFullPath(options.LogFile);
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            _logWriter = new StreamWriter(
                new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)) { AutoFlush = true };
        }

        if (_quiet)
        {
            Console.SetError(TextWriter.Null);
        }
        else if (_logWriter is not null)
        {
            Console.SetError(new TeeTextWriter(Console.Error, _logWriter));
        }
    }

    public static void Info(string message)
    {
        if (_quiet) return;
        Console.Error.WriteLine(message);
    }

    public static void Error(string message)
    {
        if (_quiet) return;
        Console.Error.WriteLine(message);
    }

    public static void Flush()
    {
        _logWriter?.Flush();
    }

    public static void Shutdown()
    {
        _logWriter?.Flush();
        _logWriter?.Dispose();
        _logWriter = null;
    }
}
