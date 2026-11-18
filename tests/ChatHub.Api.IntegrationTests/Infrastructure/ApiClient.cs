using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChatHub.Api.IntegrationTests.Infrastructure;

public sealed class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpClient Http { get; } = http;

    public Task<ApiResponse> GetAsync(string path, string? token = null) => SendAsync(HttpMethod.Get, path, null, token);

    public Task<ApiResponse> PostAsync(string path, object? body, string? token = null) => SendAsync(HttpMethod.Post, path, body, token);

    public Task<ApiResponse> PutAsync(string path, object? body, string? token) => SendAsync(HttpMethod.Put, path, body, token);

    public Task<ApiResponse> DeleteAsync(string path, string? token) => SendAsync(HttpMethod.Delete, path, null, token);

    public async Task<ApiResponse> SendAsync(HttpMethod method, string path, object? body, string? token)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return await SendAsync(request);
    }

    public async Task<ApiResponse> UploadAsync(byte[] bytes, string fileName, string contentType, string? token,
        string purpose = "MessageAttachment")
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/files?purpose={purpose}") { Content = form };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(request);
    }

    public async Task<ApiResponse> SendAsync(HttpRequestMessage request) => await ApiResponse.FromAsync(await Http.SendAsync(request));
}

public sealed record ApiResponse(int Status, string Text, JsonElement Json, HttpResponseMessage Message)
{
    public static async Task<ApiResponse> FromAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var json = text.Length > 0 && text[0] is '{' or '[' ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return new ApiResponse((int)response.StatusCode, text, json, response);
    }

    public string? Code => Json.S("code");

    public bool IsProblem =>
        Message.Content.Headers.ContentType?.MediaType == "application/problem+json" &&
        Json.P("status").ValueKind == JsonValueKind.Number &&
        Json.S("traceId") is not null;

    public string? Header(string name) =>
        Message.Headers.TryGetValues(name, out var values) || Message.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : null;

    public bool HasError(string key) => Json.P("errors").P(key).ValueKind == JsonValueKind.Array;
}

public static class JsonElementExtensions
{
    public static JsonElement P(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    public static string? S(this JsonElement element, string name) =>
        element.P(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static Guid G(this JsonElement element, string name) => Guid.TryParse(element.S(name), out var id) ? id : Guid.Empty;

    public static bool B(this JsonElement element, string name) => element.P(name).ValueKind == JsonValueKind.True;
}
