using Microsoft.AspNetCore.Diagnostics;

namespace GraphRag.Api.Middleware;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        logger.LogError(ex, "Request pipeline exception");

        var (status, title, detail, code) = Map(ex);
        await Results.Problem(
                detail: detail,
                title: title,
                statusCode: status,
                extensions: new Dictionary<string, object?> { ["code"] = code })
            .ExecuteAsync(ctx);

        return true;
    }

    private static (int Status, string Title, string Detail, string Code) Map(Exception ex)
    {
        switch (ex)
        {
            case ArgumentException ae:
                return (400, "Bad request", ae.Message, "bad_argument");
            case UnauthorizedAccessException ue:
                return (401, "Unauthorized", ue.Message, "auth_denied");
            case InvalidOperationException ioe:
                return MapInvalidOperation(ioe);
            default:
                return (500, "Server error", ex.Message, "internal_error");
        }
    }

    private static (int Status, string Title, string Detail, string Code) MapInvalidOperation(InvalidOperationException ex)
    {
        var msg = ex.Message;
        if (msg.Contains("429", StringComparison.Ordinal) ||
            msg.Contains("throttl", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
            return (429, "Kusto throttled", msg, "kusto_throttled");

        if (msg.Contains("LLM call failed", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Failed to parse LLM JSON", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Azure OpenAI", StringComparison.OrdinalIgnoreCase))
            return (502, "LLM error", msg, "llm_error");

        return (400, "Bad request", msg, "invalid_operation");
    }
}
