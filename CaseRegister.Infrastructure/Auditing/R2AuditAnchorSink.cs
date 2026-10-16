using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CaseRegister.Application.Auditing;
using Microsoft.Extensions.Options;

namespace CaseRegister.Infrastructure.Auditing;

/// <summary>
/// AWS Signature Version 4, the signing Cloudflare R2's S3 API expects. Written out here (about forty lines) so no cloud SDK is added
/// for three requests. Checked in the tests against the worked examples in Amazon's documentation.
/// </summary>
internal static class AwsSignatureV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    public static string AmzDate(DateTimeOffset time) => time.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// The Authorization header value. <paramref name="headers"/> are the headers that are signed: lower-case names, trimmed values,
    /// and they must include host, x-amz-date (equal to <paramref name="amzDate"/>) and x-amz-content-sha256.
    /// </summary>
    public static string Authorization(
        string accessKeyId,
        string secretAccessKey,
        string region,
        string service,
        string method,
        string canonicalUri,
        string canonicalQuery,
        SortedDictionary<string, string> headers,
        string payloadHashHex,
        string amzDate)
    {
        var dateStamp = amzDate[..8];
        var scope = $"{dateStamp}/{region}/{service}/aws4_request";
        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalHeaders = string.Concat(headers.Select(header => $"{header.Key}:{header.Value}\n"));

        var canonicalRequest = $"{method}\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHashHex}";
        var stringToSign = $"{Algorithm}\n{amzDate}\n{scope}\n{Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest))}";

        var signingKey = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp), region), service), "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(signingKey, stringToSign));

        return $"{Algorithm} Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}

/// <summary>
/// Daily anchors in a Cloudflare R2 bucket. Every anchor is its own object with its own name (the date, the sequence, the start of the
/// hash and the time), written with "If-None-Match: *" so R2 refuses to replace an object that is already there: an anchor is written
/// once and never overwritten. With an R2 bucket lock on top (see docs/deployment.md) it cannot be deleted either, so someone with the
/// database and the application's credentials still cannot rewrite history unnoticed. The token this runs with can reach one bucket.
/// </summary>
internal sealed class R2AuditAnchorSink(IHttpClientFactory clients, IOptions<AuditOptions> options, TimeProvider time) : IAuditAnchorSink
{
    public const string ClientName = "r2-audit-anchors";
    public const string Prefix = "anchors/";
    private const string Region = "auto"; // R2 has no regions; it asks for this word.
    private const string Service = "s3";
    private const int ReadBatchSize = 8;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => true;

    public static string KeyFor(AuditAnchor anchor) =>
        $"{Prefix}{anchor.ZambianDate}/{anchor.Sequence.ToString("D12", CultureInfo.InvariantCulture)}-{anchor.Hash[..Math.Min(16, anchor.Hash.Length)]}-{anchor.CreatedAt.UtcDateTime.ToString("yyyyMMddHHmmssffffff", CultureInfo.InvariantCulture)}.json";

    public async Task WriteAsync(AuditAnchor anchor, CancellationToken cancellationToken = default)
    {
        var key = KeyFor(anchor);
        var body = JsonSerializer.SerializeToUtf8Bytes(anchor, _json);

        using var response = await SendAsync(HttpMethod.Put, ObjectPath(key), string.Empty, body, ifNoneMatch: true, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // Already there: fine if it is this very anchor (a retry after a reply was lost), never fine if it is something else.
            var existing = await GetBytesAsync(key, cancellationToken);
            if (existing.AsSpan().SequenceEqual(body))
            {
                return;
            }

            throw new InvalidDataException("An anchor with this name already exists with different content, so it was not replaced.");
        }

        throw new IOException($"The anchor store answered {(int)response.StatusCode} to a write.");
    }

    public async Task<IReadOnlyList<AuditAnchor>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        var keys = new List<string>();
        string? token = null;
        do
        {
            var query = $"list-type=2&prefix={Uri.EscapeDataString(Prefix)}" + (token is null ? string.Empty : $"&continuation-token={Uri.EscapeDataString(token)}");
            using var response = await SendAsync(HttpMethod.Get, $"/{Bucket}", query, [], ifNoneMatch: false, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new IOException($"The anchor store answered {(int)response.StatusCode} to a listing.");
            }

            (var page, token) = ParseListing(await response.Content.ReadAsStringAsync(cancellationToken));
            keys.AddRange(page);
        }
        while (token is not null);

        var anchors = new List<AuditAnchor>(keys.Count);
        foreach (var batch in keys.Chunk(ReadBatchSize))
        {
            var bodies = await Task.WhenAll(batch.Select(key => GetBytesAsync(key, cancellationToken)));
            anchors.AddRange(bodies.Select(body => JsonSerializer.Deserialize<AuditAnchor>(body, _json) ?? throw new InvalidDataException("An anchor could not be read.")));
        }

        return anchors;
    }

    /// <summary>The keys in a ListObjectsV2 answer, and the token for the next page (null at the end). DTDs are refused: nothing here needs them.</summary>
    internal static (IReadOnlyList<string> Keys, string? NextToken) ParseListing(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        string? Value(string name) => document.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value;

        var keys = document.Descendants().Where(element => element.Name.LocalName == "Key").Select(element => element.Value).ToList();
        var truncated = string.Equals(Value("IsTruncated"), "true", StringComparison.OrdinalIgnoreCase);

        return (keys, truncated ? Value("NextContinuationToken") ?? throw new InvalidDataException("The listing was cut short without a way to continue it.") : null);
    }

    private string Bucket => options.Value.R2.Bucket!;

    private string ObjectPath(string key) => $"/{Bucket}/{string.Join('/', key.Split('/').Select(Uri.EscapeDataString))}";

    private async Task<byte[]> GetBytesAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, ObjectPath(key), string.Empty, [], ifNoneMatch: false, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"The anchor store answered {(int)response.StatusCode} to a read.");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string query, byte[] body, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        var r2 = options.Value.R2;
        var endpoint = new Uri(r2.Endpoint!);
        var payloadHash = AwsSignatureV4.Sha256Hex(body);
        var amzDate = AwsSignatureV4.AmzDate(time.GetUtcNow());

        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = endpoint.Authority,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };
        if (ifNoneMatch)
        {
            signed["if-none-match"] = "*";
        }

        var request = new HttpRequestMessage(method, new Uri(endpoint, path + (query.Length == 0 ? string.Empty : "?" + query)));
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        if (ifNoneMatch)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }

        request.Headers.TryAddWithoutValidation(
            "Authorization",
            AwsSignatureV4.Authorization(r2.AccessKeyId!, r2.SecretAccessKey!, Region, Service, method.Method, path, CanonicalQuery(query), signed, payloadHash, amzDate));

        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        try
        {
            return await clients.CreateClient(ClientName).SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A network error, a timeout or an open circuit breaker: whatever reads the anchors expects an IOException from a store it cannot reach.
            throw new IOException("The anchor store could not be reached.", exception);
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>The query as it is signed: names sorted, and both names and values already percent-encoded.</summary>
    private static string CanonicalQuery(string query) =>
        query.Length == 0 ? string.Empty : string.Join('&', query.Split('&').OrderBy(pair => pair, StringComparer.Ordinal));
}
