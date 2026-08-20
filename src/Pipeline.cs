using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Portal;

/// <summary>Turns exceptions into RFC 9457 problem responses: deliberate <see cref="PortalException"/>s as themselves, framework/binding/validation errors as 4xx, everything else as an opaque 500.</summary>
internal sealed class PortalExceptionHandler(IProblemDetailsService problems, IOptions<PortalOptions> options, IEnumerable<IPortalExceptionMapper> mappers, ILogger<PortalExceptionHandler> logger) : IExceptionHandler
{
    private readonly PortalOptions _o = options.Value;

    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        if (exception is OperationCanceledException && http.RequestAborted.IsCancellationRequested) return true;   // the client left; nothing to say

        var problem = Classify(exception);
        if (problem.Status >= 500) logger.LogError(exception, "Unhandled exception on {Method} {Path}", http.Request.Method, http.Request.Path);
        else logger.LogInformation("{Method} {Path} failed with {Status} {Code}: {Detail}", http.Request.Method, http.Request.Path, problem.Status, problem.Code, problem.Detail);

        http.Response.StatusCode = problem.Status;
        foreach (var (k, v) in problem.Headers) http.Response.Headers[k] = v;

        var details = new ProblemDetails { Status = problem.Status, Title = problem.Title, Detail = problem.Detail, Type = problem.Code };
        foreach (var (k, v) in problem.Extensions) details.Extensions[k] = v;
        if (exception is ValidationException ve) details.Extensions["errors"] = ValidationErrors.From(ve.Errors);
        if (problem.Status >= 500 && _o.ExposeExceptionDetails) { details.Detail = exception.Message; details.Extensions["exception"] = exception.GetType().FullName; }

        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = details, Exception = exception });
    }

    private PortalException Classify(Exception e)
    {
        if (e is PortalException pe) return pe;
        foreach (var m in _o.Mappers.Concat(mappers)) if (m.Map(e) is { } mapped) return mapped;
        return e switch
        {
            ValidationException => new PortalException(_o.ValidationStatusCode, "validation-failed", "One or more validation errors occurred."),
            BadHttpRequestException b => new PortalException(b.StatusCode, "bad-request", "The request is invalid.", b.Message),
            JsonException => new PortalException(400, "malformed-json", "The request body is not valid JSON."),
            _ => new PortalException(500, "internal-error", "An unexpected error occurred."),
        };
    }
}

internal static class ValidationErrors
{
    /// <summary>FluentValidation failures grouped by property, using JSON-style names: <c>Address.City</c> → <c>address.city</c>, <c>Items[0].Sku</c> → <c>items[0].sku</c>.</summary>
    public static Dictionary<string, string[]> From(IEnumerable<FluentValidation.Results.ValidationFailure> failures)
        => failures.GroupBy(f => Name(f.PropertyName), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct().ToArray());

    public static string Name(string property)
    {
        if (string.IsNullOrEmpty(property)) return "";
        return string.Join(".", property.Split('.').Select(seg => seg.Length == 0 ? seg : char.ToLowerInvariant(seg[0]) + seg[1..]));
    }
}

/// <summary>Validates endpoint arguments that have a registered <see cref="IValidator{T}"/>; on failure short-circuits with a <c>validation-failed</c> problem.</summary>
internal sealed class PortalValidationFilter(IOptions<PortalOptions> options) : IEndpointFilter
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Type> ValidatorTypes = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var arg in context.Arguments)
        {
            if (arg is null || arg is HttpContext or CancellationToken or string || arg.GetType().IsPrimitive || arg.GetType().IsValueType && arg.GetType().Namespace == "System") continue;
            var validatorType = ValidatorTypes.GetOrAdd(arg.GetType(), t => typeof(IValidator<>).MakeGenericType(t));
            if (context.HttpContext.RequestServices.GetService(validatorType) is not IValidator validator) continue;
            var result = await validator.ValidateAsync(new ValidationContext<object>(arg), context.HttpContext.RequestAborted);
            failures.AddRange(result.Errors);
        }
        if (failures.Count == 0) return await next(context);

        var o = options.Value;
        var details = new ProblemDetails
        {
            Status = o.ValidationStatusCode, Title = "One or more validation errors occurred.", Type = "validation-failed",
            Detail = "See the 'errors' field for details.",
        };
        details.Extensions["errors"] = ValidationErrors.From(failures);
        return Results.Problem(details);
    }
}

internal sealed class PortalPipelineMiddleware(RequestDelegate next, IOptions<PortalOptions> options)
{
    private readonly PortalOptions _o = options.Value;

    public async Task InvokeAsync(HttpContext http)
    {
        // correlation: honour a caller-supplied id, otherwise use the trace id, and echo it back
        var correlation = http.Request.Headers.TryGetValue(_o.CorrelationHeader, out var v) && v.ToString() is { Length: > 0 and <= 128 } given && given.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
            ? given : Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
        http.Items[CorrelationKey] = correlation;
        http.Response.OnStarting(() => { http.Response.Headers[_o.CorrelationHeader] = correlation; return Task.CompletedTask; });

        if (TryVersion(http.Request.Path, out var version, out var versioned) && versioned)
        {
            if (!_o.Versions.Contains(version))
            {
                await WriteUnsupportedVersionAsync(http, version);
                return;
            }
            http.Response.OnStarting(() =>
            {
                var h = http.Response.Headers;
                h["api-supported-versions"] = string.Join(", ", _o.Versions.Where(x => !_o.Deprecated.ContainsKey(x)).Order());
                if (_o.Deprecated.Count > 0) h["api-deprecated-versions"] = string.Join(", ", _o.Deprecated.Keys.Order());
                if (_o.Deprecated.TryGetValue(version, out var dep))
                {
                    h["Deprecation"] = "true";
                    if (dep.Sunset is { } s) h["Sunset"] = s.UtcDateTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    if (dep.Link is { } l) h.Append("Link", $"<{l}>; rel=\"deprecation\"");
                }
                return Task.CompletedTask;
            });
        }
        await next(http);
    }

    public const string CorrelationKey = "Portal.CorrelationId";

    private static bool TryVersion(PathString path, out int version, out bool versioned)
    {
        version = 0; versioned = false;
        var s = path.Value;
        if (s is null || s.Length < 3 || s[0] != '/' || s[1] != 'v') return false;
        var end = s.IndexOf('/', 2); var digits = end < 0 ? s[2..] : s[2..end];
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit) || digits.Length > 4) return false;
        version = int.Parse(digits); versioned = true;
        return true;
    }

    private async Task WriteUnsupportedVersionAsync(HttpContext http, int version)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        http.Response.Headers["api-supported-versions"] = string.Join(", ", _o.Versions.Order());
        var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        var details = new ProblemDetails
        {
            Status = 400, Title = "Unsupported API version.", Type = "unsupported-api-version",
            Detail = $"Version {version} is not available. Supported: {string.Join(", ", _o.Versions.Order().Select(v => "v" + v))}.",
        };
        details.Extensions["supportedVersions"] = _o.Versions.Order().ToArray();
        await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = details });
    }
}

public static class PortalApplicationExtensions
{
    /// <summary>
    /// Adds Portal to the pipeline: correlation id, API-version checking and headers, exception → problem mapping, and problem bodies for bare status codes
    /// (404 for unknown routes, 405, 415…). Call it early, before routing/auth.
    /// </summary>
    public static IApplicationBuilder UsePortal(this IApplicationBuilder app)
    {
        app.UseMiddleware<PortalPipelineMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }

    /// <summary>Maps a versioned route group (<c>/v{version}</c>) with request validation and OpenAPI grouping applied. Add your endpoints inside.</summary>
    public static RouteGroupBuilder MapPortalApi(this IEndpointRouteBuilder endpoints, int version, Action<RouteGroupBuilder>? configure = null)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<PortalOptions>>().Value;
        if (!options.Versions.Contains(version)) throw new InvalidOperationException($"Version {version} is not listed in PortalOptions.Versions.");
        var group = endpoints.MapGroup($"/v{version}").WithGroupName($"v{version}").AddEndpointFilter<PortalValidationFilter>();
        configure?.Invoke(group);
        return group;
    }

    /// <summary>Maps the OpenAPI documents at <c>/openapi/v{n}.json</c> (one per version).</summary>
    public static IEndpointRouteBuilder MapPortalOpenApi(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<PortalOptions>>().Value;
        var openApi = endpoints.MapOpenApi("/openapi/{documentName}.json");
        if (options.RequireAuthorizationForOpenApi) openApi.RequireAuthorization();
        return endpoints;
    }

    /// <summary>The correlation id of the current request (also echoed in the response header and problem documents).</summary>
    public static string? GetCorrelationId(this HttpContext http) => http.Items.TryGetValue(PortalPipelineMiddleware.CorrelationKey, out var v) ? v as string : null;
}
