using Dollhouse.Server.Requests;
using Microsoft.AspNetCore.Routing;

namespace Dollhouse.Server.Modules;

public sealed record ServerRoute(string Method, string Path, ServerModuleDelegate HandleAsync);

/// <summary>Exact routes take precedence over routes with named path segments.</summary>
public sealed class RequestHandlersModule : IServerModule
{
    private readonly Dictionary<(string Method, string Path), ServerRoute> routes = [];
    private readonly List<(ServerRoute Route, string[] Segments)> parameterRoutes = [];

    public RequestHandlersModule(IEnumerable<ServerRoute> routes)
    {
        foreach (var route in routes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(route.Method);
            ArgumentException.ThrowIfNullOrWhiteSpace(route.Path);
            ArgumentNullException.ThrowIfNull(route.HandleAsync);
            if (!route.Path.StartsWith('/') || route.Path.Contains('?') || route.Path.Contains('#'))
                throw new ArgumentException("Route paths must start with / and contain no query or fragment.", nameof(routes));
            if (!this.routes.TryAdd((route.Method.ToUpperInvariant(), route.Path), route))
                throw new ArgumentException($"Duplicate route: {route.Method} {route.Path}", nameof(routes));
            if (route.Path.Contains('{') || route.Path.Contains('}'))
            {
                var segments = route.Path.Split('/');
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var segment in segments.Where(segment => segment.Contains('{') || segment.Contains('}')))
                    if (segment.Length < 3 || segment[0] != '{' || segment[^1] != '}'
                        || !segment[1..^1].All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                        || !names.Add(segment[1..^1]))
                        throw new ArgumentException("Route parameters must be unique named path segments.", nameof(routes));
                parameterRoutes.Add((route, segments));
            }
        }
    }

    public ServerRoute? Match(HttpContext context)
    {
        var method = context.Request.Method.ToUpperInvariant();
        var path = context.Request.Path.Value ?? "/";
        var exact = routes.GetValueOrDefault((method, path));
        if (exact is not null && !exact.Path.Contains('{')) return exact;
        var segments = path.Split('/');
        foreach (var candidate in parameterRoutes)
        {
            if (!candidate.Route.Method.Equals(method, StringComparison.OrdinalIgnoreCase) || candidate.Segments.Length != segments.Length) continue;
            var values = MatchSegments(candidate.Segments, segments);
            if (values is null) continue;
            context.Request.RouteValues = values;
            return candidate.Route;
        }
        return null;
    }

    private static RouteValueDictionary? MatchSegments(string[] pattern, string[] segments)
    {
        var values = new RouteValueDictionary();
        for (var index = 0; index < segments.Length; index++)
        {
            if (pattern[index].StartsWith('{'))
            {
                if (segments[index].Length == 0) return null;
                values.Add(pattern[index][1..^1], segments[index]);
            }
            else if (pattern[index] != segments[index]) return null;
        }
        return values;
    }

    public Task InvokeAsync(ServerRequest request, ServerModuleDelegate next, CancellationToken cancellationToken) =>
        request.SelectedRoute is { } route
            ? route.HandleAsync(request, cancellationToken)
            : next(request, cancellationToken);
}
