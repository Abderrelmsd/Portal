using System.Diagnostics;
using System.Reflection;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Portal;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Portal: problem-details error handling, exception mapping, correlation ids and one OpenAPI document per API version.
    /// Then call <c>app.UsePortal()</c>, <c>app.MapPortalApi(1, …)</c> and (optionally) <c>app.MapPortalOpenApi()</c>. Register validators with <see cref="AddPortalValidators"/>.
    /// </summary>
    public static IServiceCollection AddPortal(this IServiceCollection services, Action<PortalOptions>? configure = null)
    {
        var options = new PortalOptions();
        configure?.Invoke(options);
        var errors = options.Validate();
        if (errors.Count > 0) throw new ArgumentException("Invalid PortalOptions: " + string.Join("; ", errors));
        services.AddSingleton(Options.Create(options));

        services.AddProblemDetails(pd => pd.CustomizeProblemDetails = ctx =>
        {
            var http = ctx.HttpContext;
            ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
            if (http.GetCorrelationId() is { } c) ctx.ProblemDetails.Extensions["correlationId"] = c;
            ctx.ProblemDetails.Instance ??= http.Request.Path;
            // framework-generated problems (unknown route, wrong method…) carry generic RFC 9110 type URIs; give them our stable ones
            if (ctx.ProblemDetails.Type is null || ctx.ProblemDetails.Type.StartsWith("https://tools.ietf.org/html/rfc9110", StringComparison.Ordinal))
                ctx.ProblemDetails.Type = (ctx.ProblemDetails.Status switch { 400 => "bad-request", 401 => "unauthorized", 403 => "forbidden", 404 => "not-found", 405 => "method-not-allowed", 406 => "not-acceptable", 415 => "unsupported-media-type", 500 => "internal-error", _ => "error" });
        });
        services.AddExceptionHandler<PortalExceptionHandler>();
        services.AddSingleton<PortalValidationFilter>();

        foreach (var v in options.Versions)
            services.AddOpenApi($"v{v}", o =>
            {
                o.ShouldInclude = d => d.GroupName == $"v{v}";
                o.AddDocumentTransformer((doc, ctx, ct) => PortalOpenApi.Document(doc, v, options));
                o.AddOperationTransformer((op, ctx, ct) => PortalOpenApi.Operation(op, ctx, v, options));
            });
        return services;
    }

    /// <summary>Registers every FluentValidation validator found in the given assemblies (default: the calling assembly) so <c>MapPortalApi</c> groups validate arguments automatically.</summary>
    public static IServiceCollection AddPortalValidators(this IServiceCollection services, params Assembly[] assemblies)
    {
        foreach (var asm in assemblies.Length == 0 ? [Assembly.GetCallingAssembly()] : assemblies)
            foreach (var type in asm.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }))
                foreach (var i in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>)))
                    services.TryAddEnumerable(ServiceDescriptor.Scoped(i, type));
        return services;
    }

    /// <summary>Adds a mapper for exceptions from other packages, e.g. <c>services.AddPortalExceptionMapper&lt;TenantViolationException&gt;(403, "tenant-violation", "Forbidden.")</c>.</summary>
    public static IServiceCollection AddPortalExceptionMapper<T>(this IServiceCollection services, int status, string code, string title) where T : Exception
    {
        services.AddSingleton<IPortalExceptionMapper>(new DelegateMapper<T>(e => new PortalException(status, code, title, e.Message)));
        return services;
    }
}

internal static class PortalOpenApi
{
    public static Task Document(OpenApiDocument doc, int version, PortalOptions o)
    {
        doc.Info = new OpenApiInfo { Title = o.Title, Version = $"{version}.0", Description = o.Description };
        doc.Components ??= new OpenApiComponents();

        doc.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        doc.Components.Schemas["ProblemDetails"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "An RFC 9457 problem document.",
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["type"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "A stable URI identifying the problem kind." },
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["instance"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["correlationId"] = new OpenApiSchema { Type = JsonSchemaType.String },
            },
        };
        doc.Components.Schemas["ValidationProblemDetails"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AllOf = [new OpenApiSchemaReference("ProblemDetails", doc)],
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["errors"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object, Description = "Messages per field (JSON property names).",
                    AdditionalProperties = new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.String } },
                },
            },
        };

        if (o.DocumentBearerAuth)
        {
            doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            doc.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", Description = "A session token issued at login." };
            doc.Security ??= [];
            doc.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", doc)] = [] });
        }
        return Task.CompletedTask;
    }

    public static Task Operation(OpenApiOperation op, OpenApiOperationTransformerContext ctx, int version, PortalOptions o)
    {
        var doc = ctx.Document;
        op.Responses ??= new OpenApiResponses();
        void Problem(string status, string description, string schema = "ProblemDetails")
        {
            if (op.Responses.ContainsKey(status) || doc is null) return;
            op.Responses[status] = new OpenApiResponse
            {
                Description = description,
                Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new OpenApiMediaType { Schema = new OpenApiSchemaReference(schema, doc) } },
            };
        }
        Problem("400", "The request is malformed or failed validation.", "ValidationProblemDetails");
        if (o.DocumentBearerAuth) { Problem("401", "Authentication is required."); Problem("403", "The caller isn't allowed to do this."); }
        Problem("429", "Too many requests; see the Retry-After header.");
        Problem("500", "An unexpected error occurred. Quote the traceId when reporting it.");
        if (o.Deprecated.ContainsKey(version)) op.Deprecated = true;
        return Task.CompletedTask;
    }
}
