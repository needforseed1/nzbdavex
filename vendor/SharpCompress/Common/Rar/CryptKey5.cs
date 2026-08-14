using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Common.Rar.Headers;

namespace SharpCompress.Common.Rar;

internal class CryptKey5 : ICryptKey
{
    const int AES_256 = 256;
    const int DERIVED_KEY_LENGTH = 0x10;
    const int SHA256_DIGEST_SIZE = 32;

    private string _password;
    private Rar5CryptoInfo _cryptoInfo;
    private readonly Rar5DerivedKeyCache _derivedKeyCache;
    private byte[] _pswCheck = [];
    private byte[] _hashKey = [];

    public CryptKey5(string? password, Rar5CryptoInfo rar5CryptoInfo)
        : this(password, rar5CryptoInfo, new Rar5DerivedKeyCache()) { }

    internal CryptKey5(
        string? password,
        Rar5CryptoInfo rar5CryptoInfo,
        Rar5DerivedKeyCache derivedKeyCache
    )
    {
        _password = password ?? "";
        _cryptoInfo = rar5CryptoInfo;
        _derivedKeyCache = derivedKeyCache;
    }

    public byte[] PswCheck => _pswCheck;

    public byte[] HashKey => _hashKey;

    private static List<byte[]> GenerateRarPBKDF2Key(
        string password,
        byte[] salt,
        int iterations,
        int keyLength
    )
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        using var hmac = new HMACSHA256(passwordBytes);
        var block = new byte[SHA256_DIGEST_SIZE];
        var nextBlock = new byte[SHA256_DIGEST_SIZE];

        void ComputeHmac(ReadOnlySpan<byte> input, Span<byte> output)
        {
            if (!hmac.TryComputeHash(input, output, out var written)
                || written != SHA256_DIGEST_SIZE)
            {
                throw new CryptographicException("Unable to derive the RAR5 encryption key.");
            }
        }

        ComputeHmac(salt, block);
        var finalHash = (byte[])block.Clone();

        var loop = new int[] { iterations, 17, 17 };
        var res = new List<byte[]> { };

        for (var x = 0; x < 3; x++)
        {
            for (var i = 1; i < loop[x]; i++)
            {
                ComputeHmac(block, nextBlock);
                (block, nextBlock) = (nextBlock, block);
                for (var j = 0; j < finalHash.Length; j++)
                {
                    finalHash[j] ^= block[j];
                }
            }

            res.Add((byte[])finalHash.Clone());
        }

        CryptographicOperations.ZeroMemory(passwordBytes);
        CryptographicOperations.ZeroMemory(block);
        CryptographicOperations.ZeroMemory(nextBlock);
        CryptographicOperations.ZeroMemory(finalHash);

        return res;
    }

    public ICryptoTransform Transformer(byte[] salt)
    {
        var iterations = (1 << _cryptoInfo.LG2Count); // Adjust the number of iterations as needed

        var salt_rar5 = salt.Concat(new byte[] { 0, 0, 0, 1 });
        var derivedKey = _derivedKeyCache.GetOrCreate(
            _password,
            salt,
            _cryptoInfo.LG2Count,
            () => GenerateRarPBKDF2Key(
                _password,
                salt_rar5.ToArray(),
                iterations,
                DERIVED_KEY_LENGTH
            )
        );
        _hashKey = derivedKey[1];

        _pswCheck = new byte[EncryptionConstV5.SIZE_PSWCHECK];

        for (var i = 0; i < SHA256_DIGEST_SIZE; i++)
        {
            _pswCheck[i % EncryptionConstV5.SIZE_PSWCHECK] ^= derivedKey[2][i];
        }

        if (_cryptoInfo.UsePswCheck && !_cryptoInfo.PswCheck.SequenceEqual(_pswCheck))
        {
            throw new CryptographicException("The password did not match.");
        }

        var aes = Aes.Create();
        aes.KeySize = AES_256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = derivedKey[0];
        aes.IV = _cryptoInfo.InitV;
        return aes.CreateDecryptor();
    }
}
