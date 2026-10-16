using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using CaseRegister.Application.Auditing;
using CaseRegister.Infrastructure.Auditing;
using CaseRegister.Tests.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CaseRegister.Tests.Auditing;

/// <summary>A stand-in for the R2 bucket: stores objects, refuses to replace one when asked not to, and lists in small pages.</summary>
internal sealed class FakeR2Handler : HttpMessageHandler
{
    public const string Bucket = "anchors-bucket";

    public Dictionary<string, byte[]> Objects { get; } = [];

    public List<(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

    /// <summary>Answer every request with this status instead.</summary>
    public HttpStatusCode? Fail { get; set; }

    /// <summary>Throw this for every request, as a network failure does.</summary>
    public Exception? Throw { get; set; }

    public int PageSize { get; set; } = 1000;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!, request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase), Encoding.UTF8.GetString(body)));

        if (Throw is not null)
        {
            throw Throw;
        }

        if (Fail is { } status)
        {
            return new HttpResponseMessage(status);
        }

        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        if (path == $"/{Bucket}" && request.Method == HttpMethod.Get)
        {
            return List(request.RequestUri.Query);
        }

        var key = path[($"/{Bucket}/".Length)..];
        if (request.Method == HttpMethod.Put)
        {
            if (request.Headers.TryGetValues("If-None-Match", out var values) && values.Contains("*") && Objects.ContainsKey(key))
            {
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            }

            Objects[key] = body;

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        return Objects.TryGetValue(key, out var found)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(found) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage List(string query)
    {
        var parameters = query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
        var prefix = parameters["prefix"];
        var all = Objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var start = parameters.TryGetValue("continuation-token", out var token) ? int.Parse(token, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var page = all.Skip(start).Take(PageSize).ToList();
        var more = start + page.Count < all.Count;

        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
        xml.Append(System.Globalization.CultureInfo.InvariantCulture, $"<Name>{Bucket}</Name><KeyCount>{page.Count}</KeyCount><IsTruncated>{(more ? "true" : "false")}</IsTruncated>");
        if (more)
        {
            xml.Append(System.Globalization.CultureInfo.InvariantCulture, $"<NextContinuationToken>{start + page.Count}</NextContinuationToken>");
        }

        foreach (var key in page)
        {
            xml.Append(System.Globalization.CultureInfo.InvariantCulture, $"<Contents><Key>{key}</Key></Contents>");
        }

        xml.Append("</ListBucketResult>");

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
    }
}

internal sealed class FakeClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

public class R2AuditAnchorSinkTests
{
    private const string SecretAccessKey = "SECRET-ACCESS-KEY-DO-NOT-LEAK";
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset _now = new(2026, 10, 12, 8, 30, 0, TimeSpan.Zero);
    private static readonly AuditKey _key = new("k1", Enumerable.Range(1, 32).Select(index => (byte)index).ToArray());

    private static (R2AuditAnchorSink Sink, FakeR2Handler Store) Make(Action<FakeR2Handler>? adjust = null)
    {
        var store = new FakeR2Handler();
        adjust?.Invoke(store);
        var options = Options.Create(new AuditOptions
        {
            R2 = { Endpoint = "https://account123.r2.cloudflarestorage.com", Bucket = FakeR2Handler.Bucket, AccessKeyId = "AKID-TEST", SecretAccessKey = SecretAccessKey },
        });

        return (new R2AuditAnchorSink(new FakeClientFactory(store), options, new FakeTimeProvider(_now)), store);
    }

    private static AuditAnchor Anchor(long sequence = 42, char hash = 'a', DateTimeOffset? at = null) =>
        AuditAnchoring.Make(_key, sequence, new string(hash, 64), at ?? _now);

    // --- The signature, checked against Amazon's own worked examples ------------------------------------------

    [Fact]
    public void The_signature_matches_amazons_example_for_reading_an_object()
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ["x-amz-date"] = "20130524T000000Z",
        };

        var authorization = AwsSignatureV4.Authorization(
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3", "GET", "/test.txt", string.Empty, headers, headers["x-amz-content-sha256"], "20130524T000000Z");

        Assert.EndsWith("Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", authorization, StringComparison.Ordinal);
        Assert.Contains("Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request", authorization, StringComparison.Ordinal);
        Assert.Contains("SignedHeaders=host;range;x-amz-content-sha256;x-amz-date", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public void The_signature_matches_amazons_example_for_writing_an_object()
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["date"] = "Fri, 24 May 2013 00:00:00 GMT",
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["x-amz-content-sha256"] = "44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072",
            ["x-amz-date"] = "20130524T000000Z",
            ["x-amz-storage-class"] = "REDUCED_REDUNDANCY",
        };

        var authorization = AwsSignatureV4.Authorization(
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3", "PUT", "/test%24file.text", string.Empty, headers, headers["x-amz-content-sha256"], "20130524T000000Z");

        Assert.EndsWith("Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd", authorization, StringComparison.Ordinal);
        Assert.Equal("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072", AwsSignatureV4.Sha256Hex(Encoding.UTF8.GetBytes("Welcome to Amazon S3.")));
    }

    [Fact]
    public void The_signature_matches_amazons_example_for_listing_a_bucket()
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["x-amz-content-sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ["x-amz-date"] = "20130524T000000Z",
        };

        var authorization = AwsSignatureV4.Authorization(
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3", "GET", "/", "max-keys=2&prefix=J", headers, headers["x-amz-content-sha256"], "20130524T000000Z");

        Assert.EndsWith("Signature=34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7", authorization, StringComparison.Ordinal);
    }

    // --- Writing --------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_anchor_is_written_as_its_own_object_with_a_signed_request_that_will_not_replace_one()
    {
        var (sink, store) = Make();
        var anchor = Anchor();

        await sink.WriteAsync(anchor);

        var request = Assert.Single(store.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("account123.r2.cloudflarestorage.com", request.Uri.Host);
        Assert.Equal($"/anchors-bucket/{R2AuditAnchorSink.KeyFor(anchor)}", request.Uri.AbsolutePath);
        Assert.Equal("*", request.Headers["If-None-Match"]);
        Assert.Equal(AwsSignatureV4.Sha256Hex(Encoding.UTF8.GetBytes(request.Body)), request.Headers["x-amz-content-sha256"]);
        Assert.Equal("20261012T083000Z", request.Headers["x-amz-date"]);
        var authorization = request.Headers["Authorization"];
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKID-TEST/20261012/auto/s3/aws4_request,", authorization, StringComparison.Ordinal);
        Assert.Contains("SignedHeaders=host;if-none-match;x-amz-content-sha256;x-amz-date", authorization, StringComparison.Ordinal);
        Assert.Equal(anchor, JsonSerializer.Deserialize<AuditAnchor>(store.Objects[R2AuditAnchorSink.KeyFor(anchor)], _json));
    }

    [Fact]
    public async Task The_secret_access_key_is_never_part_of_any_request()
    {
        var (sink, store) = Make();
        await sink.WriteAsync(Anchor());
        await sink.ReadAllAsync();

        foreach (var request in store.Requests)
        {
            var everything = request.Uri + string.Join('\n', request.Headers.Select(header => $"{header.Key}: {header.Value}")) + request.Body;
            Assert.DoesNotContain(SecretAccessKey, everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Each_anchor_has_a_name_of_its_own_made_of_the_date_the_sequence_the_hash_and_the_time()
    {
        Assert.Equal("anchors/2026-10-12/000000000042-aaaaaaaaaaaaaaaa-20261012083000000000.json", R2AuditAnchorSink.KeyFor(Anchor()));

        var names = new[]
        {
            R2AuditAnchorSink.KeyFor(Anchor()),
            R2AuditAnchorSink.KeyFor(Anchor(sequence: 43)),
            R2AuditAnchorSink.KeyFor(Anchor(hash: 'b')),
            R2AuditAnchorSink.KeyFor(Anchor(at: _now.AddSeconds(1))),
        };
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public async Task Writing_the_very_same_anchor_again_is_fine_as_when_a_reply_was_lost_and_the_request_retried()
    {
        var (sink, store) = Make();
        var anchor = Anchor();

        await sink.WriteAsync(anchor);
        await sink.WriteAsync(anchor);

        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task A_different_anchor_under_a_name_that_is_taken_is_refused_and_what_is_stored_stays()
    {
        var (sink, store) = Make();
        var anchor = Anchor();
        var original = Encoding.UTF8.GetBytes("""{"someone":"else's"}""");
        store.Objects[R2AuditAnchorSink.KeyFor(anchor)] = original;

        await Assert.ThrowsAsync<InvalidDataException>(() => sink.WriteAsync(anchor));

        Assert.Equal(original, store.Objects[R2AuditAnchorSink.KeyFor(anchor)]);
    }

    // --- Reading --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_anchor_is_read_back_even_when_the_listing_comes_in_several_pages()
    {
        var (sink, store) = Make(handler => handler.PageSize = 2);
        var written = Enumerable.Range(1, 5).Select(index => Anchor(sequence: index * 10, at: _now.AddDays(index))).ToList();
        foreach (var anchor in written)
        {
            await sink.WriteAsync(anchor);
        }

        store.Requests.Clear();
        var read = await sink.ReadAllAsync();

        Assert.Equal(written.OrderBy(anchor => anchor.Sequence), read.OrderBy(anchor => anchor.Sequence));
        var listings = store.Requests.Where(request => request.Uri.AbsolutePath == "/anchors-bucket").ToList();
        Assert.Equal(3, listings.Count); // 2 + 2 + 1
        Assert.All(listings, listing => Assert.Contains("prefix=anchors%2F", listing.Uri.Query, StringComparison.Ordinal));
        Assert.Contains("continuation-token=", listings[1].Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("continuation-token", listings[0].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_bucket_has_no_anchors()
    {
        var (sink, _) = Make();

        Assert.Empty(await sink.ReadAllAsync());
    }

    [Fact]
    public void A_listing_that_declares_a_document_type_is_refused()
    {
        const string xml = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e "boom">]><ListBucketResult><Contents><Key>&e;</Key></Contents></ListBucketResult>""";

        Assert.Throws<XmlException>(() => R2AuditAnchorSink.ParseListing(xml));
    }

    [Fact]
    public void A_listing_cut_short_without_a_way_to_continue_is_an_error_not_a_partial_answer()
    {
        const string xml = "<ListBucketResult><IsTruncated>true</IsTruncated><Contents><Key>anchors/a.json</Key></Contents></ListBucketResult>";

        Assert.Throws<InvalidDataException>(() => R2AuditAnchorSink.ParseListing(xml));
    }

    // --- When the store is not there ------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task An_error_answer_is_an_IOException_that_says_the_status_and_nothing_else(HttpStatusCode status)
    {
        var (sink, _) = Make(handler => handler.Fail = status);

        var write = await Assert.ThrowsAsync<IOException>(() => sink.WriteAsync(Anchor()));
        var read = await Assert.ThrowsAsync<IOException>(() => sink.ReadAllAsync());

        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), write.Message, StringComparison.Ordinal);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), read.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretAccessKey, write.ToString() + read, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_store_that_cannot_be_reached_is_an_IOException_so_the_verifier_reports_it_instead_of_failing()
    {
        var (sink, _) = Make(handler => handler.Throw = new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<IOException>(() => sink.WriteAsync(Anchor()));
        await Assert.ThrowsAsync<IOException>(() => sink.ReadAllAsync());
    }

    [Fact]
    public async Task A_timeout_is_an_IOException_too()
    {
        var (sink, _) = Make(handler => handler.Throw = new TimeoutException());

        await Assert.ThrowsAsync<IOException>(() => sink.ReadAllAsync());
    }

    // --- Which store the application picks ----------------------------------------------------------------------

    [Fact]
    public void With_the_bucket_configured_the_application_writes_anchors_to_it()
    {
        using var factory = ApiFactory.WithoutDatabase(environmentName: "Production", realAnchorSink: true);

        var sink = factory.Services.GetRequiredService<IAuditAnchorSink>();

        Assert.IsType<R2AuditAnchorSink>(sink);
        Assert.True(sink.IsConfigured);
    }

    [Theory]
    [InlineData("Audit:R2:Endpoint")]
    [InlineData("Audit:R2:Bucket")]
    [InlineData("Audit:R2:AccessKeyId")]
    [InlineData("Audit:R2:SecretAccessKey")]
    public void A_half_configured_bucket_stops_startup_in_any_environment_and_names_what_is_missing(string missing)
    {
        // Testing, not Production: the bucket is optional there, but once any of it is set, all of it has to be.
        using var factory = ApiFactory.WithoutDatabase(settings =>
        {
            settings["Audit:R2:Endpoint"] = "https://account123.r2.cloudflarestorage.com";
            settings["Audit:R2:Bucket"] = "anchors-bucket";
            settings["Audit:R2:AccessKeyId"] = "AKID-TEST";
            settings["Audit:R2:SecretAccessKey"] = SecretAccessKey;
            settings.Remove(missing);
        });

        var failure = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(failure);
        Assert.Contains(missing, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretAccessKey, failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://account123.r2.cloudflarestorage.com")]
    [InlineData("https://account123.r2.cloudflarestorage.com/some-bucket")]
    [InlineData("account123.r2.cloudflarestorage.com")]
    public void The_endpoint_has_to_be_an_https_address_without_a_path(string endpoint)
    {
        var options = new AuditR2Settings { Endpoint = endpoint, Bucket = "anchors-bucket", AccessKeyId = "a", SecretAccessKey = "b" };

        var problem = options.Problem();

        Assert.NotNull(problem);
        Assert.Contains("Audit:R2:Endpoint", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("Has-Capitals")]
    [InlineData("-leading-hyphen")]
    [InlineData("under_score")]
    public void The_bucket_name_has_to_be_a_valid_one(string bucket)
    {
        var options = new AuditR2Settings { Endpoint = "https://account123.r2.cloudflarestorage.com", Bucket = bucket, AccessKeyId = "a", SecretAccessKey = "b" };

        Assert.Contains("Audit:R2:Bucket", options.Problem(), StringComparison.Ordinal);
    }
}
