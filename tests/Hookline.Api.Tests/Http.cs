using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Hookline.Api.Tests;

public static class Http
{
    public static readonly DateTimeOffset Now = new(2026, 11, 18, 13, 0, 0, TimeSpan.Zero);

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, HooklineApp.Json, TestContext.Current.CancellationToken);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, HooklineApp.Json)!;
    }

    public static async Task<T> GetOkAsync<T>(this HttpClient client, string url) =>
        await (await client.GetAsync(url, TestContext.Current.CancellationToken)).ReadAsync<T>(HttpStatusCode.OK);
}
