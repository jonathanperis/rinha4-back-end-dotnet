using System.IO.Compression;
using System.Text.Json;

string inputDir = args.Length > 0 ? args[0] : "/data";
string inputPath = Path.Combine(inputDir, "references.json.gz");
string outputPath = Path.Combine(inputDir, "references.bin");

const int Magic = 0x35444852; // RHD5
const short Scale = 10000;
const int Dims = 14;
const int PaddedDims = 16; // AVX2 alignment
const int GroupCount = FraudVectorizer.FineGroupCount;

Console.WriteLine("Loading references.json.gz...");

int count;
var groupCounts = new int[GroupCount];

// First pass counts bucket sizes. That lets us compute exact offsets and write
// vectors already grouped without per-bucket lists.
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
    }
}

// Write binary format:
// [int32 magic][int32 count][int32 dims][int32 padded_dims][int32 scale]
// [(FineGroupCount + 1) * int32 group_offsets]
// [count * padded_dims * int16 vectors]
// [count * byte labels]
var groupOffsets = new int[GroupCount + 1];
for (int i = 0; i < GroupCount; i++)
    groupOffsets[i + 1] = groupOffsets[i] + groupCounts[i];

var nextGroupIndex = new int[GroupCount];
Array.Copy(groupOffsets, nextGroupIndex, GroupCount);

// Padded vectors are stored as flat arrays so the WebApi can pointer-scan or
// index directly without object graphs.
var vectors = GC.AllocateUninitializedArray<short>(count * PaddedDims);
var labels = GC.AllocateUninitializedArray<byte>(count);

Console.WriteLine("Packing vectors grouped by fine bucket key...");

using (var fs = File.OpenRead(inputPath))
using (var gz = new GZipStream(fs, CompressionMode.Decompress))
using (var doc = JsonDocument.Parse(gz))
{
    // Second pass writes each vector into its final grouped slot.
    var root = doc.RootElement;
    foreach (var el in root.EnumerateArray())
    {
        var v = el.GetProperty("vector");
        int group = DataConverterVectorGroups.VectorGroup(v);
        int dest = nextGroupIndex[group]++;
        int vectorBase = dest * PaddedDims;

        for (int i = 0; i < Dims; i++)
            vectors[vectorBase + i] = (short)(v[i].GetSingle() * Scale);
        for (int i = Dims; i < PaddedDims; i++)
            vectors[vectorBase + i] = 0;

        labels[dest] = el.GetProperty("label").GetString() == "fraud" ? (byte)1 : (byte)0;
    }
}

using var outStream = File.Create(outputPath);
using var writer = new BinaryWriter(outStream);

writer.Write(Magic);
writer.Write(count);
writer.Write(Dims);
writer.Write(PaddedDims);
writer.Write((int)Scale);
for (int i = 0; i <= GroupCount; i++)
    writer.Write(groupOffsets[i]);

Console.WriteLine("Writing grouped vectors and labels...");
foreach (short value in vectors)
    writer.Write(value);
writer.Write(labels);

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
    /// in the API, used to place vectors in grouped order inside <c>references.bin</c>.
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
