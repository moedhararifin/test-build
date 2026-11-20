using System.Text.Json;

namespace Dollhouse.Server.Licensing;

public sealed record LicenseProof(string Account, string Action, string ClientNonce, string Device, string Nonce, string Product)
{
    public static LicenseProof FromChallenge(LicenseChallenge challenge)
    {
        var binding = challenge.Binding;
        return new(binding.AccountId, binding.Action, binding.ClientNonce, binding.Device, challenge.Nonce, binding.Product);
    }

    public byte[] Encode()
    {
        // Native proof bytes: UTF-8, compact JSON, exactly these six properties in this order.
        // Every value is constrained to ASCII by local account/challenge creation.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("account", Account);
            writer.WriteString("action", Action);
            writer.WriteString("client_nonce", ClientNonce);
            writer.WriteString("device", Device);
            writer.WriteString("nonce", Nonce);
            writer.WriteString("product", Product);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public override string ToString() => nameof(LicenseProof);
}
