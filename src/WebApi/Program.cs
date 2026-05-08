string? socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH");
string dataDirectory = Environment.GetEnvironmentVariable("DATA_DIR") ?? "/data";
var scorer = FraudScorer.Load(dataDirectory);
var server = new RawHttpServer(socketPath, scorer);

await server.RunAsync();
