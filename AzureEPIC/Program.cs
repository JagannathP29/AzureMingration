using CsvHelper;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

class Program
{
    private static int processedCount = 0;

    static async Task Main(string[] args)
    {
        var config = LoadConfiguration();
        string organization = config["AzureDevOps:Organization"];
        string project = config["AzureDevOps:Project"];
        string pat = config["AzureDevOps:PersonalAccessToken"];
        string csvFilePath = config["FilePaths:CsvFilePath"];
        string attachmentPath = config["AttachmentSettings:AttachmentPath"];

        var records = ReadCsvFile(csvFilePath);
        using var client = CreateHttpClient(pat);
        string choreParentFeatureId = null;

        List<string> azurePTIds = await GetAzurePTStoryValues(client, organization, project);
        records = records.OrderBy(f => f.CreatedAt).ToList();

        //var epics = records.Where(r => r.Type.Equals("epic", StringComparison.OrdinalIgnoreCase)).ToList();
        //var workItems = records.Where(r => r.Type is "feature" or "bug" or "chore" or "release").ToList();
        //var chores = workItems.Where(w => w.Type.Equals("chore", StringComparison.OrdinalIgnoreCase)).ToList();

        List<WorkItem> epics = new List<WorkItem>();
        List<WorkItem> workItems = new List<WorkItem>(); //Feature, Bug, Release
        List<WorkItem> chores = new List<WorkItem>();

        foreach (var record in records)
        {
            switch (record.Type.ToLower())
            {
                case "epic":
                    epics.Add(record);
                    break;
                case "feature":
                case "bug":
                case "chore":
                case "release":
                    workItems.Add(record);
                    if (record.Type.Equals("chore", StringComparison.OrdinalIgnoreCase))
                    {
                        chores.Add(record);
                    }
                    break;
            }
        }

        foreach (var epic in epics)
        {
            if (azurePTIds.Contains(epic.Id)) continue;

            string epicId = await CreateWorkItem(client, organization, project, "Feature", epic, null, attachmentPath);
            if (!string.IsNullOrEmpty(epicId)) 
            {
                Console.WriteLine($"Feature - '{epic.Title}' created with ID - {epicId}");
                processedCount++;
            }

            var childItems = workItems.Where(w => Normalize(w.Labels) == Normalize(epic.Title) && !w.Type.Equals("Chore", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.CreatedAt).ToList();

            foreach (var child in childItems)
            {
                if (azurePTIds.Contains(child.Id)) continue;

                string childId = await CreateWorkItem(client, organization, project, MapWorkItemType(child.Type), child, epicId, attachmentPath);

                if (!string.IsNullOrEmpty(childId))
                {
                    Console.WriteLine($"{MapWorkItemType(child.Type)} created under Epic '{epic.Title}'");
                    processedCount++;
                }
            }
        }

        if (chores.Any())
        {
            choreParentFeatureId = await CreateChoreParentFeature(client, organization, project);

            foreach (var chore in chores)
            {
                if (azurePTIds.Contains(chore.Id)) continue;

                string choreId = await CreateWorkItem(client, organization, project, "User Story", chore, choreParentFeatureId, attachmentPath);
                if (!string.IsNullOrEmpty(choreId))
                {
                    Console.WriteLine($"Chore '{chore.Title}' linked to Chore Parent Feature");
                    processedCount++;
                }
            }
        }

        Console.WriteLine($"{processedCount} items processed successfully of {records.Count} rows.");
    }

    #region Create Workitems
    
    private static async Task<string> CreateChoreParentFeature(HttpClient client, string organization, string project)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/$Feature?api-version=7.1";
        var requestBody = new List<object>
        {
            new { op = "add", path = "/fields/System.Title", value = "Chore Parent Feature" },
            new { op = "add", path = "/fields/System.Description", value = "This feature contains all Chore items" }
        };

        var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json-patch+json");
        var response = await client.PostAsync(url, content);

        if (!response.IsSuccessStatusCode)
        {
            LogError($"❌ [ERROR] Failed to create Chore Parent Feature.");
            return null;
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(responseBody);
        int responseId = json.RootElement.GetProperty("id").GetInt32();
        return responseId.ToString();
    }

    private static async Task<string> CreateWorkItem(HttpClient client, string organization, string project, string type, WorkItem workItem, string parentId = null, string attachmentPath = null)
    {
        try
        {
            List<string> azureUsers = await GetAzureDevOpsUsers(client, organization, project);
            string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/${type}?api-version=7.1";
            var requestBody = new List<object>
            {
                new { op = "add", path = "/fields/System.Title", value = workItem.Title },
                new { op = "add", path = "/fields/Custom.PTStory", value = workItem.Id }
            };

            if (!string.IsNullOrEmpty(workItem.Description))
            {
                string htmlDescription = $"<pre><code>{System.Net.WebUtility.HtmlEncode(workItem.Description)}</code></pre>";

                requestBody.Add(new
                {
                    op = "add",
                    path = "/fields/System.Description",
                    value = htmlDescription
                });
            }

            if (!string.IsNullOrEmpty(workItem.Priority))
            {
                requestBody.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Common.Priority", value = MapPriority(workItem.Priority) });
            }

            if (!string.IsNullOrEmpty(parentId))
            {
                requestBody.Add(new
                {
                    op = "add",
                    path = "/relations/-",
                    value = new
                    {
                        rel = "System.LinkTypes.Hierarchy-Reverse",
                        url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{parentId}"
                    }
                });
            }

            if (workItem.Estimate.HasValue)
            {
                requestBody.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.Effort", value = workItem.Estimate.Value });
            }

            if (workItem.AcceptedAt.HasValue)
            {
                requestBody.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.StartDate", value = workItem.AcceptedAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") });
            }

            if (workItem.Deadline.HasValue)
            {
                requestBody.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.TargetDate", value = workItem.Deadline.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") });
            }

            string assignedTo = null;
            if (!string.IsNullOrEmpty(workItem.OwnedBy1))
            {
                string firstName1 = workItem.OwnedBy1.Split(' ')[0];
                assignedTo = azureUsers.FirstOrDefault(user => user.StartsWith(firstName1, StringComparison.OrdinalIgnoreCase));
            }
            if (assignedTo == null && !string.IsNullOrEmpty(workItem.OwnedBy2))
            {
                string firstName2 = workItem.OwnedBy2.Split(' ')[0];
                assignedTo = azureUsers.FirstOrDefault(user => user.StartsWith(firstName2, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(assignedTo))
            {
                requestBody.Add(new { op = "add", path = "/fields/System.AssignedTo", value = assignedTo });
            }

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json-patch+json");
            var response = await client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                string errorMessage = $"Failed to create {type} '{workItem.Title}'. Status: {response.StatusCode}";
                LogError(errorMessage);
                return null;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(responseBody);
            int responseId = json.RootElement.GetProperty("id").GetInt32();

            // **Step 1: Update Work Item State**
            if (!string.IsNullOrEmpty(workItem.CurrentState))
            {
                await UpdateWorkItemState(client, organization, project, responseId.ToString(), MapState(workItem.CurrentState));
            }

            // **Step 2: Check Parent State After Updating Child**
            if (!string.IsNullOrEmpty(parentId))
            {
                await UpdateParentState(client, organization, project, parentId);
            }

            await ProcessCommemnts(client, organization, project, responseId.ToString(), workItem.Comments);
            await ProcessAttachments(client, organization, project,responseId.ToString(), workItem.Id, attachmentPath);

            return responseId.ToString();
        }
        catch (Exception ex)
        {
            LogError($"Exception while creating {type} '{workItem.Title}': {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Update State

    static async Task UpdateParentState(HttpClient client, string organization, string project, string parentId)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{parentId}?$expand=relations&api-version=7.1";
        HttpResponseMessage response = await client.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            LogError($"[ERROR] Failed to fetch parent work item {parentId}. Status: {response.StatusCode}");
            return;
        }

        string responseBody = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(responseBody);

        if (!json.RootElement.TryGetProperty("relations", out JsonElement relations))
        {
            LogError($"[INFO] Work item {parentId} has no child items.");
            return;
        }

        bool anyActiveChild = false;
        bool allClosed = true;
        bool allNew = true;
        bool allResolved = true;

        foreach (var relation in relations.EnumerateArray())
        {
            if (relation.GetProperty("rel").GetString() == "System.LinkTypes.Hierarchy-Forward")
            {
                string childUrl = relation.GetProperty("url").GetString();
                string childId = childUrl.Split('/').Last();

                string childState = await GetWorkItemState(client, organization, project, childId);

                // Check if any child is Active
                if (childState == "Active")
                {
                    anyActiveChild = true;
                }

                // Track if all children are Closed, New, or Resolved
                if (childState != "Closed") allClosed = false;
                if (childState != "New") allNew = false;
                if (childState != "Resolved") allResolved = false;
            }
        }

        string parentState;

        // Determine the parent's state based on the child states
        if (anyActiveChild)
        {
            parentState = "Active";
        }
        else if (allClosed)
        {
            parentState = "Closed";
        }
        else if (allNew)
        {
            parentState = "New";
        }
        else if (allResolved)
        {
            parentState = "Resolved";
        }
        else
        {
            parentState = "Active"; // Default to Active if a mix of states exist
        }

        // Update the parent's state
        await UpdateWorkItemState(client, organization, project, parentId, parentState);
    }

    static async Task UpdateWorkItemState(HttpClient client, string organization, string project, string workItemId, string newState)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{workItemId}?api-version=7.1";

        var patchDocument = new[]
        {
            new { op = "add", path = "/fields/System.State", value = newState }
        };

        var content = new StringContent(JsonSerializer.Serialize(patchDocument), Encoding.UTF8, "application/json-patch+json");
        HttpResponseMessage response = await client.PatchAsync(url, content);

        if (!response.IsSuccessStatusCode)
        {
            LogError($"[ERROR] Failed to update work item {workItemId} to state '{newState}'. Status: {response.StatusCode}");
        }
    }

    static async Task<string> GetWorkItemState(HttpClient client, string organization, string project, string workItemId)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{workItemId}?fields=System.State&api-version=7.1";
        HttpResponseMessage response = await client.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            LogError($"[ERROR] Failed to fetch work item {workItemId}. Status: {response.StatusCode}");
            return null;
        }

        string responseBody = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(responseBody);

        return json.RootElement.GetProperty("fields").GetProperty("System.State").GetString();
    }

    #endregion

    #region Attach Comments & Attachments

    static async Task ProcessCommemnts(HttpClient client, string organization, string project, string workItemId, List<string> comments)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{workItemId}?api-version=7.1";

        if (comments == null || comments.Count == 0)
        {
            LogError($"[INFO] No comments provided to {workItemId}");
            return;
        }

        foreach (var comment in comments)
        {
            string formattedComment = "<b>Migrated from Pivotal Tracker</b><br><br>" + comment;

            var patchData = new List<object>
            {
                new { op = "add", path = "/fields/System.History", value = formattedComment }
            };

            var json = JsonSerializer.Serialize(patchData);
            var content = new StringContent(json, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json-patch+json");

            HttpResponseMessage response = await client.PatchAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                LogError($"Exception while adding comment - {formattedComment} of Work Item {workItemId}");
                return;
            }
        }
    }

    static async Task ProcessAttachments(HttpClient client, string organization, string project, string pkId, string workItemId, string baseFolderPath)
    {
        string workItemFolder = Path.Combine(baseFolderPath, workItemId);

        if (Directory.Exists(workItemFolder))
        {
            string[] files = Directory.GetFiles(workItemFolder);

            foreach (string file in files)
            {
                string attachmentUrl = await UploadAttachment(client, organization, project, file);

                if (!string.IsNullOrEmpty(attachmentUrl))
                {
                    bool success = await AttachFileToWorkItem(client, organization, project, pkId, attachmentUrl);
                    if (!success)
                    {
                        LogError($"Failed to attach {file} to Work Item {pkId}");
                        return;
                    }
                }
            }
        }
        else
        {
            LogError($"[INFO] No attachments found for Work Item {pkId}");
        }
    }

    static async Task<string> UploadAttachment(HttpClient client, string organization, string project, string filePath)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/attachments?fileName={Path.GetFileName(filePath)}&api-version=7.1";

        byte[] fileBytes = File.ReadAllBytes(filePath);
        using (var content = new ByteArrayContent(fileBytes))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            HttpResponseMessage response = await client.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync();
                JsonDocument json = JsonDocument.Parse(responseBody);
                return json.RootElement.GetProperty("url").GetString();
            }
            else
            {
                LogError($"Failed to upload file {filePath}: {response.ReasonPhrase}");
                return null;
            }
        }
    }

    static async Task<bool> AttachFileToWorkItem(HttpClient client, string organization, string project, string workItemId, string attachmentUrl)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{workItemId}?api-version=7.1";

        var attachment = new List<object>
        {
            new { op = "add", path = "/relations/-", value = new { rel = "AttachedFile", url = attachmentUrl } }
        };

        var content = new StringContent(JsonSerializer.Serialize(attachment), Encoding.UTF8, "application/json-patch+json");
        HttpResponseMessage response = await client.PatchAsync(url, content);

        return response.IsSuccessStatusCode;
    }

    #endregion

    #region Azure[GET]

    static async Task<List<string>> GetAzurePTStoryValues(HttpClient client, string organization, string project)
    {
        List<string> ptStoryValues = new List<string>();
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/wiql?api-version=7.1";

        var query = new
        {
            query = "SELECT * FROM WorkItems"
        };

        var content = new StringContent(JsonSerializer.Serialize(query), Encoding.UTF8, "application/json");
        HttpResponseMessage response = await client.PostAsync(url, content);

        if (response.IsSuccessStatusCode)
        {
            string responseBody = await response.Content.ReadAsStringAsync();
            JsonDocument json = JsonDocument.Parse(responseBody);

            if (json.RootElement.TryGetProperty("workItems", out JsonElement workItems))
            {
                foreach (var workItem in workItems.EnumerateArray())
                {
                    string workItemId = workItem.GetProperty("id").ToString();
                    string ptStoryValue = await GetWorkItemField(client, organization, project, workItemId, "Custom.PTStory");

                    if (!string.IsNullOrEmpty(ptStoryValue))
                    {
                        ptStoryValues.Add(ptStoryValue);
                    }
                }
            }
        }
        else
        {
            Console.WriteLine($"[ERROR] Failed to fetch PT Story work items. Status Code: {response.StatusCode}");
        }

        return ptStoryValues;
    }

    static async Task<List<string>> GetAzureDevOpsUsers(HttpClient client, string organization, string project)
    {
        string url = $"https://vssps.dev.azure.com/{organization}/_apis/graph/users?api-version=7.1-preview.1";
        HttpResponseMessage response = await client.GetAsync(url);

        if (response.IsSuccessStatusCode)
        {
            string responseBody = await response.Content.ReadAsStringAsync();
            JsonDocument json = JsonDocument.Parse(responseBody);

            List<string> users = json.RootElement.GetProperty("value").EnumerateArray().Select(u => u.GetProperty("displayName").GetString()).ToList();

            return users;
        }
        else
        {
            Console.WriteLine($"❌ [ERROR] Failed to fetch users. Status: {response.StatusCode}");
            return new List<string>();
        }
    }

    static async Task<string> GetWorkItemField(HttpClient client, string organization, string project, string workItemId, string fieldName)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{workItemId}?api-version=7.1";

        HttpResponseMessage response = await client.GetAsync(url);

        if (response.IsSuccessStatusCode)
        {
            string responseBody = await response.Content.ReadAsStringAsync();
            JsonDocument json = JsonDocument.Parse(responseBody);

            if (json.RootElement.TryGetProperty("fields", out JsonElement fields) &&
                fields.TryGetProperty(fieldName, out JsonElement fieldValue))
            {
                return fieldValue.ToString();
            }
        }
        else
        {
            Console.WriteLine($"[ERROR] Failed to fetch work item {workItemId}. Status Code: {response.StatusCode}");
        }

        return null;
    }

    #endregion

    #region Configuration

    static IConfiguration LoadConfiguration()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\.."));

        return new ConfigurationBuilder()
        .SetBasePath(projectRoot)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .Build();
    }

    private static List<WorkItem> ReadCsvFile(string filePath)
    {
        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        csv.Read();
        csv.ReadHeader();
        var headerRow = csv.HeaderRecord;
        var records = new List<WorkItem>();

        while (csv.Read())
        {
            var workItem = new WorkItem
            {
                Id = csv.GetField("Id"),
                Title = csv.GetField("Title"),
                Labels = csv.GetField("Labels"),
                Type = csv.GetField("Type"),
                Description = csv.GetField("Description"),
                Estimate = csv.GetField<double?>("Estimate"),
                Priority = csv.GetField("Priority"),
                CurrentState = csv.GetField("CurrentState"),
                CreatedAt = csv.GetField<DateTime?>("CreatedAt"),
                AcceptedAt = csv.GetField<DateTime?>("AcceptedAt"),
                Deadline = csv.GetField<DateTime?>("Deadline"),
                OwnedBy1 = csv.GetField("OwnedBy1"),
                OwnedBy2 = csv.GetField("OwnedBy2"),
                Comments = headerRow.Where(col => col.StartsWith("Comment")).Select(col => csv.GetField(col)).Where(value => !string.IsNullOrEmpty(value)).ToList()
            };
            records.Add(workItem);
        }
        return records;
    }

    private static HttpClient CreateHttpClient(string pat)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json-patch+json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

        return client;
    }

    #endregion

    #region Mapping

    static string Normalize(string text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : new string(text.Where(c => !char.IsPunctuation(c)).ToArray()).Trim().ToLowerInvariant();
    }

    static string MapWorkItemType(string type)
    {
        var workItemTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Epic", "Feature" },
            { "Feature", "User Story" },
            { "Bug", "Bug" },
            { "Chore", "User Story" },
            { "Release", "Release" }
        };

        return workItemTypes.TryGetValue(type, out string mappedType) ? mappedType : null;
    }

    static int MapPriority(string priority)
    {
        if (string.IsNullOrWhiteSpace(priority))
            return 4; // Default to 'Low' priority (4)

        // Extract priority text
        string[] parts = priority.Split('-');
        if (parts.Length > 1)
        {
            string priorityText = parts[1].Trim();

            var priorityMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "Low", 4 },
                { "Medium", 3 },
                { "High", 2 },
                { "Critical", 2 }
            };

            return priorityMap.TryGetValue(priorityText, out int mappedValue) ? mappedValue : 4;
        }

        return 4;
    }

    static string MapState(string state)
    {
        return state switch
        {
            "started" => "Active",
            "unstarted" => "New",
            "unscheduled" => "New",
            "delivered" => "Resolved",
            "accepted" => "Closed",
            _ => "New",
        };
    }

    #endregion

    #region Error Log

    private static void LogError(string message)
    {
        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

        DirectoryInfo? directory = new DirectoryInfo(baseDirectory);
        while (directory != null && directory.Name != "AzureEPIC")
        {
            directory = directory.Parent;
        }

        string projectRoot = directory?.FullName ?? baseDirectory;
        string logDirectory = Path.Combine(projectRoot, "Log");

        string logFileName = "Log.txt";
        string logFilePath = Path.Combine(logDirectory, logFileName);

        string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

        try
        {
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            using (StreamWriter writer = new StreamWriter(logFilePath, true))
            {
                writer.WriteLine(logEntry);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAILED] Failed to write error log: {ex.Message}");
        }
    }

    #endregion
}

class WorkItem
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Labels { get; set; }
    public string Type { get; set; }
    public string Description { get; set; }
    public double? Estimate { get; set; }
    public string Priority { get; set; }
    public string CurrentState { get; set; }
    public string Comment { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? Deadline { get; set; }
    public string? OwnedBy1 { get; set; }
    public string? OwnedBy2 { get; set; }
    public List<string> Comments { get; set; } = new List<string>();
}