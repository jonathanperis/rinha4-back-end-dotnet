using System.Net;
using System.Net.Sockets;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddHttpForwarder();
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.ListenAnyIP(9999);
});

var app = builder.Build();

using var httpClient = new HttpMessageInvoker(new SocketsHttpHandler
{
    UseCookies = false,
    UseProxy = false,
    AutomaticDecompression = DecompressionMethods.None,
    MaxConnectionsPerServer = 4096,
    PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
    PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
    ConnectCallback = static async (context, cancellationToken) =>
    {
        string path = context.DnsEndPoint.Host == "api1" ? "/sockets/api1.sock" : "/sockets/api2.sock";
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
});

var requestConfig = new ForwarderRequestConfig
{
    ActivityTimeout = TimeSpan.FromSeconds(5)
};

long next = 0;
app.Run(async context =>
{
    string destinationPrefix = (Interlocked.Increment(ref next) & 1) == 0 ? "http://api1/" : "http://api2/";
    var forwarder = context.RequestServices.GetRequiredService<IHttpForwarder>();
    ForwarderError error = await forwarder.SendAsync(context, destinationPrefix, httpClient, requestConfig);
    if (error != ForwarderError.None && !context.Response.HasStarted)
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
});

app.Run();
