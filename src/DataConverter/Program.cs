using System.IO.Compression;
using System.Text.Json;

string inputDir = args.Length > 0 ? args[0] : "/data";
string inputPath = Path.Combine(inputDir, "references.json.gz");
string outputPath = Path.Combine(inputDir, "references.bin");

const short Scale = 10000;
const int Dims = 14;
const int PaddedDims = 16; // AVX2 alignment

Console.WriteLine("Loading references.json.gz...");

int count;

using (var fs = File.OpenRead(inputPath))
using (var gz = new GZipStream(fs, CompressionMode.Decompress))
using (var doc = JsonDocument.Parse(gz))
{
    var root = doc.RootElement;
    count = root.GetArrayLength();
    Console.WriteLine($"Found {count:N0} vectors");
}

// Write binary format:
// [int32 count][int32 dims][int32 padded_dims][int32 scale]
// [count * padded_dims * int16 vectors]
// [count * byte labels]

using var outStream = File.OpenWrite(outputPath);
using var writer = new BinaryWriter(outStream);

writer.Write(count);
writer.Write(Dims);
writer.Write(PaddedDims);
writer.Write((int)Scale);

Console.WriteLine("Writing vectors (padded to 16 dims)...");

using (var fs = File.OpenRead(inputPath))
using (var gz = new GZipStream(fs, CompressionMode.Decompress))
using (var doc = JsonDocument.Parse(gz))
{
    var root = doc.RootElement;
    foreach (var el in root.EnumerateArray())
    {
        var v = el.GetProperty("vector");
        for (int i = 0; i < Dims; i++)
            writer.Write((short)(v[i].GetSingle() * Scale));
        // Pad remaining dims with 0
        for (int i = Dims; i < PaddedDims; i++)
            writer.Write((short)0);
    }
}

Console.WriteLine("Writing labels...");

using (var fs = File.OpenRead(inputPath))
using (var gz = new GZipStream(fs, CompressionMode.Decompress))
using (var doc = JsonDocument.Parse(gz))
{
    var root = doc.RootElement;
    foreach (var el in root.EnumerateArray())
    {
        byte label = el.GetProperty("label").GetString() == "fraud" ? (byte)1 : (byte)0;
        writer.Write(label);
    }
}

writer.Flush();

long size = new FileInfo(outputPath).Length;
Console.WriteLine($"Done. Output: {size / (1024.0 * 1024.0):F1} MB ({size:N0} bytes)");
Console.WriteLine($"Format: {count} vectors, {Dims} dims (padded to {PaddedDims}), scale {Scale}");
