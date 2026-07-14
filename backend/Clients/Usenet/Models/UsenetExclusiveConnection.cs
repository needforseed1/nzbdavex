using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet.Models;

public readonly struct UsenetExclusiveConnection
{
    public UsenetExclusiveConnection(Action<ArticleBodyResult>? onConnectionReadyAgain)
        : this(onConnectionReadyAgain, null, null)
    {
    }

    internal UsenetExclusiveConnection(
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        INntpClient? owner,
        Action? onCallCompleted)
    {
        OnConnectionReadyAgain = onConnectionReadyAgain;
        Owner = owner;
        OnCallCompleted = onCallCompleted;
    }

    public Action<ArticleBodyResult>? OnConnectionReadyAgain { get; }
    internal INntpClient? Owner { get; }
    internal Action? OnCallCompleted { get; }
}
