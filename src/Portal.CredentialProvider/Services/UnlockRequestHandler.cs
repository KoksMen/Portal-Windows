using Microsoft.AspNetCore.Http;
using Portal.Common;
using System.Security;

namespace Portal.CredentialProvider.Services;

public class UnlockRequestHandler
{
    private readonly PortalWinConfig _config;
    private readonly AttemptTracker _attemptTracker;

    public event Action<string, SecureString?, string>? UnlockRequested;

    public UnlockRequestHandler(PortalWinConfig config, AttemptTracker attemptTracker)
    {
        _config = config;
        _attemptTracker = attemptTracker;
    }

    public IResult HandleUnlockRequest(UnlockRequest request, HttpContext context)
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
        Logger.Log($"[UnlockHandler] Unlock Request from {clientIp}. ClientId: {request.ClientId}");

        if (_attemptTracker.IsBlocked(clientIp))
        {
            Logger.LogWarning($"[UnlockHandler] Rejected for {clientIp}: Too many failed attempts.");
            return Results.Json(new UnlockResponse(false, "Too many requests"), statusCode: 429);
        }

        var device = context.Items["ValidatedDevice"] as Portal.Common.Models.DeviceModel;

        if (device == null)
        {
            Logger.LogWarning($"[UnlockHandler] Validation failed for {clientIp}: Missing device context.");
            _attemptTracker.RecordFailure(clientIp);
            return Results.Json(new UnlockResponse(false, "Unauthorized"), statusCode: 403);
        }

        if (!string.Equals(device.ClientId, request.ClientId, StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning($"[UnlockHandler] ClientId mismatch. Device: {device.ClientId}, Request: {request.ClientId}");
            _attemptTracker.RecordFailure(clientIp);
            return Results.Json(new UnlockResponse(false, "ClientId mismatch"), statusCode: 403);
        }

        if (!device.IsEnabled)
        {
            Logger.LogWarning($"[UnlockHandler] Rejected unlock request from disabled device: {device.IdsSafe()}");
            _attemptTracker.RecordFailure(clientIp);
            return Results.Json(new UnlockResponse(false, "Client disabled"), statusCode: 403);
        }

        var account = device.Accounts.FirstOrDefault();
        using var securePassword = account?.GetDecryptedSecurePassword();
        if (account == null || securePassword == null || securePassword.Length == 0)
        {
            Logger.LogWarning($"[UnlockHandler] No credentials for device: {device.Name}");
            _attemptTracker.RecordFailure(clientIp);
            return Results.Json(new UnlockResponse(false, "No credentials"), statusCode: 400);
        }

        _attemptTracker.RecordSuccess(clientIp);
        Logger.Log($"[UnlockHandler] Unlock APPROVED for user: {account.Username} from {device.Name}");
        UnlockRequested?.Invoke(account.Username, securePassword, account.Domain);

        return Results.Ok(new UnlockResponse(true, null));
    }

    public Portal.Common.Models.DeviceModel? ValidateWebSocketClient(HttpContext context)
    {
        return context.Items["ValidatedDevice"] as Portal.Common.Models.DeviceModel;
    }
}
