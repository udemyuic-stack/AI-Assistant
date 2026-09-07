using AiAssistant.Api.Configuration;
using AiAssistant.Api.Models;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace AiAssistant.Api.Services;

// Keeps the uploaded originals in MinIO. The index holds vectors, which are
// derived and disposable; this holds the file they were derived from, so a
// document survives re-chunking, a model change, or a rebuilt collection.
//
// Object keys are scope-prefixed, which keeps one conversation's uploads
// grouped together and makes them removable with that conversation.
public class DocumentStorage(IMinioClient minio, IOptions<StorageOptions> options)
{
    private readonly StorageOptions _options = options.Value;
    private readonly SemaphoreSlim _bucketLock = new(1, 1);
    private bool _bucketReady;

    public async Task<StoredDocument> UploadAsync(
        string filePath,
        string fileName,
        RetrievalScope scope,
        CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);

        var objectKey = BuildObjectKey(scope, fileName);

        try
        {
            await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(_options.Bucket)
                .WithObject(objectKey)
                .WithFileName(filePath)
                .WithContentType(ContentTypeFor(fileName)), ct);
        }
        catch (MinioException ex)
        {
            throw new DocumentStorageException($"'{fileName}' faylini saqlab bo'lmadi.", ex);
        }

        return new StoredDocument(objectKey, fileName, scope);
    }

    public async Task<Stream> OpenAsync(string objectKey, CancellationToken ct = default)
    {
        var buffer = new MemoryStream();

        try
        {
            await minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(_options.Bucket)
                .WithObject(objectKey)
                .WithCallbackStream(async (source, token) => await source.CopyToAsync(buffer, token)), ct);
        }
        catch (MinioException ex)
        {
            throw new DocumentStorageException($"'{objectKey}' obyektini o'qib bo'lmadi.", ex);
        }

        buffer.Position = 0;
        return buffer;
    }

    // Collision-free without a round-trip: the timestamp orders uploads and the
    // random suffix separates two files uploaded in the same millisecond.
    private static string BuildObjectKey(RetrievalScope scope, string fileName)
    {
        var safeScope = scope.Value.Replace(':', '-');
        var stamp = DateTime.UtcNow.ToString("yyyy/MM/dd/HHmmss");
        var unique = Guid.NewGuid().ToString("N")[..8];
        return $"{safeScope}/{stamp}-{unique}-{Path.GetFileName(fileName)}";
    }

    private static string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".json" => "application/json",
            _ => "application/octet-stream"
        };

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketReady) return;

        await _bucketLock.WaitAsync(ct);
        try
        {
            if (_bucketReady) return;

            var exists = await minio.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(_options.Bucket), ct);

            if (!exists)
                await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(_options.Bucket), ct);

            _bucketReady = true;
        }
        catch (MinioException ex)
        {
            throw new DocumentStorageException(
                $"'{_options.Bucket}' bucket'iga ulanib bo'lmadi ({_options.Endpoint}).", ex);
        }
        finally
        {
            _bucketLock.Release();
        }
    }
}

public class DocumentStorageException(string message, Exception? inner = null)
    : Exception(message, inner);
