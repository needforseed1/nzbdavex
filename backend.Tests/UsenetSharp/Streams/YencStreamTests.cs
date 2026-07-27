using System.Text;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.UsenetSharp.Streams;

public class YencStreamTests
{
    [Fact]
    public async Task DecodesFirstDataLineLargerThanDefaultBuffer()
    {
        const int lineLength = 700;
        var encodedLine = new string('k', lineLength); // 'A' + yEnc's 42-byte offset
        var article = Encoding.Latin1.GetBytes(
            $"=ybegin line={lineLength} size={lineLength} name=test.bin\r\n" +
            encodedLine +
            $"\r\n=yend size={lineLength}\r\n");
        await using var stream = new YencStream(new MemoryStream(article));
        var decoded = new byte[lineLength];

        var read = await stream.ReadAsync(decoded);

        Assert.Equal(lineLength, read);
        Assert.All(decoded, value => Assert.Equal((byte)'A', value));
    }

    [Fact]
    public async Task ReadsCompleteBodyToEnd()
    {
        await using var stream = new YencStream(new MemoryStream(Article(dataLines: 3)));
        var decoded = new MemoryStream();

        await stream.CopyToAsync(decoded);

        Assert.Equal(3 * LineLength, decoded.Length);
    }

    [Fact]
    public async Task ThrowsWhenBodyEndsShortWithoutTrailer()
    {
        // The socket dropped mid-body: data lines stop with no =yend.
        var truncated = Article(dataLines: 3, declaredLines: 5, includeTrailer: false);
        await using var stream = new YencStream(new MemoryStream(truncated));

        await Assert.ThrowsAsync<InvalidDataException>(() => stream.CopyToAsync(new MemoryStream()));
    }

    [Fact]
    public async Task AcceptsACompleteBodyThatNeverSendsATrailer()
    {
        // Seen in the wild on ngPost-posted parts: every declared byte arrives
        // but no =yend follows. Rejecting these sends a complete segment down
        // the retry path to be zero-filled, which corrupts the stream outright.
        var noTrailer = Article(dataLines: 4, includeTrailer: false);
        await using var stream = new YencStream(new MemoryStream(noTrailer));
        var decoded = new MemoryStream();

        await stream.CopyToAsync(decoded);

        Assert.Equal(4 * LineLength, decoded.Length);
    }

    [Fact]
    public async Task ThrowsWhenPartDecodesShorterThanDeclaredSize()
    {
        // A well-formed trailer still cannot rescue a part that lost data.
        var short_ = Article(dataLines: 3, declaredLines: 5);
        await using var stream = new YencStream(new MemoryStream(short_));

        await Assert.ThrowsAsync<InvalidDataException>(() => stream.CopyToAsync(new MemoryStream()));
    }

    [Fact]
    public async Task ReadsEmptyArticleWithoutThrowing()
    {
        var empty = Encoding.Latin1.GetBytes(
            "=ybegin line=128 size=0 name=test.bin\r\n=yend size=0\r\n");
        await using var stream = new YencStream(new MemoryStream(empty));
        var decoded = new MemoryStream();

        await stream.CopyToAsync(decoded);

        Assert.Equal(0, decoded.Length);
    }

    private const int LineLength = 128;

    private static byte[] Article(int dataLines, int? declaredLines = null, bool includeTrailer = true)
    {
        var declaredSize = (declaredLines ?? dataLines) * LineLength;
        var body = new StringBuilder();
        body.Append($"=ybegin line={LineLength} size={declaredSize} name=test.bin\r\n");
        for (var i = 0; i < dataLines; i++)
            body.Append(new string('k', LineLength)).Append("\r\n"); // 'A' + yEnc's 42-byte offset
        if (includeTrailer) body.Append($"=yend size={declaredSize}\r\n");
        return Encoding.Latin1.GetBytes(body.ToString());
    }
}
