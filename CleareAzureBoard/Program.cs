using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AzureEPIC
{
    class Program
    {
        static async Task Main()
        {
            string organization = "CirruscloudSystems";
            string project = "Test";
            string pat = "FChclBho8h2zKK9z8JbEMjxyiOnVLh09TcbcrLvY8oUj7lBQGhYoJQQJ99BCACAAAAAbOY6VAAASAZDO15Am";

            var workItemTypesToDelete = new List<string> { "UserStory", "Release", "Bug", "Chore", "Feature", "Epic"};

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

                foreach (var workItemType in workItemTypesToDelete)
                {
                    await DeleteWorkItemsByType(client, organization, project, workItemType);
                }
            }
        }

        static async Task DeleteWorkItemsByType(HttpClient client, string organization, string project, string workItemType)
        {
            string queryUrl = $"https://dev.azure.com/{organization}/{project}/_apis/wit/wiql?api-version=7.1";

            var query = new
            {
                query = $"SELECT [System.Id], [System.Title], [System.State] FROM WorkItems " +
                        $"WHERE [System.WorkItemType] = '{workItemType}' " + $"AND [System.State] NOT IN ('Closed', 'Removed')"
            };

            var content = new StringContent(JsonSerializer.Serialize(query), Encoding.UTF8, "application/json");
            HttpResponseMessage queryResponse = await client.PostAsync(queryUrl, content);

            if (!queryResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ [ERROR] Failed to retrieve {workItemType} work items. Status: {queryResponse.StatusCode}");
                return;
            }

            var queryResult = JsonSerializer.Deserialize<QueryResult>(await queryResponse.Content.ReadAsStringAsync());

            if (queryResult.WorkItems.Count == 0)
            {
                Console.WriteLine($"✅ No {workItemType} work items found.");
                return;
            }

            Console.WriteLine($"🔍 Found {queryResult.WorkItems.Count} {workItemType} work items. Listing them:");

            foreach (var workItem in queryResult.WorkItems)
            {
                Console.WriteLine($"📌 ID: {workItem.Id}");
            }

            Console.WriteLine("Proceeding with deletion...");

            foreach (var workItem in queryResult.WorkItems)
            {
                await DeleteWorkItem(client, organization, project, workItem.Id);
            }
        }

        static async Task DeleteWorkItem(HttpClient client, string organization, string project, int workItemId)
        {
            string deleteUrl = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{workItemId}?api-version=7.1";

            HttpResponseMessage deleteResponse = await client.DeleteAsync(deleteUrl);

            if (deleteResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"🗑️ Work item {workItemId} deleted successfully.");
            }
            else
            {
                Console.WriteLine($"❌ [ERROR] Failed to delete work item {workItemId}. Status: {deleteResponse.StatusCode}");
            }
        }
    }

    public class QueryResult
    {
        public List<WorkItemReference> WorkItems { get; set; } = new List<WorkItemReference>();
    }

    public class WorkItemReference
    {
        public int Id { get; set; }
    }

}
