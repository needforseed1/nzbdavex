# SharpCompress fork

This directory contains the C# sources required to build SharpCompress for
nzbdavex. It is based on upstream SharpCompress `0.48.0`, commit
`6e59c7d7bbf8c19a8a92c3c382599906684bb93d`.

The nzbdavex patch optimizes encrypted RAR header handling. The RAR5
key-derivation loop reuses one pre-keyed `HMACSHA256` instance and writes each
digest into reusable buffers. RAR3 and RAR5 derived keys are also reused when
the exact password and KDF parameters repeat while mapping volumes from one
archive. The algorithms, iteration counts, password encoding, salts, password
checks, initialization vectors, and derived AES material are unchanged.

The third-party cryptography tests cover independent RAR5 derivation and real
header-encrypted RAR3 and RAR5 fixtures.

SharpCompress is distributed under the MIT license in `LICENSE.txt`.
