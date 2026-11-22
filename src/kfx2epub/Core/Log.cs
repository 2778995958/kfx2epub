namespace Kfx2Epub.Core;

public static class Log
{
    public static bool Verbose { get; set; }

    public static void Debug(string message)
    {
        if (Verbose) Console.Error.WriteLine(message);
    }

    public static void Info(string message)
    {
        if (Verbose) Console.Error.WriteLine(message);
    }

    public static void Warning(string message)
    {
        Console.Error.WriteLine("WARNING: " + message);
    }

    public static void Error(string message)
    {
        Console.Error.WriteLine("ERROR: " + message);
    }
}
