using System.Net;
using System.Text;
using System.Text.Json;

namespace EgyChina.Pos.Sync;

public sealed record ErpCallResult(bool ResponseCompleted, int? StatusCode, string? Error, bool TransportFailed, string? Body)
{
    public bool Accepted => ResponseCompleted && StatusCode is >= 200 and < 300;

    public bool Unauthorized => ResponseCompleted && StatusCode == 401;

    public static ErpCallResult Transport(string error) => new(false, null, error, true, null);

    public static ErpCallResult Completed(int statusCode, string? error, string? body) => new(true, statusCode, error, false, body);
}

/// <summary>
/// Speaks only the calls present in the production browser bundle:
/// GET /api/products and POST /api/checkout. Auth is the Auth.js cookie the browser already sends.
/// A sale is accepted only when the HTTP response is fully received and the status is 2xx.
/// </summary>
public sealed class ErpPosClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _baseAddress;
    private readonly bool _ownsClient;

    public ErpPosClient(Uri baseAddress, string sessionCookie, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(sessionCookie))
            throw new ArgumentException("Session cookie is required.", nameof(sessionCookie));
        _baseAddress = baseAddress;
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _ownsClient = true;
        }
        else
        {
            _http = httpClient;
            _ownsClient = false;
        }

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("EgyChina-POS/0.1");
        _http.DefaultRequestHeaders.Remove("Cookie");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", sessionCookie);
    }

    public Uri CheckoutUri => Combine("api/checkout");

    public Uri ProductsUri(string? updatedSince)
    {
        var uri = Combine("api/products");
        if (string.IsNullOrWhiteSpace(updatedSince))
            return uri;
        var builder = new UriBuilder(uri)
        {
            Query = "updatedSince=" + Uri.EscapeDataString(updatedSince),
        };
        return builder.Uri;
    }

    public async Task<ErpCallResult> GetProductsAsync(string? updatedSince, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(ProductsUri(updatedSince), cancellationToken).ConfigureAwait(false);
            return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return ErpCallResult.Transport(ex.Message);
        }
    }

    public async Task<ErpCallResult> PostCheckoutAsync(string json, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(CheckoutUri, content, cancellationToken).ConfigureAwait(false);
            return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return ErpCallResult.Transport(ex.Message);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }

    private Uri Combine(string relative)
    {
        var text = _baseAddress.ToString();
        if (!text.EndsWith('/'))
            text += "/";
        return new Uri(new Uri(text, UriKind.Absolute), relative);
    }

    private static async Task<ErpCallResult> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return ErpCallResult.Transport(ex.Message);
        }

        var code = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
            return ErpCallResult.Completed(code, null, body);

        return ErpCallResult.Completed(code, ReadError(body) ?? $"HTTP {code}", body);
    }

    private static string? ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
