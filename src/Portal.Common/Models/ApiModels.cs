namespace Portal.Common;

public record PairRequest(string Code);
// MacAddress is returned only for successful Wi-Fi/LAN pairing.
// Format: uppercase hex with '-' separators, for example AA-BB-CC-DD-EE-FF.
public record PairResponse(string ClientId, string? MacAddress = null);
public record UnlockRequest(string ClientId);
public record UnlockResponse(bool Success, string? Error);

// WebSocket Host -> Client and Client -> Host
public record WsMessage(string Type, string? ClientId, string? Status, string? RequestId = null);
