string? socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH");
string dataPath = Environment.GetEnvironmentVariable("DATA_PATH") ?? "/data/references.bin";
var scorer = FraudScorer.Load(dataPath);
var server = new RawHttpServer(socketPath, scorer);

await server.RunAsync();
