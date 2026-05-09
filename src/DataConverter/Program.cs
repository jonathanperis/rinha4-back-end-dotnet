string inputDir = DataConverterOptions.InputDirectory(args);
string inputPath = Path.Combine(inputDir, "references.json.gz");
string exactOutputPath = Path.Combine(inputDir, "references.bin");
string ivfOutputPath = Path.Combine(inputDir, "references.ivf.bin");
string bucketOutputPath = Path.Combine(inputDir, "references.bucket.bin");
int exactMaxRefs = DataConverterOptions.ExactMaxRefs();
IvfBuildOptions ivfOptions = DataConverterOptions.IvfOptions();
BucketBuildOptions bucketOptions = DataConverterOptions.BucketOptions();

const int Dims = 14;

Console.WriteLine("Loading references.json.gz...");
Console.WriteLine($"IVF build: clusters={ivfOptions.Clusters}, train_sample={ivfOptions.TrainSample}, iterations={ivfOptions.Iterations}, scale={ivfOptions.Scale}");

int count;
double[] vectors;
byte[] labels;
int row = 0;

// Single pass loads vectors and labels needed for Zan-style exact-reference storage.
using (var fs = File.OpenRead(inputPath))
using (var gz = new GZipStream(fs, CompressionMode.Decompress))
using (var doc = JsonDocument.Parse(gz))
{
    var root = doc.RootElement;
    count = root.GetArrayLength();
    Console.WriteLine($"Found {count:N0} vectors");

    vectors = GC.AllocateUninitializedArray<double>(count * Dims);
    labels = GC.AllocateUninitializedArray<byte>(count);

    foreach (var el in root.EnumerateArray())
    {
        var v = el.GetProperty("vector");
        bool isFraud = el.GetProperty("label").GetString() == "fraud";

        int vectorBase = row * Dims;
        for (int dim = 0; dim < Dims; dim++)
            vectors[vectorBase + dim] = v[dim].GetDouble();

        labels[row] = isFraud ? (byte)1 : (byte)0;
        row++;
    }
}

ExactIndexBuilder.Write(exactOutputPath, vectors, labels, count, exactMaxRefs);
long exactSize = new FileInfo(exactOutputPath).Length;
Console.WriteLine($"Exact output: {exactSize / (1024.0 * 1024.0):F1} MB ({exactSize:N0} bytes)");

float[] floatVectors = GC.AllocateUninitializedArray<float>(vectors.Length);
for (int i = 0; i < vectors.Length; i++)
    floatVectors[i] = (float)vectors[i];

IvfIndexBuilder.Write(ivfOutputPath, floatVectors, labels, count, ivfOptions);
long ivfSize = new FileInfo(ivfOutputPath).Length;
Console.WriteLine($"IVF output: {ivfSize / (1024.0 * 1024.0):F1} MB ({ivfSize:N0} bytes)");

BucketIndexBuilder.Write(bucketOutputPath, floatVectors, labels, count, bucketOptions);
long bucketSize = new FileInfo(bucketOutputPath).Length;
Console.WriteLine($"Bucket output: {bucketSize / (1024.0 * 1024.0):F1} MB ({bucketSize:N0} bytes)");

/// <summary>
/// Reads converter command-line and environment options.
/// </summary>
/// <remarks>
/// The converter writes Zan-style rounded int16 exact-reference storage.
/// </remarks>
internal static class DataConverterOptions
{
    /// <summary>
    /// Resolves the input directory from positional arguments.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the converter.</param>
    /// <returns>The directory containing Rinha resource files.</returns>
    public static string InputDirectory(string[] args)
    {
        foreach (string arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                return arg;
        }

        return "/data";
    }

    /// <summary>
    /// Reads maximum exact-reference count. Zero or negative means all rows.
    /// </summary>
    public static int ExactMaxRefs()
    {
        string? value = Environment.GetEnvironmentVariable("EXACT_MAX_REFS");
        return int.TryParse(value, CultureInfo.InvariantCulture, out int parsed) ? parsed : 100_000;
    }

    /// <summary>
    /// Reads IVF build parameters from environment variables.
    /// </summary>
    public static IvfBuildOptions IvfOptions() => new(
        EnvInt("IVF_CLUSTERS", 4096),
        EnvInt("IVF_TRAIN_SAMPLE", 65_536),
        EnvInt("IVF_ITERATIONS", 6),
        Math.Min(EnvInt("IVF_SCALE", IvfIndexBuilder.DefaultScale), short.MaxValue));

    /// <summary>
    /// Reads bucket-index build parameters from environment variables.
    /// </summary>
    public static BucketBuildOptions BucketOptions() => new(
        Math.Min(EnvInt("BUCKET_SCALE", EnvInt("IVF_SCALE", IvfIndexBuilder.DefaultScale)), short.MaxValue));

    private static int EnvInt(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : fallback;
    }

}
