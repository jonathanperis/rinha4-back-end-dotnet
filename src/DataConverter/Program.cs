using System.IO.Compression;
using System.Text.Json;

string inputDir = args.Length > 0 ? args[0] : "/data";
string InputPath = Path.Combine(inputDir, "references.json.gz");
string OutputPath = Path.Combine(inputDir, "references.bin");
const short Scale = 10000;

Console.WriteLine("Loading references.json.gz...");

using var fileStream = File.OpenRead(InputPath);
using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
using var doc = await JsonDocument.ParseAsync(gzipStream);

var root = doc.RootElement;
int count = root.GetArrayLength();
Console.WriteLine($"Found {count} vectors");

using var outStream = File.OpenWrite(OutputPath);
using var writer = new BinaryWriter(outStream);

// Header
writer.Write(count);
writer.Write(14); // dims
writer.Write((int)Scale);

int fraudCount = 0;
int legitCount = 0;

foreach (var element in root.EnumerateArray())
{
    var vector = element.GetProperty("vector");
    var label = element.GetProperty("label").GetString();

    for (int i = 0; i < 14; i++)
    {
        float f = vector[i].GetSingle();
        short s = (short)(f * Scale);
        writer.Write(s);
    }

    byte labelByte = label == "fraud" ? (byte)1 : (byte)0;
    writer.Write(labelByte);

    if (label == "fraud") fraudCount++;
    else legitCount++;
}

Console.WriteLine($"Converted {count} vectors");
Console.WriteLine($"  Fraud: {fraudCount}");
Console.WriteLine($"  Legit: {legitCount}");
Console.WriteLine($"Output written to {OutputPath}");
Console.WriteLine($"Output size: {new FileInfo(OutputPath).Length / (1024 * 1024)} MB");
