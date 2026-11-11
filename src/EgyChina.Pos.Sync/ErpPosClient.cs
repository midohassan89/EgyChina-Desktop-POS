using System.Net.Http.Headers;
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
/// Speaks <c>GET /api/products</c> and checkout POST.
/// Prefers public <c>/api/store/pos-checkout</c> (no device Bearer). Optional Bearer still works for
/// protected <c>/api/checkout</c> when a token is present.
/// </summary>
public sealed class ErpPosClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _baseAddress;
    private readonly bool _ownsClient;
    private readonly bool _useStoreCheckout;

    public ErpPosClient(Uri baseAddress, string? deviceBearerToken = null, HttpClient? httpClient = null)
    {
        _baseAddress = baseAddress;
        _useStoreCheckout = string.IsNullOrWhiteSpace(deviceBearerToken);
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            _ownsClient = true;
        }
        else
        {
            _http = httpClient;
            _ownsClient = false;
        }

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("EgyChina-POS/0.1");
        _http.DefaultRequestHeaders.Remove("Authorization");
        _http.DefaultRequestHeaders.Remove("Cookie");
        if (!_useStoreCheckout)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceBearerToken!.Trim());
    }

    public Uri CheckoutUri =>
        _useStoreCheckout ? Combine("api/store/pos-checkout") : Combine("api/checkout");

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

    public Uri QueueTicketUri(string code) =>
        Combine("api/pos/queue-tickets/" + Uri.EscapeDataString(code.Trim()));

    public Uri LoyaltySearchUri(string phone)
    {
        var uri = Combine("api/admin/customers/search");
        var builder = new UriBuilder(uri)
        {
            Query = "phone=" + Uri.EscapeDataString(phone.Trim()),
        };
        return builder.Uri;
    }

    public Uri OnlineOrdersUri => Combine("api/pos/online-orders");

    public Uri ShiftOpenUri => Combine("api/shifts/open");

    public Uri ShiftCloseUri => Combine("api/shifts/close");

    public Uri ShiftCurrentUri(string? cashierUserId)
    {
        var uri = Combine("api/shifts/current");
        if (string.IsNullOrWhiteSpace(cashierUserId))
            return uri;
        var builder = new UriBuilder(uri)
        {
            Query = "cashierUserId=" + Uri.EscapeDataString(cashierUserId.Trim()),
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

    public async Task<ErpCallResult> GetQueueTicketAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(QueueTicketUri(code), cancellationToken).ConfigureAwait(false);
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

    public async Task<ErpCallResult> PatchQueueTicketAsync(string code, string json, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Patch, QueueTicketUri(code)) { Content = content };
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

    public async Task<ErpCallResult> GetLoyaltyCustomerAsync(string phone, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(LoyaltySearchUri(phone), cancellationToken).ConfigureAwait(false);
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

    public async Task<ErpCallResult> GetOnlineOrdersAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(OnlineOrdersUri, cancellationToken).ConfigureAwait(false);
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

    public async Task<ErpCallResult> PostShiftOpenAsync(string json, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(ShiftOpenUri, content, cancellationToken).ConfigureAwait(false);
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

    public async Task<ErpCallResult> PostShiftCloseAsync(string json, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(ShiftCloseUri, content, cancellationToken).ConfigureAwait(false);
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

    public async Task<ErpCallResult> GetShiftCurrentAsync(string? cashierUserId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(ShiftCurrentUri(cashierUserId), cancellationToken)
                .ConfigureAwait(false);
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
