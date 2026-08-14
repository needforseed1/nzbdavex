using System;

namespace SharpCompress.Common.Rar;

internal sealed class Rar3DerivedKeyMaterial(byte[] key, byte[] initV)
{
    internal byte[] Key { get; } = key;
    internal byte[] InitV { get; } = initV;
}

internal sealed class Rar3DerivedKeyCache
{
    private readonly object _gate = new();
    private string? _password;
    private byte[]? _salt;
    private Rar3DerivedKeyMaterial? _derivedKey;

    internal int DerivationCount { get; private set; }

    internal Rar3DerivedKeyMaterial GetOrCreate(
        string password,
        byte[] salt,
        Func<Rar3DerivedKeyMaterial> derive
    )
    {
        lock (_gate)
        {
            if (
                _derivedKey is not null
                && string.Equals(_password, password, StringComparison.Ordinal)
                && _salt.AsSpan().SequenceEqual(salt)
            )
            {
                return _derivedKey;
            }

            DerivationCount++;
            _password = password;
            _salt = (byte[])salt.Clone();
            _derivedKey = derive();
            return _derivedKey;
        }
    }
}
