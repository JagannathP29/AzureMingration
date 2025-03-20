using CsvHelper;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Net.Http.Headers;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using AzureEPIC;

class Program
{
    static async Task Main()
    {
        var config = LoadConfiguration();
        string organization = config["AzureDevOps:Organization"];
        string project = config["AzureDevOps:Project"];
        string pat = config["AzureDevOps:PersonalAccessToken"];
        string csvFilePath = config["FilePaths:CsvFilePath"];
        string attachmentPath = config["AttachmentSettings:AttachmentPath"];

        var epics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Store Epic ID mapping
        var features = new List<WorkItem>(); //(Creates as UserStory of Epic(Feature) - Parent)
        var bugs = new List<WorkItem>();
        var chores = new List<WorkItem>();
        var releases = new List<WorkItem>();
        string? choreFeatureId = null;

        using (var reader = new StreamReader(csvFilePath))
        using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
        {
            var records = new List<WorkItem>();

            csv.Read();
            csv.ReadHeader();
            var headerRow = csv.HeaderRecord;

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

                    Comments = new List<string>()
                };

                foreach (var column in headerRow)
                {
                    if (column.StartsWith("Comment"))
                    {
                        var commentValue = csv.GetField(column);
                        if (!string.IsNullOrEmpty(commentValue))
                        {
                            workItem.Comments.Add(commentValue);
                        }
                    }
                }

                records.Add(workItem);
            }

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json-patch+json"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));
                
                List<string> azureIds = await GetAzurePTStoryValues(client, organization, project);

                // Step 1: Create Epics(Actually Feature in Azure DevOps)
                records = records.OrderBy(f => f.CreatedAt).ToList();
                foreach (var record in records)
                {
                    if (record.Type.Equals("Epic", StringComparison.OrdinalIgnoreCase))
                    {
                        string normalizedTitle = Normalize(record.Title);

                        if (azureIds.Contains(record.Id))
                        {
                            Console.WriteLine($"[INFO] ID {record.Id} already exists in Azure DevOps as a PT Story. Skipping creation.");
                        }
                        else
                        {
                            string epicId = await CreateWorkItem(client, organization, project, "Feature", record.Id, record.Title, record.Description, record.Priority, record.CurrentState, record.Comments, record.AcceptedAt, record.Deadline, record.CreatedAt, record.Estimate);
                            if (!string.IsNullOrEmpty(epicId))
                            {
                                epics[normalizedTitle] = epicId;

                                Console.WriteLine($"✅ Work Item '{record.Title}' created.");

                                //Attach Files from"
                                await ProcessAttachments(client, organization, project, epicId, record.Id, attachmentPath);
                            }
                        }
                    }
                    else if (record.Type.Equals("Feature", StringComparison.OrdinalIgnoreCase) || record.Type.Equals("Bug", StringComparison.OrdinalIgnoreCase))
                    {
                        features.Add(record);
                    }
                    else if (record.Type.Equals("Release", StringComparison.OrdinalIgnoreCase))
                    {
                        releases.Add(record);
                    }
                    else if (record.Type.Equals("Chore", StringComparison.OrdinalIgnoreCase))
                    {
                        chores.Add(record);
                    }
                }

                // Step 2: Create Features(User Story) and link them to Epics(Features)
                features = features.OrderBy(f => f.CreatedAt).ToList();
                foreach (var feature in features)
                {
                    string featureLabel = Normalize(feature.Labels);

                    Console.WriteLine($"Checking feature '{feature.Title}' with label '{featureLabel}'...");

                    if (!string.IsNullOrEmpty(featureLabel))
                    {
                        if (epics.TryGetValue(featureLabel, out string epicId))
                        {
                            Console.WriteLine($"✅ Matched Epic(Feature) for '{feature.Title}': EpicID(FeatureID) = {epicId}");

                            if (azureIds.Contains(feature.Id))
                            {
                                Console.WriteLine($"[INFO] ID {feature.Id} already exists in Azure DevOps as a PT Story. Skipping creation.");
                            }
                            else
                            {
                                string featureId = await CreateWorkItem(client, organization, project, "User Story", feature.Id, feature.Title, feature.Description, feature.Priority, feature.CurrentState, feature.Comments, feature.AcceptedAt, feature.Deadline, feature.CreatedAt, feature.Estimate, epicId);
                                if (!string.IsNullOrEmpty(feature.Id))
                                {
                                    Console.WriteLine($"✅ Work Item '{feature.Title}' created.");
                                    await ProcessAttachments(client, organization, project, featureId, feature.Id, attachmentPath);
                                }
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"❌ [WARNING] No matching Epic found for feature '{feature.Title}' (Label: '{featureLabel}').");
                    }
                }

                // Step 3: Create Releases (User Story)
                releases = releases.OrderBy(f => f.CreatedAt).ToList();
                foreach (var release in releases)
                {
                    string releaseLabel = Normalize(release.Labels);

                    if (!string.IsNullOrEmpty(releaseLabel))
                    {
                        if (epics.TryGetValue(releaseLabel, out string epicId))
                        {
                            Console.WriteLine($"✅ Matched Epic(Feature) for '{release.Title}': EpicID(FeatureID) = {epicId}");

                            if (azureIds.Contains(release.Id))
                            {
                                Console.WriteLine($"[INFO] ID {release.Id} already exists in Azure DevOps as a PT Story. Skipping creation.");
                            }
                            else
                            {
                                Console.WriteLine($"Checking feature '{release.Title}' with label '{releaseLabel}'...");

                                string releaseId = await CreateWorkItem(client, organization, project, "Release", release.Id, release.Title, release.Description, release.Priority, release.CurrentState, release.Comments, release.AcceptedAt, release.Deadline, release.CreatedAt, release.Estimate, epicId);
                                if (!string.IsNullOrEmpty(releaseId))
                                {
                                    Console.WriteLine($"✅ Bug(User Story) '{release.Title}' created.");
                                    await ProcessAttachments(client, organization, project, releaseId, release.Id, attachmentPath);
                                }
                            }
                        }
                    }
                }

                // Step 4: Create Chores (User Story) under a Single feature(Titled as "Parent Chore Feature") and linked all chore to that feature - "Parent Chore Feature"
                if (chores.Any())
                {
                    string parentChoreTitle = "Parent Chore Feature";

                    if (!await ParentChoreFeaturexists(client, organization, project, parentChoreTitle))
                    {
                        choreFeatureId = await CreateWorkItem(client, organization, project, "Feature", "1", parentChoreTitle, "This is a parent feature for all Chores.");
                        Console.WriteLine($"✅ Created Parent Chore Feature with ID: {choreFeatureId}");
                    }
                    else
                    {
                        Console.WriteLine($"[INFO] Parent Chore Feature '{parentChoreTitle}' already exists.");
                        choreFeatureId = await GetWorkItemId(client, organization, project, parentChoreTitle);
                    }
                }

                chores = chores.OrderBy(f => f.CreatedAt).ToList();
                foreach (var chore in chores)
                {
                    if (choreFeatureId != null)
                    {
                        if (azureIds.Contains(chore.Id))
                        {
                            Console.WriteLine($"[INFO] ID {chore.Id} already exists in Azure DevOps as a PT Story. Skipping creation.");
                        }
                        else
                        {
                            string choreId = await CreateWorkItem(client, organization, project, "User Story", chore.Id, chore.Title, chore.Description, chore.Priority, chore.CurrentState, chore.Comments, chore.AcceptedAt, chore.Deadline, chore.CreatedAt, chore.Estimate, choreFeatureId);
                            if (!string.IsNullOrEmpty(choreId))
                            {
                                Console.WriteLine($"✅ Chore (User Story) '{chore.Title}' created under Parent Chore Feature.");
                                await ProcessAttachments(client, organization, project, choreId, chore.Id, attachmentPath);
                            }
                        }
                    }
                }
            }
        }
    }


    #region Create Workitems

    static async Task<string> CreateWorkItem(HttpClient client, string organization, string project, string type, string? id, string title, string description, string priority = null, string currentState = null, List<string> comments = null, DateTime? acceptedAt = null, DateTime? deadline = null, DateTime? createdAt = null, double? estimate = null, string parentId = null)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/${type}?api-version=7.1";

        var workItem = new List<object>
        {
            new { op = "add", path = "/fields/System.Id", value = id },
            new { op = "add", path = "/fields/System.Title", value = title },
            new { op = "add", path = "/fields/Custom.PTStory", value = id }
        };

        if (!string.IsNullOrEmpty(description))
        {
            string htmlDescription = $"<pre><code>{System.Net.WebUtility.HtmlEncode(description)}</code></pre>";

            workItem.Add(new
            {
                op = "add",
                path = "/fields/System.Description",
                value = htmlDescription
            });
        }

        if (!string.IsNullOrEmpty(priority))
        {
            int mappedPriority = MapPriority(priority);
            workItem.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Common.Priority", value = mappedPriority });
        }

        if (!string.IsNullOrEmpty(parentId))
        {
            workItem.Add(new
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

        if (estimate.HasValue)
        {
            workItem.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.Effort", value = estimate.Value });
        }

        if (acceptedAt.HasValue)
        {
            workItem.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.StartDate", value = acceptedAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") });
        }

        if (deadline.HasValue)
        {
            workItem.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.TargetDate", value = deadline.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") });
        }

        var content = new StringContent(JsonSerializer.Serialize(workItem), Encoding.UTF8, "application/json-patch+json");
        HttpResponseMessage response = await client.PostAsync(url, content);

        if (response.IsSuccessStatusCode)
        {
            string responseBody = await response.Content.ReadAsStringAsync();
            JsonDocument json = JsonDocument.Parse(responseBody);
            int responseId = json.RootElement.GetProperty("id").GetInt32();
            Console.WriteLine($"✅ {type} '{title}' created with ID: {responseId}");

            await ProcessCommemnts(client, organization, project, responseId.ToString(), comments);

            // **Step 1: Update Work Item State**
            if (!string.IsNullOrEmpty(currentState))
            {
                await UpdateWorkItemState(client, organization, project, responseId.ToString(), MapState(currentState));
            }

            // **Step 2: Check Parent State After Updating Child**
            if (!string.IsNullOrEmpty(parentId))
            {
                await UpdateParentState(client, organization, project, parentId);
            }

            return responseId.ToString();
        }
        else
        {
            Console.WriteLine($"❌ [ERROR] Failed to create {type} '{title}'. Status: {response.StatusCode}");
            return null;
        }
    }

    #endregion


    #region GET Workitems and CheckExists

    static async Task<bool> ParentChoreFeaturexists(HttpClient client, string organization, string project, string title)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/wiql?api-version=7.1";

        var query = new
        {
            query = $@"SELECT [System.Id] FROM WorkItems WHERE [System.Title] COLLATE Latin1_General_CI_AS = '{title.Replace("'", "''")}' AND [System.WorkItemType] = 'Epic'"
        };

        var content = new StringContent(JsonSerializer.Serialize(query), Encoding.UTF8, "application/json");
        HttpResponseMessage response = await client.PostAsync(url, content);

        if (response.IsSuccessStatusCode)
        {
            string responseBody = await response.Content.ReadAsStringAsync();
            JsonDocument json = JsonDocument.Parse(responseBody);
            return json.RootElement.GetProperty("workItems").GetArrayLength() > 0;
        }
        return false;
    }

    static async Task<string> GetWorkItemId(HttpClient client, string organization, string project, string title)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/wiql?api-version=7.1";
        var query = new
        {
            query = $"SELECT [System.Id] FROM WorkItems WHERE [System.Title] = '{title}'"
        };

        var content = new StringContent(JsonSerializer.Serialize(query), Encoding.UTF8, "application/json");
        HttpResponseMessage response = await client.PostAsync(url, content);

        if (response.IsSuccessStatusCode)
        {
            string responseBody = await response.Content.ReadAsStringAsync();
            JsonDocument json = JsonDocument.Parse(responseBody);

            var workItems = json.RootElement.GetProperty("workItems");
            if (workItems.GetArrayLength() > 0)
            {
                return workItems[0].GetProperty("id").ToString();
            }
        }

        Console.WriteLine($"[INFO] Work item '{title}' not found.");
        return null;
    }

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


    #region Attach Comments & Attachments

    static async Task ProcessCommemnts(HttpClient client, string organization, string project, string workItemId, List<string> comments)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{workItemId}?api-version=7.1";

        if (comments == null || comments.Count == 0)
        {
            Console.WriteLine("⚠️ No comments provided.");
            return;
        }

        foreach (var comment in comments)
        {
            var patchData = new List<object>
            {
                new { op = "add", path = "/fields/System.History", value = comment }
            };

            var json = JsonSerializer.Serialize(patchData);
            var content = new StringContent(json, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json-patch+json");

            HttpResponseMessage response = await client.PatchAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"✅ Comment added to Work Item {workItemId}: {comment}");
            }
            else
            {
                string errorResponse = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ Failed to add comment: {errorResponse}");
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
                Console.WriteLine($"📂 Uploading: {file}");
                string attachmentUrl = await UploadAttachment(client, organization, project, file);

                if (!string.IsNullOrEmpty(attachmentUrl))
                {
                    bool success = await AttachFileToWorkItem(client, organization, project, pkId, attachmentUrl);
                    if (success)
                        Console.WriteLine($"✅ Attached {file} to Work Item {pkId}");
                    else
                        Console.WriteLine($"❌ Failed to attach {file} to Work Item {pkId}");
                }
            }
        }
        else
        {
            Console.WriteLine($"[INFO] No attachments found for Work Item {pkId}");
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
                return json.RootElement.GetProperty("url").GetString();  // Returns attachment URL
            }
            else
            {
                Console.WriteLine($"❌ Failed to upload file {filePath}: {response.ReasonPhrase}");
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


    #region Update State

    static async Task UpdateParentState(HttpClient client, string organization, string project, string parentId)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{parentId}?$expand=relations&api-version=7.1";
        HttpResponseMessage response = await client.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"❌ [ERROR] Failed to fetch parent work item {parentId}. Status: {response.StatusCode}");
            return;
        }

        string responseBody = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(responseBody);

        if (!json.RootElement.TryGetProperty("relations", out JsonElement relations))
        {
            Console.WriteLine($"[INFO] Work item {parentId} has no child items.");
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

        if (response.IsSuccessStatusCode)
        {
            Console.WriteLine($"✅ Work item {workItemId} updated to state '{newState}'.");
        }
        else
        {
            Console.WriteLine($"❌ [ERROR] Failed to update work item {workItemId} to state '{newState}'. Status: {response.StatusCode}");
        }
    }

    static async Task<string> GetWorkItemState(HttpClient client, string organization, string project, string workItemId)
    {
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{workItemId}?fields=System.State&api-version=7.1";
        HttpResponseMessage response = await client.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"❌ [ERROR] Failed to fetch work item {workItemId}. Status: {response.StatusCode}");
            return null;
        }

        string responseBody = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(responseBody);

        return json.RootElement.GetProperty("fields").GetProperty("System.State").GetString();
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
            { "Bug", "User Story" },
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
    public List<string> Comments { get; set; } = new List<string>();
}