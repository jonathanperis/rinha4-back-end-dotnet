string? socketPath = Environment.GetEnvironmentVariable("BIND_ADDR") ?? Environment.GetEnvironmentVariable("SOCKET_PATH");
string dataDirectory = Environment.GetEnvironmentVariable("DATA_DIR") ?? "/data";
SetMinWorkerThreads();
var scorer = FraudScorer.Load(dataDirectory);
var server = new RawHttpServer(socketPath, scorer);

await server.RunAsync();

static void SetMinWorkerThreads()
{
    int target = GetPositiveIntEnvironment("MIN_WORKER_THREADS", 128);
    ThreadPool.GetMinThreads(out int workerThreads, out int completionPortThreads);
    if (workerThreads < target)
        ThreadPool.SetMinThreads(target, completionPortThreads);

    int maxWorkers = GetPositiveIntEnvironment("MAX_WORKER_THREADS", 0);
    int maxIo = GetPositiveIntEnvironment("MAX_IO_THREADS", 0);
    if (maxWorkers > 0 || maxIo > 0)
    {
        ThreadPool.GetMaxThreads(out int currentMaxWorkers, out int currentMaxIo);
        ThreadPool.SetMaxThreads(
            maxWorkers > 0 ? maxWorkers : currentMaxWorkers,
            maxIo > 0 ? maxIo : currentMaxIo);
    }
}

static int GetPositiveIntEnvironment(string name, int fallback)
{
    string? value = Environment.GetEnvironmentVariable(name);
    return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
}
