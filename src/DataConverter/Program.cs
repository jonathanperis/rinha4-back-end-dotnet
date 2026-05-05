using System.IO.Compression;
using System.Text.Json;

string inputDir = args.Length > 0 ? args[0] : "/data";
string inputPath = Path.Combine(inputDir, "references.json.gz");
string outputPath = Path.Combine(inputDir, "references.bin");

const short Scale = 10000;
const int Dims = 14;
const int K = 512;
const int Nprobe = 3;
const int KmeansIters = 3;

Console.WriteLine("Loading references.json.gz...");

// ── Parse once into memory ──
int count;
short[][] vectors;
byte[] labels;

using (var fs = File.OpenRead(inputPath))
using (var gz = new GZipStream(fs, CompressionMode.Decompress))
using (var doc = JsonDocument.Parse(gz))
{
    var root = doc.RootElement;
    count = root.GetArrayLength();
    Console.WriteLine($"Found {count:N0} vectors");

    vectors = new short[count][];
    labels = new byte[count];

    int idx = 0;
    foreach (var el in root.EnumerateArray())
    {
        var v = el.GetProperty("vector");
        var row = new short[Dims];
        for (int i = 0; i < Dims; i++)
            row[i] = (short)(v[i].GetSingle() * Scale);
        vectors[idx] = row;

        labels[idx] = el.GetProperty("label").GetString() == "fraud" ? (byte)1 : (byte)0;
        idx++;
    }
}

Console.WriteLine("Initializing random centroids...");

var centroids = new short[K][];
var rand = new Random(42);

for (int c = 0; c < K; c++)
{
    centroids[c] = new short[Dims];
    Buffer.BlockCopy(vectors[rand.Next(count)], 0, centroids[c], 0, Dims * sizeof(short));
}

// ── Lloyd's algorithm ──
int[] assignments = new int[count];

for (int iter = 0; iter < KmeansIters; iter++)
{
    Console.WriteLine($"  Iteration {iter + 1}/{KmeansIters}");

    // Assign
    for (int i = 0; i < count; i++)
    {
        float best = float.MaxValue;
        int bestC = 0;
        var vi = vectors[i];
        for (int c = 0; c < K; c++)
        {
            float d = DistSq(vi, centroids[c]);
            if (d < best) { best = d; bestC = c; }
        }
        assignments[i] = bestC;
    }

    // Recompute
    var sums = new float[K][];
    var counts = new int[K];
    for (int c = 0; c < K; c++) sums[c] = new float[Dims];

    for (int i = 0; i < count; i++)
    {
        int c = assignments[i];
        counts[c]++;
        var vi = vectors[i];
        var sc = sums[c];
        for (int d = 0; d < Dims; d++) sc[d] += vi[d];
    }

    for (int c = 0; c < K; c++)
    {
        if (counts[c] == 0)
        {
            Buffer.BlockCopy(vectors[rand.Next(count)], 0, centroids[c], 0, Dims * sizeof(short));
            continue;
        }
        var cc = centroids[c];
        var sc = sums[c];
        for (int d = 0; d < Dims; d++) cc[d] = (short)(sc[d] / counts[c]);
    }
}

// Final assign
for (int i = 0; i < count; i++)
{
    float best = float.MaxValue;
    int bestC = 0;
    var vi = vectors[i];
    for (int c = 0; c < K; c++)
    {
        float d = DistSq(vi, centroids[c]);
        if (d < best) { best = d; bestC = c; }
    }
    assignments[i] = bestC;
}

// ── Build posting lists and reorder ──
Console.WriteLine("Building posting lists...");

var postingLists = new List<int>[K];
for (int c = 0; c < K; c++) postingLists[c] = new List<int>();
for (int i = 0; i < count; i++) postingLists[assignments[i]].Add(i);

var rVectors = new short[count][];
var rLabels = new byte[count];
int pos = 0;

for (int c = 0; c < K; c++)
{
    foreach (var idx in postingLists[c])
    {
        rVectors[pos] = vectors[idx];
        rLabels[pos] = labels[idx];
        pos++;
    }
}

// ── Write binary ──
Console.WriteLine($"Writing {outputPath}...");

using var outStream = File.OpenWrite(outputPath);
using var writer = new BinaryWriter(outStream);

// Header: 20 bytes
writer.Write(count);
writer.Write(Dims);
writer.Write((int)Scale);
writer.Write(K);
writer.Write(Nprobe);

// Centroids: K * Dims * 2 bytes
for (int c = 0; c < K; c++)
    for (int d = 0; d < Dims; d++)
        writer.Write(centroids[c][d]);

// Posting list metadata: K * 8 bytes (offset + length)
int vecOffset = 0;
for (int c = 0; c < K; c++)
{
    writer.Write(vecOffset);
    writer.Write(postingLists[c].Count);
    vecOffset += postingLists[c].Count;
}

// Vectors (reordered)
for (int i = 0; i < count; i++)
    for (int d = 0; d < Dims; d++)
        writer.Write(rVectors[i][d]);

// Labels (reordered)
for (int i = 0; i < count; i++)
    writer.Write(rLabels[i]);

writer.Flush();

Console.WriteLine($"Done. Output: {new FileInfo(outputPath).Length / (1024.0 * 1024.0):F1} MB");

static float DistSq(short[] a, short[] b)
{
    float s = 0;
    for (int i = 0; i < a.Length; i++)
    {
        float d = a[i] - b[i];
        s += d * d;
    }
    return s;
}
