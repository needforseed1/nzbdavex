# SharpCompress fork

This directory contains the C# sources required to build SharpCompress for
nzbdavex. It is based on upstream SharpCompress `0.48.0`, commit
`6e59c7d7bbf8c19a8a92c3c382599906684bb93d`.

The nzbdavex patch changes only the RAR5 key-derivation loop in
`Common/Rar/CryptKey5.cs`. It reuses one pre-keyed `HMACSHA256` instance and
writes each digest into reusable buffers. The RAR5 algorithm, iteration counts,
password encoding, salts, password check, and derived AES material are
unchanged.

`backend.Tests/ThirdParty/SharpCompressCryptKey5Tests.cs` compares the fork's
derived values and decryption output with an independent implementation of the
upstream algorithm.

SharpCompress is distributed under the MIT license in `LICENSE.txt`.
