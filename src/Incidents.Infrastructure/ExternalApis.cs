using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Incidents.Application.Abstractions;

namespace Incidents.Infrastructure;

public record GitHubOptions(string Owner, string Repo, string Token);

public record SlackOptions(string BotToken);

// Fine-grained PAT scoped to ONE repo with only "Issues: read & write". Token comes from config
// (user-secrets / local.settings.json locally, Key Vault reference in Azure) - never from source.
public class GitHubClient(HttpClient http, GitHubOptions o) : IGitHubClient
{
    public static void Configure(HttpClient http, GitHubOptions o)
    {
        http.BaseAddress = new Uri("https://api.github.com/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.Token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("incident-intake/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<int> CreateIssueAsync(string title, string body, IEnumerable<string> labels, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync($"repos/{o.Owner}/{o.Repo}/issues", new { title, body, labels }, ct);
        res.EnsureSuccessStatusCode(); // failure -> exception -> queue retry -> poison queue
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("number").GetInt32();
    }
}

// Bot token scope: chat:write only (plus app_mentions:read for the event subscription).
public class SlackClient(HttpClient http) : ISlackClient
{
    public static void Configure(HttpClient http, SlackOptions o)
    {
        http.BaseAddress = new Uri("https://slack.com/api/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.BotToken);
    }

    public async Task PostAsync(string channel, string threadTs, string text, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync("chat.postMessage", new { channel, thread_ts = threadTs, text }, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!json.GetProperty("ok").GetBoolean()) // Slack returns HTTP 200 with ok=false on errors
            throw new HttpRequestException($"Slack error: {json.GetProperty("error").GetString()}");
    }
}
