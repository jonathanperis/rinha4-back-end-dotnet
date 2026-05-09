string? socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH");
string dataDirectory = Environment.GetEnvironmentVariable("DATA_DIR") ?? "/data";
SetMinWorkerThreads();
var scorer = FraudScorer.Load(dataDirectory);
scorer.WarmUp();
var server = new RawHttpServer(socketPath, scorer);

await server.RunAsync();

static void SetMinWorkerThreads()
{
    int target = GetPositiveIntEnvironment("MIN_WORKER_THREADS", 128);
    ThreadPool.GetMinThreads(out int workerThreads, out int completionPortThreads);
    if (workerThreads < target)
        ThreadPool.SetMinThreads(target, completionPortThreads);
}

static int GetPositiveIntEnvironment(string name, int fallback)
{
    string? value = Environment.GetEnvironmentVariable(name);
    return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
}
