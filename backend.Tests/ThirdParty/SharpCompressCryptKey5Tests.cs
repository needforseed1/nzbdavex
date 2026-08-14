using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Common.Rar;
using Xunit;

namespace NzbWebDAV.Tests.ThirdParty;

public class SharpCompressCryptKey5Tests
{
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
}
