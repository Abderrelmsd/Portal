# Portal

The outward-facing API conventions every product built on these packages shares: versioned routes, request validation with FluentValidation, RFC 9457 error responses, and OpenAPI documents. Portal is a library you wire into your own `Program.cs`. It is not a gateway or a proxy, and it depends on no other package in this set.

Use it so all your APIs report errors and versions the same way, and so clients can rely on it.

## Install

```bash
dotnet add package Portal
```

## Quick start

```csharp
builder.Services.AddPortal(o =>
{
    o.Title = "Shop API"; o.Versions = [1, 2];
    o.Deprecated[1] = new VersionDeprecation { Sunset = new DateTimeOffset(2027, 1, 31, 0, 0, 0, TimeSpan.Zero), Link = "https://docs.example.com/migrate-v2" };
    o.Map<OutOfStockException>(409, "out-of-stock", "Out of stock.", e => e.Message);      // map exceptions from your domain or other packages
});
builder.Services.AddPortalValidators(typeof(Program).Assembly);                             // FluentValidation validators

var app = builder.Build();
app.UsePortal();                                                                            // correlation id, version check, problem responses
app.MapPortalApi(1, g =>
{
    g.MapPost("/orders", (CreateOrder order) => Results.Created("/v1/orders/1", order));    // validated automatically
    g.MapGet("/orders/{id:int}", (int id) => id == 0 ? throw new NotFoundException("No such order.", "order-not-found") : Results.Ok());
});
app.MapPortalOpenApi();                                                                     // /openapi/v1.json, /openapi/v2.json
```

## Versioning

APIs live under `/v{major}/...`. `MapPortalApi(1, group => ...)` returns a route group that already includes request validation and OpenAPI grouping. A small middleware checks the version segment:
- An unknown version gets a `400 unsupported-api-version` problem that lists what exists.
- Every versioned response carries `api-supported-versions` (and `api-deprecated-versions`).
- **Deprecated** versions keep working but add `Deprecation`, `Sunset` and `Link: rel="deprecation"` headers, and are flagged `deprecated` in their OpenAPI document.

Header and query negotiation and minor versions were rejected for public APIs: one visible major version per URL keeps caching, documentation and support simple.

## Errors

Every error is `application/problem+json` (RFC 9457):

```json
{ "type": "order-not-found", "title": "...", "status": 404, "detail": "...", "traceId": "...", "correlationId": "..." }
```

- Throw `PortalException` (helpers for 400, 401, 403, 404, 409, 422 and 429; 429 sets `Retry-After`) to report a deliberate error. Each carries a stable machine-readable `code` that is used as the problem `type`, so clients switch on the code and not on the message text.
- Framework failures (unknown route, wrong method, unsupported media type, malformed JSON or binding) get the same shape.
- **Validation failures** add an `errors` map with JSON-style field paths: `{ "ship.city": ["City is required."], "lines[1].sku": [...] }`. The status is 400 by default and can be set to 422. A `ValidationException` thrown from a handler is mapped identically.
- **Unexpected exceptions return an opaque 500**: no message, type or stack, only the `traceId` for support, and details are logged server-side. `ExposeExceptionDetails` (development only) changes this. Client disconnects are swallowed, not reported.
- **Correlation ids** are accepted from `X-Correlation-Id` only when short and made of safe characters (otherwise the trace id is used). They are echoed on the response and available from `HttpContext.GetCorrelationId()`.

## Validation

`AddPortalValidators(assembly)` registers your FluentValidation validators. The endpoint filter on `MapPortalApi` groups validates every bound argument that has a validator and short-circuits with a `validation-failed` problem.

## OpenAPI

One document per version at `/openapi/v{n}.json` (optionally behind authorization), built on `Microsoft.AspNetCore.OpenApi`. Each has title, description and version info, a Bearer (JWT) security scheme, shared `ProblemDetails` and `ValidationProblemDetails` schemas, and the standard problem responses (400, 401, 403, 429, 500) added to every operation that does not declare them.

## Left out on purpose

Authentication and authorization (Passport), rate limiting (Gate, whose exceptions map to 429), and pagination and idempotency-key conventions (products define those). Portal only standardizes errors, versions and documentation.

## Configuration

Section `Portal`.

| Option | Default | Meaning |
|---|---|---|
| `Title` / `Description` | `API` / none | OpenAPI document info |
| `Versions` | `[1]` | Supported major versions |
| `Deprecated` | empty | Deprecation details per version |
| `ValidationStatusCode` | `400` | Status for validation failures (400 or 422) |
| `ExposeExceptionDetails` | `false` | Include exception details in 500s (development only) |
| `CorrelationHeader` | `X-Correlation-Id` | Correlation header name |
| `DocumentBearerAuth` | `true` | Document a Bearer security scheme |
| `RequireAuthorizationForOpenApi` | `false` | Require authorization for the OpenAPI endpoints |

## Depends on

Nothing else from this set of packages (uses FluentValidation and Microsoft.AspNetCore.OpenApi).
