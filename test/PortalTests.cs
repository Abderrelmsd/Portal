using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Portal.Tests;

public sealed record Address(string? City, string? Zip);
public sealed record CreateOrder(string? Sku, int Qty, Address? Ship, List<OrderLine>? Lines);
public sealed record OrderLine(string? Sku, int Qty);

public sealed class CreateOrderValidator : AbstractValidator<CreateOrder>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().WithMessage("SKU is required.");
        RuleFor(x => x.Qty).InclusiveBetween(1, 100).WithMessage("Quantity must be 1-100.");
        RuleFor(x => x.Ship!.City).NotEmpty().When(x => x.Ship is not null).WithMessage("City is required.");
        RuleForEach(x => x.Lines).ChildRules(l => l.RuleFor(y => y.Sku).NotEmpty().WithMessage("Line SKU is required."));
    }
}

public sealed class OutOfStockException(string sku) : Exception($"'{sku}' is out of stock.");
public sealed class DomainSecretException() : Exception("connection string: Server=prod;Password=hunter2");

public sealed class Api : IAsyncDisposable
{
    public WebApplication App { get; }
    public HttpClient Http { get; }

    private Api(WebApplication app) { App = app; Http = ((TestServer)app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()).CreateClient(); }

    public static async Task<Api> StartAsync(Action<PortalOptions>? configure = null, Action<IServiceCollection>? more = null)
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        b.Logging.ClearProviders();
        b.Services.AddPortal(o =>
        {
            o.Title = "Shop API"; o.Description = "Orders and stuff."; o.Versions = [1, 2];
            o.Map<OutOfStockException>(409, "out-of-stock", "Out of stock.", e => e.Message);
            configure?.Invoke(o);
        });
        b.Services.AddPortalValidators(typeof(Api).Assembly);
        b.Services.AddPortalExceptionMapper<InvalidCastException>(422, "bad-cast", "Bad cast.");
        more?.Invoke(b.Services);

        var app = b.Build();
        app.UsePortal();
        app.MapPortalApi(1, g =>
        {
            g.MapGet("/orders/{id:int}", (int id) => id == 404 ? throw new NotFoundException($"Order {id} does not exist.", "order-not-found") : Results.Ok(new { id }));
            g.MapPost("/orders", (CreateOrder order) => Results.Created($"/v1/orders/1", new { order.Sku }));
            g.MapGet("/stock/{sku}", (string sku) => Throw(new OutOfStockException(sku)));
            g.MapGet("/boom", () => Throw(new DomainSecretException()));
            g.MapGet("/cast", () => Throw(new InvalidCastException("nope")));
            g.MapGet("/limited", () => Throw(new TooManyRequestsException(TimeSpan.FromSeconds(30))));
            g.MapGet("/forbidden", () => Throw(new ForbiddenException()));
            g.MapGet("/conflict", () => Throw(new ConflictException("Already exists.", "duplicate-order")));
            g.MapGet("/whoami", (HttpContext http) => Results.Ok(new { correlation = http.GetCorrelationId() }));
            g.MapGet("/manual-validation", () => Throw(new ValidationException([new FluentValidation.Results.ValidationFailure("Ship.City", "City is required.")])));
        });
        app.MapPortalApi(2, g => g.MapGet("/orders/{id:int}", (int id) => Results.Ok(new { id, version = 2 })));
        app.MapPortalOpenApi();
        await app.StartAsync();
        return new Api(app);
    }

    private static object Throw(Exception e) => throw e;

    public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }

    public static async Task<JsonObject> Body(HttpResponseMessage r) => (JsonObject)JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
}

public class ErrorTests
{
    [Fact]
    public async Task Deliberate_errors_become_problem_documents_with_stable_types_and_ids()
    {
        await using var api = await Api.StartAsync();
        var r = await api.Http.GetAsync("/v1/orders/404");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);
        var p = await Api.Body(r);
        Assert.Equal("order-not-found", (string)p["type"]!);
        Assert.Equal(("The resource was not found.", 404, "Order 404 does not exist.", "/v1/orders/404"), ((string)p["title"]!, (int)p["status"]!, (string)p["detail"]!, (string)p["instance"]!));
        Assert.False(string.IsNullOrEmpty((string)p["traceId"]!));
        Assert.False(string.IsNullOrEmpty((string)p["correlationId"]!));
        Assert.Equal((string)p["correlationId"]!, r.Headers.GetValues("X-Correlation-Id").Single());

        Assert.Equal(HttpStatusCode.OK, (await api.Http.GetAsync("/v1/orders/7")).StatusCode);
    }

    [Fact]
    public async Task The_standard_exception_types_map_to_their_statuses_and_headers()
    {
        await using var api = await Api.StartAsync();
        var limited = await api.Http.GetAsync("/v1/limited");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("30", limited.Headers.GetValues("Retry-After").Single());
        Assert.Equal("rate-limited", (string)(await Api.Body(limited))["type"]!);

        Assert.Equal(HttpStatusCode.Forbidden, (await api.Http.GetAsync("/v1/forbidden")).StatusCode);
        var conflict = await api.Http.GetAsync("/v1/conflict");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("duplicate-order", (string)(await Api.Body(conflict))["type"]!);
    }

    [Fact]
    public async Task Exceptions_from_other_packages_are_mapped_through_registered_mappers()
    {
        await using var api = await Api.StartAsync();
        var oos = await api.Http.GetAsync("/v1/stock/ABC");
        Assert.Equal(HttpStatusCode.Conflict, oos.StatusCode);
        var body = await Api.Body(oos);
        Assert.Equal(("out-of-stock", "'ABC' is out of stock."), ((string)body["type"]!, (string)body["detail"]!));

        var cast = await api.Http.GetAsync("/v1/cast");
        Assert.Equal((HttpStatusCode)422, cast.StatusCode);                                            // registered through AddPortalExceptionMapper<T>
        Assert.Equal("bad-cast", (string)(await Api.Body(cast))["type"]!);
    }

    [Fact]
    public async Task Unexpected_exceptions_return_an_opaque_500_that_leaks_nothing()
    {
        await using var api = await Api.StartAsync();
        var r = await api.Http.GetAsync("/v1/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("DomainSecretException", text);
        Assert.DoesNotContain("   at ", text);
        var p = JsonNode.Parse(text)!;
        Assert.Equal(("An unexpected error occurred.", 500), (((string)p["title"]!), (int)p["status"]!));
        Assert.NotNull(p["traceId"]);                                                                  // …but the id lets support find the log entry
    }

    [Fact]
    public async Task Development_mode_can_expose_exception_details()
    {
        await using var api = await Api.StartAsync(o => o.ExposeExceptionDetails = true);
        var p = await Api.Body(await api.Http.GetAsync("/v1/boom"));
        Assert.Contains("hunter2", (string)p["detail"]!);
        Assert.Equal(typeof(DomainSecretException).FullName, (string)p["exception"]!);
    }

    [Fact]
    public async Task Framework_level_failures_also_get_problem_bodies()
    {
        await using var api = await Api.StartAsync();

        var missing = await api.Http.GetAsync("/v1/nothing-here");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType!.MediaType);
        Assert.Equal("not-found", (string)(await Api.Body(missing))["type"]!);

        var wrongMethod = await api.Http.DeleteAsync("/v1/orders/1");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal("application/problem+json", wrongMethod.Content.Headers.ContentType!.MediaType);

        var badJson = await api.Http.PostAsync("/v1/orders", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);
        Assert.Equal("application/problem+json", badJson.Content.Headers.ContentType!.MediaType);

        var wrongType = await api.Http.PostAsync("/v1/orders", new StringContent("x", System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.Equal("application/problem+json", wrongType.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Correlation_ids_are_honoured_when_safe_and_replaced_when_not()
    {
        await using var api = await Api.StartAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, "/v1/whoami"); req.Headers.Add("X-Correlation-Id", "order-flow-42");
        var r = await api.Http.SendAsync(req);
        Assert.Equal("order-flow-42", r.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("order-flow-42", (string)(await Api.Body(r))["correlation"]!);

        var evil = new HttpRequestMessage(HttpMethod.Get, "/v1/whoami"); evil.Headers.Add("X-Correlation-Id", "x y: injected <script>");
        var safe = (await api.Http.SendAsync(evil)).Headers.GetValues("X-Correlation-Id").Single();
        Assert.DoesNotContain("script", safe);
        Assert.NotEqual("x", safe);
    }
}

public class ValidationTests
{
    private static Task<HttpResponseMessage> Post(Api api, object body) => api.Http.PostAsJsonAsync("/v1/orders", body);

    [Fact]
    public async Task Valid_requests_pass_through_untouched()
    {
        await using var api = await Api.StartAsync();
        var r = await Post(api, new { sku = "A1", qty = 2, ship = new { city = "Paris", zip = "75001" }, lines = new[] { new { sku = "A1", qty = 1 } } });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task Invalid_requests_return_field_level_errors_with_json_style_names()
    {
        await using var api = await Api.StartAsync();
        var r = await Post(api, new { sku = "", qty = 0, ship = new { city = "", zip = "1" }, lines = new object[] { new { sku = "ok", qty = 1 }, new { sku = "", qty = 2 } } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);
        var p = await Api.Body(r);
        Assert.Equal("validation-failed", (string)p["type"]!);
        var errors = (JsonObject)p["errors"]!;
        Assert.Equal(["Line SKU is required."], errors["lines[1].sku"]!.AsArray().Select(x => (string)x!));
        Assert.Equal("SKU is required.", (string)errors["sku"]![0]!);
        Assert.Equal("Quantity must be 1-100.", (string)errors["qty"]![0]!);
        Assert.Equal("City is required.", (string)errors["ship.city"]![0]!);
        Assert.False(string.IsNullOrEmpty((string)p["traceId"]!));
    }

    [Fact]
    public async Task The_validation_status_code_is_configurable_and_manual_validation_exceptions_use_it()
    {
        await using var api = await Api.StartAsync(o => o.ValidationStatusCode = 422);
        Assert.Equal((HttpStatusCode)422, (await Post(api, new { sku = "", qty = 1 })).StatusCode);
        var manual = await api.Http.GetAsync("/v1/manual-validation");
        Assert.Equal((HttpStatusCode)422, manual.StatusCode);
        Assert.Equal("City is required.", (string)(await Api.Body(manual))["errors"]!["ship.city"]![0]!);
    }
}

public class VersioningTests
{
    [Fact]
    public async Task Each_version_serves_its_own_routes_and_reports_supported_versions()
    {
        await using var api = await Api.StartAsync();
        var v1 = await api.Http.GetAsync("/v1/orders/5");
        var v2 = await api.Http.GetAsync("/v2/orders/5");
        Assert.Equal(2, (int)(await Api.Body(v2))["version"]!);
        Assert.Null((await Api.Body(v1))["version"]);
        Assert.Equal("1, 2", v1.Headers.GetValues("api-supported-versions").Single());
        Assert.False(v1.Headers.Contains("Deprecation"));
    }

    [Fact]
    public async Task Unknown_versions_are_a_400_problem_listing_what_is_available()
    {
        await using var api = await Api.StartAsync();
        var r = await api.Http.GetAsync("/v9/orders/5");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var p = await Api.Body(r);
        Assert.Equal("unsupported-api-version", (string)p["type"]!);
        Assert.Equal([1, 2], p["supportedVersions"]!.AsArray().Select(x => (int)x!));
        Assert.Equal("1, 2", r.Headers.GetValues("api-supported-versions").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await api.Http.GetAsync("/vx/orders/5")).StatusCode);      // not a version segment at all: an ordinary unknown route
    }

    [Fact]
    public async Task Deprecated_versions_keep_working_but_announce_their_sunset()
    {
        var sunset = new DateTimeOffset(2027, 1, 31, 0, 0, 0, TimeSpan.Zero);
        await using var api = await Api.StartAsync(o => o.Deprecated[1] = new VersionDeprecation { Sunset = sunset, Link = "https://docs.example.com/migrate-v2" });
        var r = await api.Http.GetAsync("/v1/orders/5");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("true", r.Headers.GetValues("Deprecation").Single());
        Assert.Equal("Sun, 31 Jan 2027 00:00:00 GMT", r.Headers.GetValues("Sunset").Single());
        Assert.Contains("rel=\"deprecation\"", r.Headers.GetValues("Link").Single());
        Assert.Equal("2", r.Headers.GetValues("api-supported-versions").Single());                        // supported list excludes deprecated ones
        Assert.Equal("1", r.Headers.GetValues("api-deprecated-versions").Single());
        Assert.False((await api.Http.GetAsync("/v2/orders/5")).Headers.Contains("Deprecation"));
    }

    [Fact]
    public void Options_are_validated_up_front()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddPortal(o => o.Versions = []));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddPortal(o => { o.Versions = [1]; o.Deprecated[2] = new VersionDeprecation(); }));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddPortal(o => o.ValidationStatusCode = 418));
    }
}

public class OpenApiTests
{
    [Fact]
    public async Task Each_version_has_its_own_document_with_its_own_routes_info_and_security()
    {
        await using var api = await Api.StartAsync();
        var v1 = await Api.Body(await api.Http.GetAsync("/openapi/v1.json"));
        var v2 = await Api.Body(await api.Http.GetAsync("/openapi/v2.json"));

        Assert.Equal(("Shop API", "1.0", "Orders and stuff."), ((string)v1["info"]!["title"]!, (string)v1["info"]!["version"]!, (string)v1["info"]!["description"]!));
        Assert.Equal("2.0", (string)v2["info"]!["version"]!);
        var paths1 = ((JsonObject)v1["paths"]!).Select(p => p.Key).ToList();
        Assert.Contains("/v1/orders/{id}", paths1); Assert.Contains("/v1/orders", paths1);
        Assert.DoesNotContain(paths1, p => p.StartsWith("/v2"));                                       // documents are per version
        Assert.Equal(["/v2/orders/{id}"], ((JsonObject)v2["paths"]!).Select(p => p.Key));

        var scheme = v1["components"]!["securitySchemes"]!["Bearer"]!;
        Assert.Equal(("http", "bearer", "JWT"), ((string)scheme["type"]!, (string)scheme["scheme"]!, (string)scheme["bearerFormat"]!));
        Assert.NotNull(v1["security"]);
        Assert.NotNull(v1["components"]!["schemas"]!["ProblemDetails"]);
        Assert.NotNull(v1["components"]!["schemas"]!["ValidationProblemDetails"]);
    }

    [Fact]
    public async Task Operations_document_the_standard_problem_responses()
    {
        await using var api = await Api.StartAsync();
        var doc = await Api.Body(await api.Http.GetAsync("/openapi/v1.json"));
        var post = doc["paths"]!["/v1/orders"]!["post"]!["responses"]!;
        foreach (var status in new[] { "400", "401", "403", "429", "500" })
            Assert.NotNull(post[status]!["content"]!["application/problem+json"]);
        Assert.EndsWith("ValidationProblemDetails", (string)post["400"]!["content"]!["application/problem+json"]!["schema"]!["$ref"]!);
        Assert.EndsWith("ProblemDetails", (string)post["500"]!["content"]!["application/problem+json"]!["schema"]!["$ref"]!);
    }

    [Fact]
    public async Task Deprecated_versions_mark_every_operation_deprecated_and_bearer_docs_can_be_switched_off()
    {
        await using var api = await Api.StartAsync(o => { o.Deprecated[1] = new VersionDeprecation(); o.DocumentBearerAuth = false; });
        var doc = await Api.Body(await api.Http.GetAsync("/openapi/v1.json"));
        Assert.True((bool)doc["paths"]!["/v1/orders/{id}"]!["get"]!["deprecated"]!);
        Assert.Null(doc["components"]!["securitySchemes"]);
        Assert.Null(doc["paths"]!["/v1/orders"]!["post"]!["responses"]!["401"]);
        var v2 = await Api.Body(await api.Http.GetAsync("/openapi/v2.json"));
        Assert.Null(v2["paths"]!["/v2/orders/{id}"]!["get"]!["deprecated"]);
    }
}
