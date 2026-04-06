using System.Text.Json.Serialization;

namespace Portal.Common;

public class BtMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

public class BtPairRequest : BtMessage
{
    public BtPairRequest() { Type = "pair_request"; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;
}

public class BtPairResponse : BtMessage
{
    public BtPairResponse() { Type = "pair_response"; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class BtRegisterMessage : BtMessage
{
    public BtRegisterMessage() { Type = "register"; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }
}

public class BtUnlockRequest : BtMessage
{
    public BtUnlockRequest() { Type = "unlock_request"; }

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;
}

public class BtUnlockResponse : BtMessage
{
    public BtUnlockResponse() { Type = "unlock_response"; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class BtHostUnlockRequest : BtMessage
{
    public BtHostUnlockRequest() { Type = "host_unlock_request"; }

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }
}

public class BtHostUnlockResponse : BtMessage
{
    public BtHostUnlockResponse() { Type = "host_unlock_response"; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }
}
