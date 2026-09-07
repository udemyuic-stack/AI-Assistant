using Qdrant.Client;
using Qdrant.Client.Grpc;
using UglyToad.PdfPig;

namespace AiAssistant.Api.Services;

// Vector store + retrieval. Points carry a "scope" payload field:
//   "global"        - part of the curated knowledge base, visible to every question
//   "ticket:{id}"    - attached to one conversation/ticket only, never leaks to others
// Search always includes "global" plus the caller's own scope (if any) - never
// a hard language filter (that was the book's bug: exact-match language filter
// silently returns zero results when source docs are tagged in a different
// language than the question). Language is carried as metadata for future
// boosting, not as an exclusion filter.
public class QdrantRagService
{
    private readonly OllamaService _ollama;
    private readonly QdrantClient _qdrant;
    private readonly ILogger<QdrantRagService> _logger;
    private readonly string _collection;
    private readonly ulong _vectorSize;
    private readonly float _similarityThreshold;
    private bool _initialized;

    public const string GlobalScope = "global";

    public QdrantRagService(OllamaService ollama, IConfiguration config, ILogger<QdrantRagService> logger)
    {
        _ollama = ollama;
        _logger = logger;
        _collection = config["Qdrant:Collection"] ?? "documents";
        _vectorSize = ulong.Parse(config["Qdrant:VectorSize"] ?? "4096");
        _similarityThreshold = float.Parse(config["Rag:SimilarityThreshold"] ?? "0.4");

        var host = config["Qdrant:Host"] ?? "localhost";
        var grpcPort = int.Parse(config["Qdrant:GrpcPort"] ?? "6334");
        _qdrant = new QdrantClient(host, grpcPort);
    }

    public async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        try
        {
            var cols = await _qdrant.ListCollectionsAsync();
            if (!cols.Contains(_collection))
            {
                await _qdrant.CreateCollectionAsync(_collection,
                    new VectorParams { Size = _vectorSize, Distance = Distance.Cosine });
            }
            _initialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qdrant hali tayyor emas");
        }
    }

    public async Task IngestAsync(
        string filePath,
        string fileName,
        string scope = GlobalScope,
        string? language = null,
        IProgress<string>? progress = null)
    {
        await EnsureInitializedAsync();
        if (!_initialized) throw new InvalidOperationException("Qdrant mavjud emas.");

        progress?.Report("Matn ajratilyapti...");
        var chunks = ExtractChunks(filePath, fileName);
        var baseId = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        const int batchSize = 32;
        var allEmbeddings = new List<float[]>(chunks.Count);

        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            var batch = chunks.GetRange(i, Math.Min(batchSize, chunks.Count - i));
            var batchNum = i / batchSize + 1;
            var totalBatches = (int)Math.Ceiling(chunks.Count / (double)batchSize);
            progress?.Report($"{batchNum}/{totalBatches} to'plam vektorlanyapti ({chunks.Count} bo'lak)...");
            var embeddings = await _ollama.EmbedBatchAsync(batch);
            if (embeddings.Count != batch.Count)
                throw new InvalidOperationException(
                    $"Ollama {embeddings.Count}/{batch.Count} vektor qaytardi. " +
                    "Embed model topilmadimi yoki Ollama javob bermadimi - " +
                    "OllamaService loglarini tekshiring.");
            allEmbeddings.AddRange(embeddings);
        }

        var points = new List<PointStruct>(chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
        {
            var vec = new Vector();
            vec.Data.AddRange(allEmbeddings[i]);

            var point = new PointStruct
            {
                Id = new PointId { Num = baseId + (ulong)i },
                Vectors = new Vectors { Vector = vec }
            };
            point.Payload.Add("text", new Value { StringValue = chunks[i] });
            point.Payload.Add("source", new Value { StringValue = fileName });
            point.Payload.Add("scope", new Value { StringValue = scope });
            if (!string.IsNullOrEmpty(language))
                point.Payload.Add("language", new Value { StringValue = language });
            points.Add(point);
        }

        progress?.Report("Bazaga yozilyapti...");
        await _qdrant.UpsertAsync(_collection, points);
        progress?.Report("Tayyor!");
    }

    // scope: pass a ticket/session id (e.g. "ticket:4821") to also search that
    // caller's private uploads alongside the global knowledge base. Omit for
    // global-only search.
    public async Task<string> SearchAsync(string query, string? scope = null, int topK = 3)
    {
        if (!_initialized) await EnsureInitializedAsync();
        if (!_initialized) return string.Empty;

        try
        {
            var vector = await _ollama.EmbedAsync(query);
            if (vector.Length == 0) return string.Empty;

            var scopeValues = scope is null ? [GlobalScope] : new[] { GlobalScope, scope };
            var scopeFilter = new Filter
            {
                Should = { scopeValues.Select(s => new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "scope",
                        Match = new Match { Keyword = s }
                    }
                }) }
            };

            var candidates = await _qdrant.SearchAsync(
                collectionName: _collection,
                vector: vector,
                filter: scopeFilter,
                limit: 15,
                scoreThreshold: _similarityThreshold);

            if (!candidates.Any()) return string.Empty;

            var texts = candidates
                .Where(r => r.Payload.TryGetValue("text", out _))
                .Select(r => r.Payload["text"].StringValue)
                .ToList();

            var primary = texts.Take(topK).ToList();

            // Keyword-stem fallback: catches inflected forms a pure vector
            // search misses (Uzbek/Russian suffixes on the same root word).
            var stems = query
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Select(w => w[..Math.Min(6, w.Length)].ToLowerInvariant())
                .Distinct()
                .ToList();

            var extras = texts
                .Skip(topK)
                .Where(t => stems.Any(stem => t.ToLowerInvariant().Contains(stem)))
                .ToList();

            var final = primary.Concat(extras).Take(5).ToList();
            return string.Join("\n\n---\n\n", final);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Qidiruv muvaffaqiyatsiz tugadi");
            return string.Empty;
        }
    }

    private static List<string> ExtractChunks(string path, string fileName)
    {
        string text;

        if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var pdf = PdfDocument.Open(path);
            text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        }
        else
        {
            text = File.ReadAllText(path);
        }

        var chunks = new List<string>();
        const int size = 1000;
        const int overlap = 200;

        for (int i = 0; i < text.Length; i += size - overlap)
        {
            var chunk = text.Substring(i, Math.Min(size, text.Length - i)).Trim();
            if (chunk.Length > 50)
                chunks.Add(chunk);
        }

        return chunks;
    }
}
