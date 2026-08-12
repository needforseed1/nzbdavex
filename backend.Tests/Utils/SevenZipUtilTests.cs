using NzbWebDAV.Utils;
using SharpCompress.Common;

namespace NzbWebDAV.Tests.Utils;

public class SevenZipUtilTests
{
    private const string EncryptedStoredArchive =
        "N3q8ryccAATL4M7skAAAAAAAAAAvAAAAAAAAABEU4T9xza/nHDzE791W7EHaLiZvYIURZ77C8ODDFjCZy2ZJzwg6oLca9ks++ylc5WoHc3i5BEhpObs0i43Pt0pPmdR4XopZglVafQdtSjpGfdk+WhoYeW9e8sTCUKeOIOQHT97okR2KRea/h74KTEGYGxHzUmTArLKqk3waBluGSVRL6fzFtffGduWX0m87gFLQUWQXBhABCYCAAAcLAQABJAbxBwESUw8Gqb/o2nVHQWPHpVibnkrlDHIKATOJZj8AAA==";

    [Fact]
    public void EncryptedStoredArchiveIsRecognizedAsUncompressed()
    {
        using var stream = new MemoryStream(Convert.FromBase64String(EncryptedStoredArchive));

        var entry = Assert.Single(SevenZipUtil.GetSevenZipEntries(stream, "secret"));

        Assert.Equal("version.txt", entry.PathWithinArchive);
        Assert.Equal(CompressionType.None, entry.CompressionType);
        Assert.True(entry.IsEncrypted);
    }
}
