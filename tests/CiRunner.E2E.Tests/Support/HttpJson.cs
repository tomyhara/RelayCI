using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CiRunner.E2E.Tests.Support;

public static class HttpJson
{
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
    {
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return client.PostAsync(url, content);
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }
}
