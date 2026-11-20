using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;

namespace Dollhouse.Server.Licensing;

public sealed record LicenseChallengeBinding(string AccountId, string Session, string HeaderDevice, string Device,
    string PublicKey, string Action, string ClientNonce, string Product = "fallendoll")
{
    public override string ToString() => nameof(LicenseChallengeBinding);
}

public sealed record LicenseChallenge(string Nonce, long ExpiresAt, LicenseChallengeBinding Binding)
{
    public override string ToString() => nameof(LicenseChallenge);
}

/// <summary>Bounded, process-local, short-lived challenges for the subsequent device proof.</summary>
public sealed class LicenseChallengeStore(TimeProvider? clock = null)
{
    public const int Capacity = 4096;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> challenges = new(StringComparer.Ordinal);
    private sealed record Entry(LicenseChallenge Challenge, long CreatedTimestamp);

    private bool Expired(Entry entry) => clock.GetElapsedTime(entry.CreatedTimestamp) >= Lifetime;

    public LicenseChallenge? Issue(LicenseChallengeBinding binding)
    {
        lock (gate)
        {
            foreach (var nonce in challenges.Where(pair => Expired(pair.Value)).Select(pair => pair.Key).ToArray())
                challenges.Remove(nonce);
            if (challenges.Count >= Capacity) return null;
            string value;
            do { value = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); }
            while (challenges.ContainsKey(value));
            var challenge = new LicenseChallenge(value, clock.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds(), binding);
            challenges.Add(value, new Entry(challenge, clock.GetTimestamp()));
            return challenge;
        }
    }

    public bool TryGet(string nonce, [NotNullWhen(true)] out LicenseChallenge? challenge)
    {
        challenge = null;
        if (nonce.Length != 64) return false;
        lock (gate)
        {
            if (!challenges.TryGetValue(nonce, out var entry)) return false;
            if (Expired(entry)) { challenges.Remove(nonce); return false; }
            challenge = entry.Challenge;
            return true;
        }
    }

    // Read context with TryGet, validate the device signature, then atomically consume once.
    public bool TryConsume(string nonce, LicenseChallengeBinding binding, [NotNullWhen(true)] out LicenseChallenge? challenge)
    {
        challenge = null;
        if (nonce.Length != 64) return false;
        lock (gate)
        {
            if (!challenges.TryGetValue(nonce, out var entry)) return false;
            if (Expired(entry)) { challenges.Remove(nonce); return false; }
            if (entry.Challenge.Binding != binding) return false;
            challenges.Remove(nonce);
            challenge = entry.Challenge;
            return true;
        }
    }
}
