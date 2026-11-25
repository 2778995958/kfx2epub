namespace Kfx2Epub.Cli;

// convert 期間攔截 Ctrl+C：先標記，等目前階段結束後再以 CANCELLED（40）收尾並清掉 .part。
internal static class Cancellation
{
    private static volatile bool _requested;
    private static ConsoleCancelEventHandler _handler;

    public static bool Requested => _requested;

    public static void Install()
    {
        _requested = false;
        _handler = (_, e) =>
        {
            e.Cancel = true;
            _requested = true;
        };
        Console.CancelKeyPress += _handler;
    }

    public static void Uninstall()
    {
        if (_handler is not null) Console.CancelKeyPress -= _handler;
        _handler = null;
    }

    public static void ThrowIfRequested()
    {
        if (_requested) throw new CliException("CANCELLED", "轉換已取消。");
    }
}
