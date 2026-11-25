namespace Kfx2Epub.Kfx;

// KFX 解析過程的具名例外：讓 CLI 能依類型對應契約結束碼，不必解析訊息文字。
public class KfxParseException : Exception
{
    public KfxParseException(string message) : base(message)
    {
    }

    public KfxParseException(string message, Exception inner) : base(message, inner)
    {
    }
}

// 受 DRM 保護。
public sealed class KfxDrmException : KfxParseException
{
    public KfxDrmException(string message) : base(message)
    {
    }
}

// 可辨識但不受支援，或不是 KFX 格式。
public sealed class KfxFormatException : KfxParseException
{
    public KfxFormatException(string message) : base(message)
    {
    }
}

// 是 KFX 格式，但資料不完整或損壞。
public sealed class KfxCorruptException : KfxParseException
{
    public KfxCorruptException(string message) : base(message)
    {
    }

    public KfxCorruptException(string message, Exception inner) : base(message, inner)
    {
    }
}
