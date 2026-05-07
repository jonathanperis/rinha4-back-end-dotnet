string inputDir = args.Length > 0 ? args[0] : "/data";
string inputPath = Path.Combine(inputDir, "references.json.gz");
string outputPath = Path.Combine(inputDir, "references.bin");

const int Magic = 0x37444852; // RHD7
const short Scale = 10000;
const int Dims = 14;
const int PaddedDims = 16; // fixed converter/API binary vector stride
const int GroupCount = FraudVectorizer.FineGroupCount;

Console.WriteLine("Loading references.json.gz...");

int count;
var groupCounts = new int[GroupCount];
var groupFrauds = new int[GroupCount];

// Single pass counts bucket sizes and fraud labels. The API only needs the
// final majority response table at runtime, so raw labels stay out of the image.
using (var fs = File.OpenRead(inputPath))
using (var gz = new GZipStream(fs, CompressionMode.Decompress))
using (var doc = JsonDocument.Parse(gz))
{
    var root = doc.RootElement;
    count = root.GetArrayLength();
    Console.WriteLine($"Found {count:N0} vectors");

    foreach (var el in root.EnumerateArray())
    {
        var v = el.GetProperty("vector");
        int group = DataConverterVectorGroups.VectorGroup(v);
        groupCounts[group]++;
        if (el.GetProperty("label").GetString() == "fraud")
            groupFrauds[group]++;
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
