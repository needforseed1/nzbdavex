namespace NzbWebDAV.Clients.Usenet.Contexts;

/// <summary>
/// Phase budgets for one provider attempt of a pipelined health batch.
/// `AcquisitionTimeout` bounds waiting for a usable pooled connection
/// (queue wait plus any handshake), and `CommandTimeout` starts only once a
/// connection has been acquired. This keeps a freshly authenticated socket
/// from being cancelled by an outer deadline that was consumed while the
/// attempt was still waiting for capacity.
/// </summary>
internal sealed record ProviderAttemptContext(
    TimeSpan AcquisitionTimeout,
    TimeSpan CommandTimeout);
