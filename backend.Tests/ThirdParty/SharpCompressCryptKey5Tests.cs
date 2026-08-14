using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.IO;
using SharpCompress.Readers;
using Xunit;

namespace NzbWebDAV.Tests.ThirdParty;

public class SharpCompressCryptKey5Tests
{
    // First encrypted main + file headers from SharpCompress's
    // Rar5.encrypted_filesAndHeader.rar test archive. Password: test.
    private const string EncryptedHeaderFixture =
        "UmFyIRoHAQADasgHIQQAAAEPiORM0xChi/GBWFzwG2EkiQT3h2jj2Vi+02GHYLH/rnDEMnqxIAKYn9wmI8e0OK83ohIk0vO35BFQpYz+bHsB+oiaJiRlzLGZIOwI8SdUinKFF46hRN2fNmNCTNP9+lQMRqqOuRioEp9+rweJZQ0LC9HwAZtow8Svp/3NsXXtA97HclCf1AHQhtbqWpyVZ8mbKyDA1Pw3QLnkQq+bcWcTmdbht0Yr57RIwF8SgQUDtmpgHe5w8EQD4XyRsBY=";

    [Fact]
    public void HeaderEncryptedRar5DerivesSharedHeaderKeyOnce()
    {
        using var stream = new MemoryStream(Convert.FromBase64String(EncryptedHeaderFixture));
        var factory = CreateHeaderFactory();

        var foundFile = factory.ReadHeaders(stream).Any(header => header.HeaderType == HeaderType.File);

        Assert.True(foundFile);
        Assert.Equal(1, factory.Rar5KeyDerivationCount);
    }

    [Fact]
    public async Task HeaderEncryptedRar5DerivesSharedHeaderKeyOnceAsync()
    {
        await using var stream = new MemoryStream(Convert.FromBase64String(EncryptedHeaderFixture));
        var factory = CreateHeaderFactory();
        var foundFile = false;

        await foreach (var header in factory.ReadHeadersAsync(stream))
        {
            if (header.HeaderType != HeaderType.File) continue;
            foundFile = true;
            break;
        }

        Assert.True(foundFile);
        Assert.Equal(1, factory.Rar5KeyDerivationCount);
    }

    [Fact]
    public void DerivedKeyCacheReusesOnlyExactCryptoParameters()
    {
        var cache = new Rar5DerivedKeyCache();
        var firstSalt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var secondSalt = (byte[])firstSalt.Clone();
        secondSalt[0]++;
        var derivations = 0;

        List<byte[]> Derive()
        {
            derivations++;
            return [new byte[32], new byte[32], new byte[32]];
        }

        var first = cache.GetOrCreate("password", firstSalt, 15, Derive);
        var repeated = cache.GetOrCreate("password", (byte[])firstSalt.Clone(), 15, Derive);
        var changedSalt = cache.GetOrCreate("password", secondSalt, 15, Derive);
        var changedCount = cache.GetOrCreate("password", secondSalt, 16, Derive);
        var changedPassword = cache.GetOrCreate("other", secondSalt, 16, Derive);

        Assert.Same(first, repeated);
        Assert.NotSame(repeated, changedSalt);
        Assert.NotSame(changedSalt, changedCount);
        Assert.NotSame(changedCount, changedPassword);
        Assert.Equal(4, derivations);
        Assert.Equal(4, cache.DerivationCount);
    }

    [Fact]
    public async Task ExplicitCacheReusesRar5HeaderKeyAcrossVolumeFactories()
    {
        var cache = new Rar5DerivedKeyCache();

        for (var volume = 0; volume < 3; volume++)
        {
            await using var stream = new MemoryStream(
                Convert.FromBase64String(EncryptedHeaderFixture));
            var factory = new RarHeaderFactory(
                StreamingMode.Seekable,
                new ReaderOptions { Password = "test", LeaveStreamOpen = true },
                cache);

            await foreach (var header in factory.ReadHeadersAsync(stream))
            {
                if (header.HeaderType == HeaderType.File) break;
            }
        }

        Assert.Equal(1, cache.DerivationCount);
    }

    [Fact]
    public void OptimizedDerivationMatchesReference()
    {
        var random = new Random(0x5A17);
        for (var sample = 0; sample < 32; sample++)
        {
            var password = sample switch
            {
                0 => string.Empty,
                1 => "test",
                2 => "påsswörd-密码",
                _ => RandomPassword(random, random.Next(1, 96)),
            };
            var salt = new byte[16];
            var iv = new byte[16];
            random.NextBytes(salt);
            random.NextBytes(iv);
            var lg2Count = sample == 31 ? 15 : random.Next(0, 11);
            var expected = Reference(password, salt, lg2Count);
            var expectedCheck = PasswordCheck(expected[2]);
            var info = (Rar5CryptoInfo)Activator.CreateInstance(
                typeof(Rar5CryptoInfo), nonPublic: true
            )!;
            info.LG2Count = lg2Count;
            info.InitV = iv;
            info.Salt = salt;
            info.UsePswCheck = true;
            info.PswCheck = expectedCheck;

            var key = new CryptKey5(password, info);
            using var actualTransform = key.Transformer(salt);
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = expected[0];
            aes.IV = iv;
            using var expectedTransform = aes.CreateDecryptor();
            var ciphertext = new byte[32];
            random.NextBytes(ciphertext);

            Assert.Equal(expected[1], key.HashKey);
            Assert.Equal(expectedCheck, key.PswCheck);
            Assert.Equal(
                expectedTransform.TransformFinalBlock(ciphertext, 0, ciphertext.Length),
                actualTransform.TransformFinalBlock(ciphertext, 0, ciphertext.Length));
        }
    }

    private static List<byte[]> Reference(string password, byte[] salt, int lg2Count)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var rarSalt = salt.Concat(new byte[] { 0, 0, 0, 1 }).ToArray();
        var block = HMACSHA256.HashData(passwordBytes, rarSalt);
        var finalHash = (byte[])block.Clone();
        var result = new List<byte[]>();

        foreach (var rounds in new[] { 1 << lg2Count, 17, 17 })
        {
            for (var i = 1; i < rounds; i++)
            {
                block = HMACSHA256.HashData(passwordBytes, block);
                for (var j = 0; j < finalHash.Length; j++)
                {
                    finalHash[j] ^= block[j];
                }
            }
            result.Add((byte[])finalHash.Clone());
        }
        return result;
    }

    private static byte[] PasswordCheck(byte[] derived)
    {
        var check = new byte[8];
        for (var i = 0; i < derived.Length; i++)
        {
            check[i % check.Length] ^= derived[i];
        }
        return check;
    }

    private static string RandomPassword(Random random, int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.";
        var chars = new char[length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[random.Next(alphabet.Length)];
        }
        return new string(chars);
    }

    private static RarHeaderFactory CreateHeaderFactory() =>
        new(
            StreamingMode.Seekable,
            new ReaderOptions { Password = "test", LeaveStreamOpen = true }
        );
}
