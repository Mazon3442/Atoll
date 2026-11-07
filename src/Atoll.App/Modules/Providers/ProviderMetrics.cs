using System.Net.Http;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Atoll.Modules.Providers;

public static class ProviderMetrics
{
    public static async Task<string> ReadAsync(ProviderEndpoint provider, HttpClient client, CancellationToken ct)
    {
        if (provider.IsDeepSeek)
        {
            using var json = await Get(client, "https://api.deepseek.com/user/balance", provider.Key, ct);
            if (!json.RootElement.TryGetProperty("balance_infos", out var balances) || balances.ValueKind != JsonValueKind.Array) return "Provider did not return a balance.";
            var values = new List<string>();
            foreach (var balance in balances.EnumerateArray())
            {
                if (balance.TryGetProperty("currency", out var currency) && currency.ValueKind == JsonValueKind.String
                    && balance.TryGetProperty("total_balance", out var total) && total.ValueKind == JsonValueKind.String
                    && decimal.TryParse(total.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                    values.Add($"{amount.ToString("0.00", CultureInfo.InvariantCulture)} {currency.GetString()}");
            }
            return values.Count == 0 ? "Provider did not return a balance." : "Provider balance: " + string.Join(" · ", values);
        }
        if (!provider.IsOpenAI) return "Provider account metrics unavailable. Gateway totals are tracked locally.";
        if (string.IsNullOrWhiteSpace(provider.AdminKey)) return "OpenAI organization usage and costs need a separate admin key. Gateway totals work with your project key.";
        var start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).ToUnixTimeSeconds();
        long input = 0, output = 0; var costs = new Dictionary<string, decimal>();
        foreach (var kind in new[] { "usage/completions", "costs" })
        {
            string? page = null; var pages = new HashSet<string>();
            do
            {
                var url = $"https://api.openai.com/v1/organization/{kind}?start_time={start}&bucket_width=1d&limit=1" + (page is null ? "" : "&page=" + Uri.EscapeDataString(page));
                using var json = await Get(client, url, provider.AdminKey, ct);
                foreach (var bucket in json.RootElement.GetProperty("data").EnumerateArray())
                    foreach (var result in bucket.GetProperty("results").EnumerateArray())
                    {
                        if (kind == "costs")
                        {
                            var amount = result.GetProperty("amount");
                            var currency = amount.GetProperty("currency").GetString() ?? "unknown";
                            costs[currency] = costs.GetValueOrDefault(currency) + amount.GetProperty("value").GetDecimal();
                        }
                        else { input += result.GetProperty("input_tokens").GetInt64(); output += result.GetProperty("output_tokens").GetInt64(); }
                    }
                page = json.RootElement.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True
                    && json.RootElement.TryGetProperty("next_page", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
                if (page is not null && (!pages.Add(page) || pages.Count > 100)) throw new InvalidDataException("Provider pagination did not finish.");
            } while (page is not null);
        }
        var costText = costs.Count == 0 ? "no reported costs" : string.Join(" · ", costs.Select(c => c.Value.ToString("0.00", CultureInfo.InvariantCulture) + " " + c.Key));
        return $"OpenAI organization today (UTC): {input:N0} input · {output:N0} output tokens · {costText}. Provider reporting can lag.";
    }
    private static async Task<JsonDocument> Get(HttpClient client, string url, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return await Error(response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("Provider metrics response was too large.");
            bytes.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(bytes.ToArray());
    }
    private static Task<JsonDocument> Error(System.Net.HttpStatusCode code) => throw new HttpRequestException("Provider metrics request failed.", null, code);
}
