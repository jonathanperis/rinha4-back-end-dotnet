string? socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH");

// Default mode is the O(1) fine-bucket classifier. Exact modes bypass that
// shortcut and search the reference vectors for the real top-five neighbors.
string? searchMode = Environment.GetEnvironmentVariable("SEARCH_MODE");
bool avxSearch = string.Equals(searchMode, "avx2", StringComparison.OrdinalIgnoreCase);
bool exactSearch = avxSearch || string.Equals(searchMode, "exact", StringComparison.OrdinalIgnoreCase);

string dataPath = Environment.GetEnvironmentVariable("DATA_PATH") ?? "/data/references.bin";
var scorer = FraudScorer.Load(dataPath, exactSearch, avxSearch);
var server = new RawHttpServer(socketPath, scorer);

await server.RunAsync();
