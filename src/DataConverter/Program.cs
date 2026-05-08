string inputDir = DataConverterOptions.InputDirectory(args);
string inputPath = Path.Combine(inputDir, "references.json.gz");
string exactOutputPath = Path.Combine(inputDir, "references.bin");
int exactMaxRefs = DataConverterOptions.ExactMaxRefs();

const int Dims = 14;

Console.WriteLine("Loading references.json.gz...");

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
        return int.TryParse(value, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
    }

}
