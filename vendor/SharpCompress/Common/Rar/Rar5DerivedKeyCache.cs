using System;
using System.Collections.Generic;

namespace SharpCompress.Common.Rar;

// RAR5 archive headers share password, salt, and KDF count while each header
// supplies its own IV. Keep one derivation for an archive walk and continue to
// create a fresh AES transform per header. The lock also coalesces derivation
// if a canceled caller lets shared resolution work briefly overlap its retry.
internal sealed class Rar5DerivedKeyCache
{
    private readonly object _gate = new();
    private string? _password;
    private byte[]? _salt;
    private int _lg2Count;
    private List<byte[]>? _derivedKey;

    internal int DerivationCount { get; private set; }

    internal List<byte[]> GetOrCreate(
        string password,
        byte[] salt,
        int lg2Count,
        Func<List<byte[]>> derive
    )
    {
        lock (_gate)
        {
            if (
                _derivedKey is not null
                && string.Equals(_password, password, StringComparison.Ordinal)
                && _lg2Count == lg2Count
                && _salt.AsSpan().SequenceEqual(salt)
            )
            {
                return _derivedKey;
            }

            DerivationCount++;
            _password = password;
            _salt = (byte[])salt.Clone();
            _lg2Count = lg2Count;
            _derivedKey = derive();
            return _derivedKey;
        }
    }
}
