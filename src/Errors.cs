namespace Portal;

/// <summary>An error the API deliberately reports. Throw one from a handler; Portal turns it into an RFC 9457 problem response with the right status and a stable <see cref="Code"/>.</summary>
public class PortalException(int status, string code, string title, string? detail = null, Exception? inner = null) : Exception(detail ?? title, inner)
{
    public int Status { get; } = status;
    /// <summary>Stable, machine-readable slug (becomes the problem <c>type</c> URI suffix), e.g. <c>order-not-found</c>.</summary>
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    /// <summary>Extra fields merged into the problem document (never put secrets here).</summary>
    public IDictionary<string, object?> Extensions { get; } = new Dictionary<string, object?>();
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
}

public sealed class BadRequestException(string detail, string code = "bad-request") : PortalException(400, code, "The request is invalid.", detail);
public sealed class UnauthorizedException(string detail = "Authentication is required.", string code = "unauthorized") : PortalException(401, code, "Authentication required.", detail);
public sealed class ForbiddenException(string detail = "You don't have permission to do that.", string code = "forbidden") : PortalException(403, code, "Forbidden.", detail);
public sealed class NotFoundException(string detail, string code = "not-found") : PortalException(404, code, "The resource was not found.", detail);
public sealed class ConflictException(string detail, string code = "conflict") : PortalException(409, code, "The request conflicts with the current state.", detail);
public sealed class UnprocessableException(string detail, string code = "unprocessable") : PortalException(422, code, "The request could not be processed.", detail);
public sealed class TooManyRequestsException : PortalException
{
    public TooManyRequestsException(TimeSpan retryAfter, string detail = "Too many requests.", string code = "rate-limited") : base(429, code, "Too many requests.", detail)
        => Headers["Retry-After"] = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Maps exceptions from other packages (or your own) to problems without those packages needing to know about Portal.</summary>
public interface IPortalExceptionMapper
{
    /// <summary>Return null to leave the exception to later mappers / the 500 default.</summary>
    PortalException? Map(Exception exception);
}

internal sealed class DelegateMapper<T>(Func<T, PortalException> map) : IPortalExceptionMapper where T : Exception
{
    public PortalException? Map(Exception e) => e is T t ? map(t) : null;
}
