using NzbWebDAV.Utils;
using SharpCompress.Common;

namespace NzbWebDAV.Tests.Queue;

public class RarUtilTests
{
    [Fact]
    public async Task InvalidRarHeaderIncludesStreamOffset()
    {
        // RAR4 signature followed by a complete base header whose type byte
        // (0x53 / decimal 83) is not a valid RAR header code.
        byte[] bytes =
        [
            0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00,
            0x00, 0x00, 0x53, 0x00, 0x00, 0x07, 0x00,
        ];
        await using var stream = new MemoryStream(bytes);

        var error = await Assert.ThrowsAsync<InvalidFormatException>(() =>
            RarUtil.GetRarHeadersAsync(stream, password: null, CancellationToken.None));

        Assert.Contains("Unknown Rar Header: 83", error.Message);
        Assert.Contains("byte offset", error.Message);
    }
}
