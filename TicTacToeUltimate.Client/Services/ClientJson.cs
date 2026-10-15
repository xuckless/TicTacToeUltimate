using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Services;

/// <summary>JSON settings shared by REST and SignalR; mirrors the server (ContractsJsonContext first, reflection fallback).</summary>
public static class ClientJson
{
    /// <summary>Options for REST payloads.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Inserts the contracts context at position 0 of the resolver chain.</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        if (options.TypeInfoResolverChain.Contains(ContractsJsonContext.Default)) return;
        options.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default);
        if (options.TypeInfoResolverChain.Count == 1) options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
    }

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(o);
        return o;
    }
}

/// <summary>Extracts error codes from SignalR/REST exceptions.</summary>
public static class ErrorText
{
    /// <summary>Best-effort error code: the token after the last ": " of a HubException message, or the API code.</summary>
    public static string Code(Exception ex) => ex switch
    {
        TttApiException api => api.Code ?? ErrorCodes.Internal,
        Microsoft.AspNetCore.SignalR.HubException hub => Tail(hub.Message),
        InvalidOperationException => "offline",
        HttpRequestException => "offline",
        _ => ErrorCodes.Internal,
    };

    private static string Tail(string message)
    {
        var i = message.LastIndexOf(": ", StringComparison.Ordinal);
        var tail = i >= 0 ? message[(i + 2)..] : message;
        return tail.Trim().TrimEnd('.');
    }
}
