namespace NzbWebDAV.Clients.Usenet.Contexts;

/// <summary>
/// Phase budgets for one provider attempt.
/// `AcquisitionTimeout` bounds waiting for a usable pooled connection
/// (queue wait plus any handshake), and `CommandTimeout` starts only once a
/// connection has been acquired. This keeps a freshly authenticated socket
/// from being cancelled by an outer deadline that was consumed while the
/// attempt was still waiting for capacity. `ResponseInactivityTimeout`, when
/// set for a pipelined command, bounds the silence between responses without
/// replacing the absolute command timeout.
/// </summary>
internal sealed record ProviderAttemptContext(
    TimeSpan AcquisitionTimeout,
    TimeSpan CommandTimeout,
    Action? ArmCommandTimeout = null,
    TimeSpan? ResponseInactivityTimeout = null);
