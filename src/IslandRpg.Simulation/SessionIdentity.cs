using System.Security.Cryptography;

namespace IslandRpg.Simulation;

public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public readonly record struct PlayerId(Guid Value)
{
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ActorId(Guid Value)
{
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ClientConnectionId(Guid Value)
{
    public static ClientConnectionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Opaque bearer secret returned only when joining. Snapshots never expose it.
/// </summary>
public readonly record struct ReconnectToken(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => "[redacted]";
}

public readonly record struct PlayerIdentity(PlayerId PlayerId, ActorId ActorId);

public interface ISessionIdentitySource
{
    PlayerIdentity CreatePlayerIdentity();

    ReconnectToken CreateReconnectToken();
}

/// <summary>
/// Production identity source using cryptographically strong random values.
/// </summary>
public sealed class SecureSessionIdentitySource : ISessionIdentitySource
{
    public PlayerIdentity CreatePlayerIdentity() =>
        new(new PlayerId(Guid.NewGuid()), new ActorId(Guid.NewGuid()));

    public ReconnectToken CreateReconnectToken() =>
        new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
}
