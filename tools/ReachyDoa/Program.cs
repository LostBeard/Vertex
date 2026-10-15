// Reads the Reachy Mini's microphone array direction data, silently (read-only: no motion, no sound), and says where a voice is.
//
//   dotnet run --project tools/ReachyDoa -- [host] [--seconds N] [--label NAME] [--csv path]
//
// Why this and not /api/state/doa: on daemon 1.10.0 that endpoint answered null ("audio not initialized") while the chip itself answered every
// parameter read, and the chip has MORE than the endpoint reports. The XVF3800 runs three beams (a free-running one that scans, and two FOCUSED
// ones that each lock onto a speaker) plus a selected direction, and reports a speech-energy figure per beam:
//   AEC_AZIMUTH_VALUES  4 radians: focused beam 1, focused beam 2, free-running beam, auto-select beam
//   AEC_SPENERGY_VALUES 4 floats, same order; above 0 = speech in that beam
//   DOA_VALUE_RADIANS   (angle, speech flag): what the endpoint reports
// The array is LINEAR (AEC_MIC_ARRAY_GEO: four mics on one axis, 33 mm apart), so an angle is 0..pi with a front/back ambiguity. Pollen's convention:
// 0 = the robot's left, pi/2 = straight ahead (or behind), pi = the robot's right.
//
// Stand at the robot's left, then in front, then at its right, say a sentence each time with --label, and compare the summaries.
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

// The host is the first argument only when it is not a flag or a flag's value ("--label left" once made "left" the host: every read failed and the tool said no speech was heard).
var host = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "reachy-mini.local";
double Arg(string name, double fallback) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? double.Parse(args[i + 1], CultureInfo.InvariantCulture) : fallback;
string? Str(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var seconds = Arg("--seconds", 20);
var label = Str("--label") ?? "";
var csvPath = Str("--csv");

using var http = new HttpClient { BaseAddress = new Uri($"http://{host}:8000"), Timeout = TimeSpan.FromSeconds(3) };

async Task<double[]?> Read(string name)
{
    // The chip answers on a shared USB control channel; an occasional read fails (seen once in a minute) and the next one works.
    for (var attempt = 0; attempt < 3; attempt++)
    {
        try
        {
            using var r = await http.GetAsync($"/api/audio/config/parameter/{name}");
            if (!r.IsSuccessStatusCode) continue;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("values").EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.Null ? double.NaN : v.GetDouble()).ToArray();
        }
        catch (Exception) { }
    }
    return null;
}

static string Side(double radians) =>
    double.IsNaN(radians) ? "  --  " : (radians * 180 / Math.PI) switch
    {
        < 60 => "LEFT  ",
        > 120 => "RIGHT ",
        _ => "FRONT ",
    };

static string Deg(double radians) => double.IsNaN(radians) ? "   -" : (radians * 180 / Math.PI).ToString("0", CultureInfo.InvariantCulture).PadLeft(4);

var version = await Read("VERSION");
var geo = await Read("AEC_MIC_ARRAY_GEO");
Console.WriteLine($"robot {host}: audio firmware {(version == null ? "?" : string.Join('.', version.Select(v => (int)v)))}, mics at x = {(geo == null ? "?" : string.Join(", ", Enumerable.Range(0, geo.Length / 3).Select(i => (geo[i * 3] * 1000).ToString("0.0", CultureInfo.InvariantCulture))))} mm");
Console.WriteLine($"listening for {seconds:0} s{(label.Length > 0 ? $" (label: {label})" : "")}. Angle: 0 = robot's left, 90 = ahead, 180 = right.");
Console.WriteLine("time    | speech energy per beam [b1 b2 free auto]   | azimuth deg [b1 b2 free auto]  | chip DOA  speech | side");

var rows = new List<(double T, double[] Energy, double[] Azimuth, double Doa, bool Speech)>();
var clock = System.Diagnostics.Stopwatch.StartNew();
StreamWriter? csv = csvPath == null ? null : new StreamWriter(csvPath, append: File.Exists(csvPath));
if (csv != null && csv.BaseStream.Length == 0) csv.WriteLine("label,t,e1,e2,e3,e4,a1,a2,a3,a4,doa,speech");
var lastBeat = -10.0;
while (clock.Elapsed.TotalSeconds < seconds)
{
    var energy = await Read("AEC_SPENERGY_VALUES");
    var azimuth = await Read("AEC_AZIMUTH_VALUES");
    var doa = await Read("DOA_VALUE_RADIANS");
    if (energy is not { Length: 4 } || azimuth is not { Length: 4 } || doa is not { Length: 2 }) continue;
    var t = clock.Elapsed.TotalSeconds;
    var speech = doa[1] > 0.5 || energy.Any(e => e > 0);
    rows.Add((t, energy, azimuth, doa[0], speech));
    csv?.WriteLine($"{label},{t:0.00},{string.Join(',', energy.Select(e => e.ToString("0.###", CultureInfo.InvariantCulture)))},{string.Join(',', azimuth.Select(a => a.ToString("0.###", CultureInfo.InvariantCulture)))},{doa[0]:0.###},{(doa[1] > 0.5 ? 1 : 0)}");
    if (speech || t - lastBeat > 3)
    {
        lastBeat = t;
        Console.WriteLine($"{t,6:0.0} s | {string.Join(' ', energy.Select(e => e.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(7)))} | {string.Join(' ', azimuth.Select(Deg))}  | {Deg(doa[0])} {Side(doa[0])} {(doa[1] > 0.5 ? "SPEECH" : "      ")} | b1 {Side(azimuth[0])}b2 {Side(azimuth[1])}");
    }
    await Task.Delay(60);
}
csv?.Dispose();

var talking = rows.Where(r => r.Speech).ToList();
Console.WriteLine();
Console.WriteLine($"{rows.Count} readings ({rows.Count / clock.Elapsed.TotalSeconds:0.0}/s), {talking.Count} with speech.");
if (rows.Count == 0) { Console.WriteLine($"no readings at all: could not read the microphone array on {host}."); return; }
if (talking.Count == 0) { Console.WriteLine("no speech heard: nothing to summarise."); return; }

static double Median(IEnumerable<double> xs) { var s = xs.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList(); return s.Count == 0 ? double.NaN : s[s.Count / 2]; }
Console.WriteLine($"chip DOA while speech: median {Deg(Median(talking.Select(r => r.Doa)))} deg {Side(Median(talking.Select(r => r.Doa)))} (spread {Deg(talking.Min(r => r.Doa))}..{Deg(talking.Max(r => r.Doa))})");
foreach (var (name, i) in new[] { ("focused beam 1", 0), ("focused beam 2", 1), ("free-running beam", 2), ("auto-select beam", 3) })
{
    var active = talking.Where(r => r.Energy[i] > 0).ToList();
    Console.WriteLine($"{name,-18}: speech in {active.Count,3} readings, median azimuth {Deg(Median(active.Select(r => r.Azimuth[i])))} deg {Side(Median(active.Select(r => r.Azimuth[i])))}");
}
