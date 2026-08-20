namespace Portal;

public sealed class VersionDeprecation
{
    /// <summary>When the version stops working (sent as the <c>Sunset</c> header).</summary>
    public DateTimeOffset? Sunset { get; init; }
    /// <summary>Migration guide (sent as <c>Link: &lt;url&gt;; rel="deprecation"</c>).</summary>
    public string? Link { get; init; }
}

public sealed class PortalOptions
{
    public const string SectionName = "Portal";
    public string Title { get; set; } = "API";
    public string? Description { get; set; }
    /// <summary>Major versions the API serves under <c>/v{n}/…</c>.</summary>
    public List<int> Versions { get; set; } = [1];
    /// <summary>Versions still served but deprecated (must also be in <see cref="Versions"/>).</summary>
    public Dictionary<int, VersionDeprecation> Deprecated { get; set; } = [];
    /// <summary>Status for FluentValidation failures (400 by default; 422 if you prefer to reserve 400 for malformed requests).</summary>
    public int ValidationStatusCode { get; set; } = 400;
    /// <summary>Include exception messages and types in 500 responses. Development only.</summary>
    public bool ExposeExceptionDetails { get; set; }
    public string CorrelationHeader { get; set; } = "X-Correlation-Id";
    /// <summary>Document a Bearer (JWT) security scheme in OpenAPI.</summary>
    public bool DocumentBearerAuth { get; set; } = true;
    /// <summary>Require authorization for the OpenAPI endpoints (recommended for non-public APIs).</summary>
    public bool RequireAuthorizationForOpenApi { get; set; }

    internal List<IPortalExceptionMapper> Mappers { get; } = [];

    /// <summary>Maps an exception type to a problem.</summary>
    public PortalOptions Map<T>(int status, string code, string title, Func<T, string?>? detail = null) where T : Exception
    {
        Mappers.Add(new DelegateMapper<T>(e => new PortalException(status, code, title, detail?.Invoke(e))));
        return this;
    }

    public PortalOptions Map<T>(Func<T, PortalException> map) where T : Exception
    {
        Mappers.Add(new DelegateMapper<T>(map));
        return this;
    }

    internal IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Versions.Count == 0 || Versions.Any(v => v < 1) || Versions.Distinct().Count() != Versions.Count) errors.Add("Versions must be a non-empty list of distinct major versions >= 1.");
        foreach (var d in Deprecated.Keys.Where(d => !Versions.Contains(d))) errors.Add($"Deprecated version {d} is not in Versions.");
        if (ValidationStatusCode is not (400 or 422)) errors.Add("ValidationStatusCode must be 400 or 422.");
        return errors;
    }
}
