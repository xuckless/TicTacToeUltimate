namespace BackendWebAPI.Config;

public sealed class StripeSettings
{
    public string SecretKey { get; init; } = "";
    public string PublishableKey { get; init; } = "";
    public string WebhookSecret { get; init; } = "";
    public string Currency { get; init; } = "cad";
}