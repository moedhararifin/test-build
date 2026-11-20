using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Dollhouse.Core;
using Dollhouse.Core.Security;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server;

public static class ServerApplication
{
    public static WebApplication Build(WorkspacePaths paths, int port = 7900, IRequestJournal? journal = null,
        IRequestCapture? capture = null, IEnumerable<ServerRoute>? routes = null, IEnumerable<IServerModule>? modules = null,
        Action<string>? status = null)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var pipeline = new ServerPipeline(journal ?? new ConsoleRequestJournal(),
            capture ?? new NoRequestCapture(), routes, modules);
        var certificate = LocalCertificate.Load(paths);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = paths.Root,
            ApplicationName = typeof(ServerApplication).Assembly.FullName
        });
        // No environment/appsettings listener can widen this research server's binding.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders(); // Avoid framework URL/body logging outside our redactor.
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = RequestDispatcher.MaximumBodySize;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            server.Listen(IPAddress.Loopback, port, listener =>
            {
                listener.Protocols = HttpProtocols.Http1;
                listener.UseHttps(https =>
                {
                    https.ServerCertificate = certificate;
                    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    https.HandshakeTimeout = TimeSpan.FromSeconds(10);
                });
            });
        });
        builder.Services.AddSingleton(pipeline);
        var application = builder.Build();
        application.Lifetime.ApplicationStopped.Register(certificate.Dispose);
        application.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        application.Run(RequestDispatcher.HandleAsync);
        application.Lifetime.ApplicationStarted.Register(() =>
            (status ?? Console.WriteLine)($"Listening on {string.Join(", ", application.Urls)}"));
        return application;
    }
}
