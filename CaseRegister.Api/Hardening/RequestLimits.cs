using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace CaseRegister.Api.Hardening;

/// <summary>The size and time limits, all stated here. An endpoint that needs more says so with <see cref="RequestBodyLimitAttribute"/>.</summary>
public static class RequestLimits
{
    /// <summary>Every JSON request body: 64 KiB.</summary>
    public const long DefaultBodyBytes = 64 * 1024;

    /// <summary>Sign-in, sign-up, two-factor and setup requests are a few short fields: 8 KiB.</summary>
    public const long AuthBodyBytes = 8 * 1024;

    public const int MaxRequestHeadersTotalSize = 16 * 1024;
    public const int MaxRequestHeaderCount = 50;
    public const int MaxRequestLineSize = 4 * 1024;
    public static readonly TimeSpan RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a request may take in all before it is cut off.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>JSON nested deeper than this is refused.</summary>
    public const int MaxJsonDepth = 16;

    public static void ConfigureKestrel(KestrelServerOptions options)
    {
        options.AddServerHeader = false; // Default: true. The server software is nobody's business.
        options.Limits.MaxRequestBodySize = DefaultBodyBytes; // Default: about 30 MB.
        options.Limits.MaxRequestHeadersTotalSize = MaxRequestHeadersTotalSize; // Default: 32 KiB.
        options.Limits.MaxRequestHeaderCount = MaxRequestHeaderCount; // Default: 100.
        options.Limits.MaxRequestLineSize = MaxRequestLineSize; // Default: 8 KiB.
        options.Limits.RequestHeadersTimeout = RequestHeadersTimeout; // Default: 30 seconds.
        options.Limits.KeepAliveTimeout = KeepAliveTimeout; // Default: 130 seconds.
    }
}

/// <summary>Sets the largest request body an action or controller accepts, in place of <see cref="RequestLimits.DefaultBodyBytes"/>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequestBodyLimitAttribute(long maxBytes) : Attribute, IRequestBodyLimitMetadata
{
    public long MaxBytes { get; } = maxBytes;
}

public interface IRequestBodyLimitMetadata
{
    long MaxBytes { get; }
}

/// <summary>
/// Enforces the body limit on every request, whatever server is underneath: a declared length that is too large is refused
/// at once with 413, and a body that turns out to be larger as it is read (chunked, or a lie about the length) is cut off.
/// </summary>
public sealed class RequestBodyLimitMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var limit = context.GetEndpoint()?.Metadata.GetMetadata<IRequestBodyLimitMetadata>()?.MaxBytes ?? RequestLimits.DefaultBodyBytes;

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = limit;
        }

        if (context.Request.ContentLength is { } declared && declared > limit)
        {
            throw new BadHttpRequestException("The request body is too large.", StatusCodes.Status413PayloadTooLarge);
        }

        context.Request.Body = new LimitedReadStream(context.Request.Body, limit);

        return next(context);
    }
}

/// <summary>A read-only view of the request body that fails with 413 once more than the limit has been read.</summary>
internal sealed class LimitedReadStream(Stream inner, long limit) : Stream
{
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Count(int read)
    {
        _read += read;
        if (_read > limit)
        {
            throw new BadHttpRequestException("The request body is too large.", StatusCodes.Status413PayloadTooLarge);
        }

        return read;
    }
}

/// <summary>A request that carries a body must say it is JSON. Forms, XML, text and anything unlabelled get 415.</summary>
public sealed class JsonBodyOnlyFilter : IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var request = context.HttpContext.Request;
        var hasBody = request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0;

        if (hasBody && !IsJson(request.ContentType))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status415UnsupportedMediaType,
                Title = ApiExceptionHandler.TitleFor(StatusCodes.Status415UnsupportedMediaType),
            })
            {
                StatusCode = StatusCodes.Status415UnsupportedMediaType,
            };
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }

    internal static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
        && (parsed.CharSet is null || string.Equals(parsed.CharSet, "utf-8", StringComparison.OrdinalIgnoreCase));
}
