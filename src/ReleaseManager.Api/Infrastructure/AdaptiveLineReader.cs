using System.Runtime.CompilerServices;
using System.Text;

namespace ReleaseManager.Api.Infrastructure;

public static class AdaptiveLineReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Encoding Gbk;

    static AdaptiveLineReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    public static async IAsyncEnumerable<string> ReadLinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var readBuffer = new byte[8192];
        using var lineBuffer = new MemoryStream();
        while (true)
        {
            var count = await stream.ReadAsync(readBuffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            for (var i = 0; i < count; i++)
            {
                var value = readBuffer[i];
                if (value == (byte)'\n')
                {
                    yield return DecodeLine(lineBuffer);
                    lineBuffer.SetLength(0);
                }
                else
                {
                    lineBuffer.WriteByte(value);
                }
            }
        }
        if (lineBuffer.Length > 0) yield return DecodeLine(lineBuffer);
    }

    private static string DecodeLine(MemoryStream buffer)
    {
        var bytes = buffer.ToArray();
        var length = bytes.Length;
        if (length > 0 && bytes[length - 1] == (byte)'\r') length--;
        if (length == 0) return string.Empty;
        try
        {
            return StrictUtf8.GetString(bytes, 0, length).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            return Gbk.GetString(bytes, 0, length);
        }
    }
}
