string inputDir = DataConverterOptions.InputDirectory(args);
string inputPath = Path.Combine(inputDir, "references.json.gz");
string exactOutputPath = Path.Combine(inputDir, "references.bin");

const int Dims = 14;

Console.WriteLine("Loading references.json.gz...");

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

ExactIndexBuilder.Write(exactOutputPath, vectors, labels, count);
long exactSize = new FileInfo(exactOutputPath).Length;
Console.WriteLine($"Exact output: {exactSize / (1024.0 * 1024.0):F1} MB ({exactSize:N0} bytes)");

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

}
