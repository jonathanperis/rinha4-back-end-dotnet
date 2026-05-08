string inputDir = DataConverterOptions.InputDirectory(args);
string inputPath = Path.Combine(inputDir, "references.json.gz");
string ivfOutputPath = Path.Combine(inputDir, "references.ivf.bin");
IvfBuildOptions ivfOptions = DataConverterOptions.IvfOptions();

const int Dims = 14;

Console.WriteLine("Loading references.json.gz...");
Console.WriteLine($"IVF build: clusters={ivfOptions.Clusters}, train_sample={ivfOptions.TrainSample}, iterations={ivfOptions.Iterations}");

int count;
float[] vectors;
byte[] labels;
int row = 0;

// Single pass loads only the official vectors and labels needed to build IVF.
using (var fs = File.OpenRead(inputPath))
using (var gz = new GZipStream(fs, CompressionMode.Decompress))
using (var doc = JsonDocument.Parse(gz))
{
    var root = doc.RootElement;
    count = root.GetArrayLength();
    Console.WriteLine($"Found {count:N0} vectors");

    vectors = GC.AllocateUninitializedArray<float>(count * Dims);
    labels = GC.AllocateUninitializedArray<byte>(count);

    foreach (var el in root.EnumerateArray())
    {
        var v = el.GetProperty("vector");
        bool isFraud = el.GetProperty("label").GetString() == "fraud";

        int vectorBase = row * Dims;
        for (int dim = 0; dim < Dims; dim++)
            vectors[vectorBase + dim] = v[dim].GetSingle();

        labels[row] = isFraud ? (byte)1 : (byte)0;
        row++;
    }
}

IvfIndexBuilder.Write(ivfOutputPath, vectors, labels, count, ivfOptions);
long ivfSize = new FileInfo(ivfOutputPath).Length;
Console.WriteLine($"IVF output: {ivfSize / (1024.0 * 1024.0):F1} MB ({ivfSize:N0} bytes)");

/// <summary>
/// Reads converter command-line and environment options.
/// </summary>
/// <remarks>
/// The converter always writes the production rounded int16 IVF index.
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
    /// Reads IVF build parameters from environment variables.
    /// </summary>
    /// <returns>Configured IVF build options with conservative defaults.</returns>
    public static IvfBuildOptions IvfOptions() => new(
        EnvInt("IVF_CLUSTERS", 2048),
        EnvInt("IVF_TRAIN_SAMPLE", 65_536),
        EnvInt("IVF_ITERATIONS", 6));

    /// <summary>
    /// Reads an integer environment variable with fallback.
    /// </summary>
    /// <param name="name">Environment variable name.</param>
    /// <param name="fallback">Value returned when the variable is missing or invalid.</param>
    /// <returns>The parsed positive value, or <paramref name="fallback"/>.</returns>
    private static int EnvInt(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : fallback;
    }
}
