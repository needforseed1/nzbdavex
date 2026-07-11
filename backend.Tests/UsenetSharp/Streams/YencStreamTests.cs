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
}
