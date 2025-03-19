using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

namespace CleareAzureBoard;

class Program
{
    static async Task Main(string[] args)
    {
        var config = LoadConfiguration();
        string organization = config["AzureDevOps:Organization"];
        string project = config["AzureDevOps:Project"];
        string pat = config["AzureDevOps:PersonalAccessToken"];

        HttpClient client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

        List<string> workItemTypes = new List<string> {"Bug", "User Story", "Release", "Chore", "Feature", "Epic" };

        foreach (var type in workItemTypes)
        {
            await DeleteWorkItemsByType(client, organization, project, type);
        }
    }

    static async Task DeleteWorkItemsByType(HttpClient client, string organization, string project, string workItemType)
    {
        Console.WriteLine($"Searching for {workItemType}s...");

        string queryUrl = $"https://dev.azure.com/{organization}/{project}/_apis/wit/wiql?api-version=7.1-preview.2";
        string queryJson = $@"
        {{
            ""query"": ""SELECT [System.Id] FROM WorkItems WHERE [System.WorkItemType] = '{workItemType}'""
        }}";

        var content = new StringContent(queryJson, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await client.PostAsync(queryUrl, content);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Failed to fetch {workItemType}s: {response.ReasonPhrase}");
            return;
        }

        string responseString = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(responseString);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("workItems", out JsonElement workItems) || workItems.GetArrayLength() == 0)
        {
            Console.WriteLine($"No {workItemType}s found.");
            return;
        }

        List<int> workItemIds = new List<int>();
        foreach (JsonElement item in workItems.EnumerateArray())
        {
            workItemIds.Add(item.GetProperty("id").GetInt32());
        }

        Console.WriteLine($"Found {workItemIds.Count} {workItemType}s. Deleting...");

        foreach (int id in workItemIds)
        {
            await DeleteWorkItem(client, organization, project, id);
        }
    }

    static async Task DeleteWorkItem(HttpClient client, string organization, string project, int workItemId)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{workItemId}?api-version=7.1-preview.2";
        HttpResponseMessage response = await client.DeleteAsync(url);

        if (response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Deleted Work Item {workItemId}");
        }
        else
        {
            Console.WriteLine($"Failed to delete Work Item {workItemId}: {response.ReasonPhrase}");
        }
    }

    #region Configuration
    static IConfiguration LoadConfiguration()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\.."));

        return new ConfigurationBuilder()
        .SetBasePath(projectRoot)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .Build();
    }

    #endregion
}
