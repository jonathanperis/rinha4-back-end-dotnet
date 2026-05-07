string inputDir = DataConverterOptions.InputDirectory(args);
string inputPath = Path.Combine(inputDir, "references.json.gz");
string outputPath = Path.Combine(inputDir, "references.bin");
string ivfOutputPath = Path.Combine(inputDir, "references.ivf.bin");
string exactOutputPath = Path.Combine(inputDir, "references.exact.bin");
bool buildIvf = DataConverterOptions.BuildIvf(args);
IvfBuildOptions ivfOptions = DataConverterOptions.IvfOptions();

const int Magic = 0x37444852; // RHD7
const short Scale = 10000;
const int Dims = 14;
const int PaddedDims = 16; // fixed converter/API binary vector stride
const int GroupCount = FraudVectorizer.FineGroupCount;

Console.WriteLine("Loading references.json.gz...");

int count;
var groupCounts = new int[GroupCount];
var groupFrauds = new int[GroupCount];
float[]? ivfVectors = null;
byte[]? ivfLabels = null;
int ivfRow = 0;

if (buildIvf)
    Console.WriteLine($"IVF build enabled: clusters={ivfOptions.Clusters}, train_sample={ivfOptions.TrainSample}, iterations={ivfOptions.Iterations}");

// Single pass counts bucket sizes and fraud labels. The API only needs the
// final majority response table at runtime, so raw labels stay out of the image.
using (var fs = File.OpenRead(inputPath))
using (var gz = new GZipStream(fs, CompressionMode.Decompress))
using (var doc = JsonDocument.Parse(gz))
{
    var root = doc.RootElement;
    count = root.GetArrayLength();
    Console.WriteLine($"Found {count:N0} vectors");

    if (buildIvf)
    {
        ivfVectors = GC.AllocateUninitializedArray<float>(count * Dims);
        ivfLabels = GC.AllocateUninitializedArray<byte>(count);
    }

    foreach (var el in root.EnumerateArray())
    {
        var v = el.GetProperty("vector");
        int group = DataConverterVectorGroups.VectorGroup(v);
        bool isFraud = el.GetProperty("label").GetString() == "fraud";
        groupCounts[group]++;
        if (isFraud)
            groupFrauds[group]++;

        if (buildIvf)
        {
            int vectorBase = ivfRow * Dims;
            for (int dim = 0; dim < Dims; dim++)
                ivfVectors![vectorBase + dim] = v[dim].GetSingle();

            ivfLabels![ivfRow] = isFraud ? (byte)1 : (byte)0;
            ivfRow++;
        }
    }
}

// Write binary format:
// [int32 magic][int32 count][int32 dims][int32 padded_dims][int32 scale]
// [FineGroupCount * byte response_indexes]
var responseIndexes = GC.AllocateUninitializedArray<byte>(GroupCount);
for (int group = 0; group < GroupCount; group++)
{
    int total = groupCounts[group];
    responseIndexes[group] = total == 0 ? (byte)0 : (byte)((groupFrauds[group] * 5 + total / 2) / total);
}

using var outStream = File.Create(outputPath);
using var writer = new BinaryWriter(outStream);

writer.Write(Magic);
writer.Write(count);
writer.Write(Dims);
writer.Write(PaddedDims);
writer.Write((int)Scale);

Console.WriteLine("Writing bucket response indexes...");
writer.Write(responseIndexes);

writer.Flush();

long size = new FileInfo(outputPath).Length;
Console.WriteLine($"Done. Output: {size / (1024.0 * 1024.0):F1} MB ({size:N0} bytes)");
Console.WriteLine($"Format: {count} vectors, {Dims} dims (padded to {PaddedDims}), scale {Scale}");

if (buildIvf)
{
    IvfIndexBuilder.Write(ivfOutputPath, ivfVectors!, ivfLabels!, count, ivfOptions);
    IvfIndexBuilder.WriteExact(exactOutputPath, ivfVectors!, count);
    long ivfSize = new FileInfo(ivfOutputPath).Length;
    long exactSize = new FileInfo(exactOutputPath).Length;
    Console.WriteLine($"IVF output: {ivfSize / (1024.0 * 1024.0):F1} MB ({ivfSize:N0} bytes)");
    Console.WriteLine($"Exact rerank output: {exactSize / (1024.0 * 1024.0):F1} MB ({exactSize:N0} bytes)");
}

/// <summary>
/// Reads converter command-line and environment options.
/// </summary>
/// <remarks>
/// The default converter output stays the compact bucket table. The IVF file is
/// opt-in so the production image can keep the small, known-good data path.
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
    /// Determines whether the converter should write the experimental IVF index.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the converter.</param>
    /// <returns><see langword="true"/> when <c>--ivf</c> or <c>BUILD_IVF=true</c> is set.</returns>
    public static bool BuildIvf(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, "--ivf", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        string? value = Environment.GetEnvironmentVariable("BUILD_IVF");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
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

/// <summary>
/// Converter-only helpers for deriving binary dataset grouping metadata.
/// </summary>
/// <remarks>
/// The helper lives in a real type so XML documentation comments are valid C#
/// documentation comments instead of comments on top-level local functions.
/// </remarks>
internal static class DataConverterVectorGroups
{
    private const short Scale = 10000;

    /// <summary>
    /// Computes the fine-bucket id for one normalized reference vector.
    /// </summary>
    /// <param name="vector">JSON vector array from <c>references.json.gz</c>.</param>
    /// <returns>
    /// The same fine-bucket id produced by <see cref="FraudVectorizer.FineVectorGroup"/>
    /// in the API, used to precompute bucket response indexes inside <c>references.bin</c>.
    /// </returns>
    /// <remarks>
    /// Converter and API must use the exact same bucket key; otherwise startup
    /// majority indexes would point at the wrong vector ranges.
    /// </remarks>
    public static int VectorGroup(JsonElement vector)
    {
        int minutesSinceLast = (short)(vector[5].GetSingle() * Scale);
        int kmFromLast = (short)(vector[6].GetSingle() * Scale);
        int amount = (short)(vector[0].GetSingle() * Scale);
        int kmFromHome = (short)(vector[7].GetSingle() * Scale);
        int isOnline = (short)(vector[9].GetSingle() * Scale);
        int cardPresent = (short)(vector[10].GetSingle() * Scale);
        int unknownMerchant = (short)(vector[11].GetSingle() * Scale);
        return FraudVectorizer.FineVectorGroup(minutesSinceLast, kmFromLast, amount, kmFromHome, isOnline, cardPresent, unknownMerchant, Scale);
    }
}
