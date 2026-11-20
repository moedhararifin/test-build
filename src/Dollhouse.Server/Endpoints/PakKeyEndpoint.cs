using System.Security.Cryptography;
using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

/// <summary>Local content-key delivery for the validated standalone archives, independent of licensing.</summary>
public sealed class PakKeyEndpoint
{
    public const string Path = "/api/v1/pakkey";
    private readonly string? key;
    private readonly TimeProvider clock;

    public PakKeyEndpoint(string? base64Key, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        if (base64Key is null) return;
        Span<byte> decoded = stackalloc byte[32];
        try
        {
            if (!Convert.TryFromBase64String(base64Key, decoded, out var length) || length != 32)
                throw new ArgumentException("The configured PAK key must be base64 encoding exactly 32 bytes.", nameof(base64Key));
            key = Convert.ToBase64String(decoded);
        }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }

    public bool IsConfigured => key is not null;

    public ServerRoute Route => new("POST", Path, HandleAsync);

    private Task HandleAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (key is null)
            return JsonResponse.WriteAsync(request.Context, StatusCodes.Status503ServiceUnavailable,
                new { Reason = "pakkey_not_configured", Detail = "The local content key is not configured." });

        // Match the observed response. The incoming body may be {} or carry a lease;
        // this local endpoint supplies content material and does not assert license validity.
        return JsonResponse.WriteAsync(request.Context, StatusCodes.Status200OK,
            new { Key = key, Now = clock.GetUtcNow().ToUnixTimeSeconds() });
    }
}
