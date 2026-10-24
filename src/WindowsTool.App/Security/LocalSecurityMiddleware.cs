using Microsoft.Net.Http.Headers;

namespace WindowsTool.App.Security;

/// <summary>
/// 本機服務的安全防線（PLAN 11.1）：
/// 1. 所有回應加上安全標頭；
/// 2. Host header 驗證（防 DNS rebinding）；
/// 3. <c>/api/*</c> 必須帶正確的 session token。
/// 不啟用 CORS，所以其他網頁的腳本讀不到回應，也帶不了 token。
/// </summary>
public sealed class LocalSecurityMiddleware(RequestDelegate next, LocalServerState state)
{
    // Vue 全域版在執行時編譯 template，需要 'unsafe-eval'；腳本來源仍然只允許本機（'self'）。
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-eval'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var headers = context.Response.Headers;

        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers[HeaderNames.XFrameOptions] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers[HeaderNames.ContentSecurityPolicy] = ContentSecurityPolicy;

        if (!HostValidator.IsAllowed(request.Host, state.Port))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return context.Response.WriteAsync("Invalid Host header.");
        }

        if (request.Path.StartsWithSegments("/api"))
        {
            headers[HeaderNames.CacheControl] = "no-store";

            if (!SessionToken.IsAuthorized(request.Headers.Authorization, state.Token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                headers[HeaderNames.WWWAuthenticate] = "Bearer";
                return Task.CompletedTask;
            }
        }

        return next(context);
    }
}
