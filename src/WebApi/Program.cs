string? socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH");

// Default mode is the O(1) fine-bucket classifier. Exact modes are kept for
// validation and experiments, but they are too expensive for the target load.
bool exactSearch = string.Equals(Environment.GetEnvironmentVariable("SEARCH_MODE"), "exact", StringComparison.OrdinalIgnoreCase);
bool avxSearch = string.Equals(Environment.GetEnvironmentVariable("SEARCH_MODE"), "avx2", StringComparison.OrdinalIgnoreCase);

string dataPath = Environment.GetEnvironmentVariable("DATA_PATH") ?? "/data/references.bin";
var scorer = FraudScorer.Load(dataPath, exactSearch, avxSearch);
var server = new RawHttpServer(socketPath, scorer);

await server.RunAsync();
