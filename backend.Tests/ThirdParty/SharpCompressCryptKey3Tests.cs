using System;
using System.IO;
using System.Threading.Tasks;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.IO;
using SharpCompress.Readers;
using Xunit;

namespace NzbWebDAV.Tests.ThirdParty;

public class SharpCompressCryptKey3Tests
{
    // Three distinct volume prefixes from a header-encrypted RAR3 archive
    // generated with official RAR 6.12. Password: test.
    private static readonly string[] EncryptedVolumeFixtures =
    [
        "UmFyIRoHAFtnc5EBDQAAAAAAAABU1Xqw9SZeQsrTZGuUAmXym+uqSgluak7rj9+UE+XLOgiIdj4m8Dzx/9ONtHYKRBdKG3MdoZ6oktOAR1/+SAV977Mg4Mrotz77kva7XXUxSnt7SGPB2zji8xi5IRj6mLYXipIUiXtNl02+SRbQELAD0pAl0I+EaagnlYxXOHSZi3Koe+PCI9VR240UAUaae13J0h2HjBSGHL4V4UiSnjvyGzVIm3lTiQgKZzHy0U8uuW8LkErD3AX84zev1nwv9RgoWRWup3fBpFGBVuaKSG1/HLeRgUmqJzJIbzJzkJ8sSek0B0fyzaUyKhko1w==",
        "UmFyIRoHABhzc5EADQAAAAAAAABU1Xqw9SZeQqpOYRcz/CN49f2tzA7daVxJErclP5UrwjBJDMEmx+pbygjfYY23XMHhcp8FA1IVHZT4tJLXRUYPtddla89Zz+bvjBcwH7t2iL5CtaAFrHD6C1ybz/pbn9qHcD5JPY13a2npxgfOfb1J2HSC2VutDrqRhznpGNA/jCBuDN/0uPApwcvLqcHtx4nmMa7O+McDwrckoUorlARdAScfrclr/3Mz9nii4sVMrU0STiCi5cdkxt5zK6OqldqJLETbl30DbPM/HEnJjDdYXQBUlr9iWYWdb9wB7pWyUDRDpZzOWW7RwHG6XA==",
        "UmFyIRoHABhzc5EADQAAAAAAAABU1Xqw9SZeQpbvPyKVymfwP3vkpYYOYcN2cdHk/ceDiwKCfSyCLu5eiKRKxi2XnBntVchLX3YbHYo2FAgZumZl0c4o3ChJ3VhlPNrgeUlDqFC1c7qD1DDV5xuoStITQOuOROGU8tPgjHRlY3sH1MM+qA/0xkjmG9FcqpUkYOldyAF7o91D3QeCMuSyvNALQCJPyz1266RjXW6hz+1qhk2FLARkMQDnoVflC6u1Yz7oQfuby73qprm6ll/NiH7hDDJzJKKdZ01c7nZXgoDRpEoMayRVWUaWN//veGJ+bdrYrygjcMzQ5ptF185iqg==",
    ];

    [Fact]
    public void ExplicitCacheReusesRar3HeaderKeyAcrossVolumeFactoriesSync()
    {
        var cache = new Rar3DerivedKeyCache();

        foreach (var fixture in EncryptedVolumeFixtures)
        {
            using var stream = new MemoryStream(Convert.FromBase64String(fixture));
            var factory = new RarHeaderFactory(
                StreamingMode.Seekable,
                new ReaderOptions { Password = "test", LeaveStreamOpen = true },
                cache);
            var foundFile = false;

            foreach (var header in factory.ReadHeaders(stream))
            {
                if (header.HeaderType != HeaderType.File) continue;
                foundFile = true;
                break;
            }

            Assert.True(foundFile);
        }

        Assert.Equal(1, cache.DerivationCount);
    }

    [Fact]
    public async Task ExplicitCacheReusesRar3HeaderKeyAcrossVolumeFactories()
    {
        var cache = new Rar3DerivedKeyCache();

        foreach (var fixture in EncryptedVolumeFixtures)
        {
            await using var stream = new MemoryStream(Convert.FromBase64String(fixture));
            var factory = new RarHeaderFactory(
                StreamingMode.Seekable,
                new ReaderOptions { Password = "test", LeaveStreamOpen = true },
                cache);
            var foundFile = false;

            await foreach (var header in factory.ReadHeadersAsync(stream))
            {
                if (header.HeaderType != HeaderType.File) continue;
                foundFile = true;
                break;
            }

            Assert.True(foundFile);
        }

        Assert.Equal(1, cache.DerivationCount);
    }

    [Fact]
    public void DerivedKeyCacheReusesOnlyExactPasswordAndSalt()
    {
        var cache = new Rar3DerivedKeyCache();
        var firstSalt = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var secondSalt = (byte[])firstSalt.Clone();
        secondSalt[0]++;
        var derivations = 0;

        Rar3DerivedKeyMaterial Derive()
        {
            derivations++;
            return new Rar3DerivedKeyMaterial(new byte[16], new byte[16]);
        }

        var first = cache.GetOrCreate("password", firstSalt, Derive);
        var repeated = cache.GetOrCreate("password", (byte[])firstSalt.Clone(), Derive);
        var changedSalt = cache.GetOrCreate("password", secondSalt, Derive);
        var changedPassword = cache.GetOrCreate("other", secondSalt, Derive);

        Assert.Same(first, repeated);
        Assert.NotSame(repeated, changedSalt);
        Assert.NotSame(changedSalt, changedPassword);
        Assert.Equal(3, derivations);
        Assert.Equal(3, cache.DerivationCount);
    }
}
