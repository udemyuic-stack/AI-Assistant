using System.Text.Json;
using AiAssistant.Eval;

// Gold-set runner.
//
//   dotnet run                              # retrieval only, ~15s for 37 questions
//   dotnet run -- --full                    # end to end, ~40 min on this host
//   dotnet run -- --save baseline.json      # record this run
//   dotnet run -- --compare baseline.json   # show only what changed since it
//
// The compare mode is the point of the whole tool. A pass rate that holds at
// 84% across a change is not evidence the change was safe - four questions can
// break while four others start working. Only a per-question diff shows that,
// and it is exactly the regression the similarity threshold move from 0.25 to
// 0.45 could have caused without anyone noticing.

var arguments = args.ToList();
var full = arguments.Remove("--full");
var goldPath = TakeOption(arguments, "--gold")
    ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "eval", "gold-set.json");
var savePath = TakeOption(arguments, "--save");
var comparePath = TakeOption(arguments, "--compare");
var apiBase = TakeOption(arguments, "--api") ?? "http://localhost:5299";

var adminKey = Environment.GetEnvironmentVariable("ADMIN_API_KEY");
if (string.IsNullOrWhiteSpace(adminKey))
{
    Console.Error.WriteLine(
        "ADMIN_API_KEY o'rnatilmagan. Qo'shni .env dan oling:\n" +
        "  set -a; source ../.env; set +a; dotnet run");
    return 2;
}

var goldJson = await File.ReadAllTextAsync(Path.GetFullPath(goldPath));
var gold = JsonSerializer.Deserialize<GoldSet>(goldJson)
    ?? throw new InvalidOperationException($"Gold-set o'qilmadi: {goldPath}");

// No timeout on the whole run: --full spends a minute per question and a
// client-side deadline mid-stream is how the UI once lost a whole circuit.
// Ctrl+C stays the way to stop it.
using var http = new HttpClient { BaseAddress = new Uri(apiBase), Timeout = Timeout.InfiniteTimeSpan };
var probe = new AssistantProbe(http, adminKey);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

var mode = full ? "full" : "retrieval";
Console.WriteLine($"Rejim: {mode} | savollar: {gold.Savollar.Count} | API: {apiBase}\n");

var results = new List<ItemResult>();
var started = DateTimeOffset.UtcNow;

foreach (var item in gold.Savollar)
{
    ItemResult result;
    try
    {
        result = full
            ? Grader.GradeAnswer(item, await probe.AnswerAsync(item.Savol, cancellation.Token))
            : Grader.GradeRetrieval(item, await probe.RetrieveAsync(item.Savol, cancellation.Token));
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\nTo'xtatildi.");
        break;
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
    {
        // A transport failure is a failed question, not a failed run: one dead
        // call should not discard the 30 results already collected.
        result = new ItemResult(item.Id, item.Toifa, false, $"xato: {ex.Message}");
    }

    results.Add(result);
    Console.WriteLine($"  {(result.Passed ? "o'tdi " : "YIQIL")} {result.Id,-28} {result.Detail}");
}

var elapsed = DateTimeOffset.UtcNow - started;
Console.WriteLine();
PrintSummary(results, elapsed);

if (savePath is not null)
{
    var run = new RunResult(mode, started, results);
    await File.WriteAllTextAsync(savePath,
        JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\nSaqlandi: {savePath}");
}

if (comparePath is not null)
    await PrintComparisonAsync(comparePath, results);

// Non-zero when anything failed, so this can gate a change later without
// anyone having to read the table.
return results.Any(r => !r.Passed) ? 1 : 0;

static string? TakeOption(List<string> arguments, string name)
{
    var index = arguments.IndexOf(name);
    if (index < 0 || index + 1 >= arguments.Count) return null;

    var value = arguments[index + 1];
    arguments.RemoveRange(index, 2);
    return value;
}

static void PrintSummary(IReadOnlyList<ItemResult> results, TimeSpan elapsed)
{
    var passed = results.Count(r => r.Passed);
    Console.WriteLine($"Jami: {passed}/{results.Count} ({(double)passed / results.Count:P1}) | {elapsed.TotalSeconds:F1}s");

    foreach (var group in results.GroupBy(r => r.Toifa).OrderBy(g => g.Key))
    {
        var ok = group.Count(r => r.Passed);
        Console.WriteLine($"   {group.Key,-10} {ok}/{group.Count()} ({(double)ok / group.Count():P0})");
    }
}

static async Task PrintComparisonAsync(string baselinePath, IReadOnlyList<ItemResult> current)
{
    var baseline = JsonSerializer.Deserialize<RunResult>(
        await File.ReadAllTextAsync(baselinePath), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    if (baseline is null)
    {
        Console.Error.WriteLine($"Baza o'qilmadi: {baselinePath}");
        return;
    }

    var before = baseline.Items.ToDictionary(i => i.Id);
    var regressed = current.Where(c => !c.Passed && before.TryGetValue(c.Id, out var b) && b.Passed).ToList();
    var fixedUp = current.Where(c => c.Passed && before.TryGetValue(c.Id, out var b) && !b.Passed).ToList();

    Console.WriteLine($"\n=== {Path.GetFileName(baselinePath)} ({baseline.RunAt:yyyy-MM-dd HH:mm}) bilan solishtirish ===");

    if (regressed.Count == 0 && fixedUp.Count == 0)
    {
        Console.WriteLine("  Har bir savol bo'yicha o'zgarish yo'q.");
        return;
    }

    foreach (var item in regressed)
        Console.WriteLine($"  REGRESSIYA  {item.Id,-28} {item.Detail}");
    foreach (var item in fixedUp)
        Console.WriteLine($"  tuzaldi     {item.Id,-28} {item.Detail}");
}
