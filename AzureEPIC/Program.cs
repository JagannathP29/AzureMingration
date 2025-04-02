using CsvHelper;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Net.Http.Headers;
using CsvHelper.Configuration;
using Microsoft.Extensions.Configuration;
using System.Reflection.Emit;

class Program
{
    private static int processedCount = 0;
    private static int totalWorkItemCount = 0;
    private static HashSet<string> processedIds = new HashSet<string>();
    private static Dictionary<string, string> epicDict = new();
    private static List<FailedWorkItem> failedWorkItems = new List<FailedWorkItem>();

    static async Task Main(string[] args)
    {
        if (args.Length < 1)
        {
            return;
        }

        // string param = args[0].ToLower();
        string param = "updateazure";
        var config = LoadConfiguration();
        string organization = config["AzureDevOps:Organization"];
        string project = config["AzureDevOps:Project"];
        string csvFilePath = config["FilePaths:CsvFilePath"];
        string attachmentPath = config["AttachmentSettings:AttachmentPath"];

        var records = ReadCsvFile(csvFilePath);
        records = records.OrderBy(f => f.CreatedAt).ToList();
        using var client = CreateHttpClient(pat);

        // List<(string Id, string PTStory)> azurePTData = await GetAzurePTStoryValues(client, organization, project);
        //processedIds = azurePTData.Select(x => x.PTStory).ToHashSet();
        //await RetryFailedWorkItems(client, organization, project, attachmentPath);

        switch (param)
        {
            case "epic":
                await ProcessEpics(client, organization, project, records, attachmentPath);
                break;
            case "release":
                await ProcessReleases(client, organization, project, records, attachmentPath);
                break;
            case "updateazure":
                await UpdateAzureTickets(client, organization, project, records, attachmentPath);
                break;
            case "retrycomment":
                await UpdateComments(client, organization, project, records, attachmentPath);
                break;
            default:
                Console.WriteLine("Invalid type specified. Use 'epic', 'feature', 'bug', 'release' or 'chore'.");
                break;
        }
    }

    #region Create Workitems

    private static async Task ProcessEpics(HttpClient client, string organization, string project, List<WorkItem> records, string attachmentPath)
    {
        Console.WriteLine($"Do you want to sync board: {project}? Press enter to continue.");
        string input = Console.ReadLine();

        if (!string.IsNullOrEmpty(input))
        {
            Console.WriteLine("Aborting process.");
            return;
        }

        List<WorkItem> allItems = records
                                 .Where(r => (r.Type.Equals("epic", StringComparison.OrdinalIgnoreCase) ||
                                             r.Type.Equals("bug", StringComparison.OrdinalIgnoreCase) ||
                                             r.Type.Equals("feature", StringComparison.OrdinalIgnoreCase) ||
                                             r.Type.Equals("chore", StringComparison.OrdinalIgnoreCase)))
                                 .OrderBy(r => r.Type.Equals("epic", StringComparison.OrdinalIgnoreCase) ? 0 : 1) // Epics first
                                 .ThenBy(f => f.CreatedAt)
                                 .ToList();
        totalWorkItemCount = allItems.Count;
        //Create Parent Chore Featurw -> Use that feature for linking all chore items.
        string choreFeatureTitle = "Chore Parent Feature";
        WorkItem choreFeature = new WorkItem
        {
            Title = choreFeatureTitle,
            Type = "feature"
        };

        string choreFeatureId = await CreateWorkItem(client, organization, project, "Feature", choreFeature, null, attachmentPath);
        if (!string.IsNullOrEmpty(choreFeatureId))
            Console.WriteLine($"Chore Parent Feature created with ID: {choreFeatureId}");

        foreach (var item in allItems)
        {
            if (processedIds.Contains(item.Id))
                continue;
            if (item.Type.Equals("epic"))
            {
                string epicId = await CreateWorkItem(client, organization, project, MapWorkItemType(item.Type), item, null, attachmentPath);
                if (!string.IsNullOrEmpty(epicId))
                {
                    processedIds.Add(item.Id);
                    processedCount++;
                    string message = $"{MapWorkItemType(item.Type)} - {item.Id} - created with ID :{epicId} | [{processedCount}/{allItems.Count}]";
                    Console.WriteLine(message);
                    LogError(message);
                    epicDict.Add(item.Labels.Trim().ToLower(), epicId);
                    continue;
                }
            }

            // First creating all the epics as "Feature" sort by CreatedAt ASC then attach the "Feature" while creating "User Stories" and "Bugs" based on CreatedAt ASC.
            // Find the epic id based on first lable if it has comma and set epicWorkId
            // Add label as tags in userstory or bugs
            // For bugs there is not description, it's system info , so we need to update description in system info

            string epicWorkId = null;

            //if item.type = "chore" -> epicWorkId = chore's parent id
            if (item.Type.Equals("chore"))
            {
                epicWorkId = choreFeatureId;
            }

            if (!string.IsNullOrEmpty(item.Labels) && item.Type != "chore")
            {
                var firstLabel = item.Labels.Split(',').FirstOrDefault()?.Trim().ToLower();
                if (!string.IsNullOrEmpty(firstLabel) && epicDict.TryGetValue(firstLabel, out var foundEpicId))
                {
                    epicWorkId = foundEpicId;
                }
            }

            string workItemId = await CreateWorkItem(client, organization, project, MapWorkItemType(item.Type), item, epicWorkId, attachmentPath);
            if (!string.IsNullOrEmpty(workItemId))
            {
                processedIds.Add(item.Id);
                processedCount++;
                if (!string.IsNullOrEmpty(epicWorkId))
                {
                    string message = $"{MapWorkItemType(item.Type)} - {item.Id} - created under EpicID - {epicWorkId} | [{processedCount}/{allItems.Count}]";
                    Console.WriteLine(message);
                    LogError(message);
                }
                else
                {
                    string message = $"{MapWorkItemType(item.Type)} - {item.Id} - created under No Epic | [{processedCount}/{allItems.Count}]";
                    Console.WriteLine(message);
                    LogError(message);
                }
            }
            else
            {
                var failedItem = new FailedWorkItem
                {
                    Id = item.Id,
                    ParentId = epicWorkId,
                    Type = item.Type,
                    Title = item.Title,
                    Labels = item.Labels,
                    Description = item.Description,
                    Estimate = item.Estimate,
                    Priority = item.Priority,
                    CurrentState = item.CurrentState,
                    Comment = item.Comment,
                    CreatedAt = item.CreatedAt,
                    AcceptedAt = item.AcceptedAt,
                    Deadline = item.Deadline,
                    OwnedBy1 = item.OwnedBy1,
                    OwnedBy2 = item.OwnedBy2,
                    Comments = item.Comments ?? new List<string>(),
                    Reason = "Work item creation failed."
                };
                failedWorkItems.Add(failedItem);
                LogError($"[FAILED] Failed to create {item.Type}- {item.Id}: {item.Title}");
            }
        }

        #region Comment
        //var unprocessedRecords = records
        //      .Where(r => !processedIds.Contains(r.Id) && (r.Type.Equals("bug", StringComparison.OrdinalIgnoreCase) || r.Type.Equals("feature", StringComparison.OrdinalIgnoreCase)))
        //      .OrderBy(r => r.CreatedAt)
        //      .ToList();

        //foreach (var ptItem in unprocessedRecords)
        //{
        //    string workItemId = await CreateWorkItem(client, organization, project, MapWorkItemType(ptItem.Type), ptItem, null, attachmentPath);

        //    if (!string.IsNullOrEmpty(workItemId))
        //    {
        //        processedIds.Add(ptItem.Id);
        //        processedCount++;
        //        Console.WriteLine($"{MapWorkItemType(ptItem.Type)} - created under No Epic | [{processedCount}/{records.Count}]");
        //    }
        //    else
        //    {
        //        LogError($"[FAILED] Failed to create {ptItem.Type}: {ptItem.Title}");
        //    }
        //}
        #endregion

        Console.WriteLine($"{processedCount} work items processed successfully of {allItems.Count} records.");

        SaveFailedItemsToJson(failedWorkItems);
        await RetryFailedWorkItems(client, organization, project, attachmentPath);
    }

    private static async Task ProcessReleases(HttpClient client, string organization, string project, List<WorkItem> records, string attachmentPath)
    {
        Console.WriteLine($"Do you want to sync board: {project}? Press enter to continue.");
        string input = Console.ReadLine();

        if (!string.IsNullOrEmpty(input))
        {
            Console.WriteLine("Aborting process.");
            return;
        }

        var releaseItems = records.Where(r => r.Type.Equals("release", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var record in releaseItems)
        {
            if (processedIds.Contains(record.Id))
                continue;

            string releaseId = await CreateWorkItem(client, organization, project, "Release", record, null, attachmentPath);

            if (!string.IsNullOrEmpty(releaseId))
            {
                processedCount++;
                string message = $"{record.Id} - Created Release with AzureID : {releaseId} | [{processedCount}/{releaseItems.Count}]";
                Console.WriteLine(message);
                LogError(message);
                processedIds.Add(record.Id);
            }
            else
            {
                var failedItem = new FailedWorkItem
                {
                    Id = record.Id,
                    Type = record.Type,
                    Title = record.Title,
                    Labels = record.Labels,
                    Description = record.Description,
                    Estimate = record.Estimate,
                    Priority = record.Priority,
                    CurrentState = record.CurrentState,
                    Comment = record.Comment,
                    CreatedAt = record.CreatedAt,
                    AcceptedAt = record.AcceptedAt,
                    Deadline = record.Deadline,
                    OwnedBy1 = record.OwnedBy1,
                    OwnedBy2 = record.OwnedBy2,
                    Comments = record.Comments ?? new List<string>(),
                    Reason = "Work item creation failed."
                };
                failedWorkItems.Add(failedItem);
                LogError($"Failed to create Release {record.Id}: {record.Title}");
            }
        }

        SaveFailedItemsToJson(failedWorkItems);
        Console.WriteLine($"{processedCount} Release work items created of {releaseItems.Count}");
    }

    public static List<string> azureUsers = new List<string>();
    private static async Task<string> CreateWorkItem(HttpClient client, string organization, string project, string type, WorkItem workItem, string pkParentId = null, string attachmentPath = null)
    {
        string url = string.Empty;
        var responseBody = string.Empty;
        try
        {
            if (azureUsers.Count == 0)
            {
                azureUsers = await GetAzureDevOpsUsers(client, organization, project);
            }
            url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/${type}?api-version=7.1";
            string truncatedTitle = string.IsNullOrWhiteSpace(workItem.Title) ? "Untitled Work Item" :
                        workItem.Title.Length > 255 ? workItem.Title.Substring(0, 255) : workItem.Title;

            var requestBody = new List<object>
            {
                new { op = "add", path = "/fields/System.Title", value = truncatedTitle }
            };

            if (!string.IsNullOrEmpty(workItem.Id))
            {
                requestBody.Add(new { op = "add", path = "/fields/Custom.PTStory", value = workItem.Id });
            }

            if (workItem.Type.Equals("chore", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(workItem.Labels))
                {
                    workItem.Labels = "chore";
                }
                else
                {
                    var labelList = string.Join(";", workItem.Labels.Split(',').Select(tag => tag.Trim()));
                    workItem.Labels = labelList;
                }

                requestBody.Add(new { op = "add", path = "/fields/System.Tags", value = workItem.Labels });
            }
            else if (!string.IsNullOrEmpty(workItem.Labels) && (type.Equals("user story", StringComparison.OrdinalIgnoreCase) || type.Equals("bug", StringComparison.OrdinalIgnoreCase)))
            {
                string tags = string.Join(";", workItem.Labels.Split(',').Select(tag => tag.Trim()));
                requestBody.Add(new { op = "add", path = "/fields/System.Tags", value = tags });
            }

            if (workItem.Type.Equals("bug", StringComparison.OrdinalIgnoreCase))
            {
                string htmlDescription = $"<pre><code>{System.Net.WebUtility.HtmlEncode(workItem.Description)}</code></pre>";
                requestBody.Add(new
                {
                    op = "add",
                    path = "/fields/Microsoft.VSTS.TCM.SystemInfo",
                    value = htmlDescription
                });
            }
            else if (!string.IsNullOrEmpty(workItem.Description))
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

            if (!string.IsNullOrEmpty(pkParentId))
            {
                requestBody.Add(new
                {
                    op = "add",
                    path = "/relations/-",
                    value = new
                    {
                        rel = "System.LinkTypes.Hierarchy-Reverse",
                        url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{pkParentId}"
                    }
                });
            }

            if (workItem.Estimate.HasValue)
            {
                requestBody.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.StoryPoints", value = workItem.Estimate.Value });
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

            if (!string.IsNullOrEmpty(workItem.RequestedBy))
            {
                requestBody.Add(new { op = "add", path = "/fields/Custom.RequestedBy", value = workItem.RequestedBy });
            }

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json-patch+json");
            var response = await client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                string errorMessage = $"[FAILED] Failed to create {type} '{workItem.Id}'. Status: {response.StatusCode}";
                LogError(errorMessage);
                return null;
            }

            responseBody = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(responseBody);
            int responseId = json.RootElement.GetProperty("id").GetInt32();

            // **Step 1: Update Work Item State**
            if (!string.IsNullOrEmpty(workItem.CurrentState))
            {
                await UpdateWorkItemState(client, organization, project, responseId.ToString(), MapState(workItem.CurrentState));
            }

            // **Step 2: Check Parent State After Updating Child**
            if (!string.IsNullOrEmpty(pkParentId))
            {
                await UpdateParentState(client, organization, project, pkParentId);
            }

            await ProcessComments(client, organization, project, responseId.ToString(), workItem.Comments);
            await ProcessAttachments(client, organization, project, responseId.ToString(), attachmentPath, workItem.Id);

            return responseId.ToString();
        }
        catch (HttpRequestException httpEx)
        {
            LogError($"[HTTP ERROR] Failed to communicate with Azure DevOps API - {httpEx.Message}. " +
                     $"WorkItem ID: {workItem.Id}, Type: {type}");
        }
        catch (JsonException jsonEx)
        {
            LogError($"[JSON ERROR] Failed to parse response for WorkItem ID: {workItem.Id}, Type: {type}. " +
                     $"Error: {jsonEx.Message}, Response Content: {responseBody}");
        }
        catch (Exception ex)
        {
            LogError($"[ERROR] Exception - {ex.Message}, StackTrace: {ex.StackTrace}, " +
                     $"Type: {type}, WorkItem ID: {workItem.Id}");
        }
        return null;
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
            return;

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
            LogError($"[FAILED] Failed to update work item {workItemId} to state '{newState}'. Status: {response.StatusCode}");
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

    static async Task ProcessComments(HttpClient client, string organization, string project, string workItemId, List<string> comments)
    {
        List<FailedWorkItem> failedItemsToAddComment = new List<FailedWorkItem>();
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{workItemId}?api-version=7.1";

        if (comments == null || comments.Count == 0)
            return;

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
                LogError($"[FAILED] while adding comment - {formattedComment} of WorkItemID - {workItemId}");

                failedItemsToAddComment.Add(new FailedWorkItem
                {
                    Id = workItemId,
                    FailedType = "Comment",
                    Comment = formattedComment,
                    Reason = response.ReasonPhrase
                });
                return;
            }
        }
        if (failedItemsToAddComment.Count > 0)
        {
            SaveFailedItemsToJson(failedItemsToAddComment);
        }
    }

    static async Task ProcessAttachments(HttpClient client, string organization, string project, string pkId, string baseFolderPath, string workItemId = null)
    {
        List<FailedWorkItem> failedAttachItems = new List<FailedWorkItem>();
        if (string.IsNullOrEmpty(workItemId))
            return;

        string workItemFolder = Path.Combine(baseFolderPath, workItemId);

        if (Directory.Exists(workItemFolder))
        {
            string[] files = Directory.GetFiles(workItemFolder);

            foreach (string file in files)
            {
                string attachmentUrl = await UploadAttachment(client, organization, project, file, pkId);

                if (!string.IsNullOrEmpty(attachmentUrl))
                {
                    bool success = await AttachFileToWorkItem(client, organization, project, pkId, attachmentUrl);
                    if (!success)
                    {
                        string errorMessage = $"Failed to attach {file} to Work Item {pkId}";
                        LogError(errorMessage);
                        failedAttachItems.Add(new FailedWorkItem
                        {
                            Id = pkId,
                            FailedType = "Attachment",
                            Comment = file,
                            Reason = errorMessage
                        });
                        return;
                    }
                }
            }
        }
        if (failedAttachItems.Count > 0)
        {
            SaveFailedItemsToJson(failedAttachItems);
        }
    }

    static async Task<string> UploadAttachment(HttpClient client, string organization, string project, string filePath, string pkId = null)
    {
        List<FailedWorkItem> failedUploadItems = new List<FailedWorkItem>();
        string encodedFileName = Uri.EscapeDataString(Path.GetFileName(filePath));
        string url = $"https://dev.azure.com/{organization}/{project}/_apis/wit/attachments?fileName={encodedFileName}&api-version=7.1";

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
                string errorMessage = $"Failed to upload file {filePath}: {response.ReasonPhrase}";
                LogError(errorMessage);

                failedUploadItems.Add(new FailedWorkItem
                {
                    Id = pkId ?? "",
                    FailedType = "Attachment",
                    AttachmentPath = filePath,
                    Reason = errorMessage
                });

                SaveFailedItemsToJson(failedUploadItems);
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
            Console.WriteLine($"[ERROR] Failed to fetch users. Status: {response.StatusCode}");
            return new List<string>();
        }
    }

    #endregion

    #region Update Azure Tickets
    private static async Task UpdateAzureTickets(HttpClient client, string organization, string project, List<WorkItem> csvRecords, string attachmentPath)
    {
        Console.WriteLine($"Do you want to sync board: {project}? Press enter to continue.");
        string input = Console.ReadLine();
        if (!string.IsNullOrEmpty(input))
        {
            Console.WriteLine("Aborting process.");
            return;
        }

        var config = LoadConfiguration();
        string existingAzureIDPath = config["FilePaths:ExistAzureIDPath"];
        Dictionary<string, string> ptStoryToIdMap = new();
        using (var reader = new StreamReader(existingAzureIDPath))
        using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)))
        {
            var records = csv.GetRecords<dynamic>().ToList();
            foreach (var record in records)
            {
                string id = record.ID;
                string ptStory = record.PTStory;

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(ptStory))
                {
                    ptStoryToIdMap[ptStory] = id;
                }
            }
        }

        var excludedTypes = new[] { "epic", "release" };
        var recordsToProcess = csvRecords.Where(x => !excludedTypes.Contains(x.Type, StringComparer.OrdinalIgnoreCase)).ToList();
        int successCount = 0;

        foreach (var record in recordsToProcess)
        {
            if (record.Estimate == null) continue;

            if (!ptStoryToIdMap.TryGetValue(record.Id, out string azureId))
            {
                Console.WriteLine($"No Azure ID found for PTStory: {record.Id}");
                continue;
            }

            var updatePayload = new List<object>();
            if (!string.IsNullOrEmpty(record.RequestedBy))
            {
                updatePayload.Add(new { op = "add", path = "/fields/Custom.RequestedBy", value = record.RequestedBy });
            }

            if (!string.IsNullOrEmpty(record.Priority))
            {
                updatePayload.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Common.Priority", value = MapPriority(record.Priority) });
            }

            if (record.Estimate.HasValue)
            {
                updatePayload.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.StoryPoints", value = record.Estimate });
            }

            if (record.Type.Equals("chore", StringComparison.OrdinalIgnoreCase))
            {
                string tags;

                if (string.IsNullOrEmpty(record.Labels))
                {
                    tags = "chore";
                }
                else
                {
                    var labelList = string.Join(";", record.Labels.Split(',').Select(tag => tag.Trim()));
                    tags = labelList;
                }

                updatePayload.Add(new { op = "add", path = "/fields/System.Tags", value = tags });
            }

            var content = new StringContent(JsonSerializer.Serialize(updatePayload), Encoding.UTF8, "application/json-patch+json");
            var response = await client.PatchAsync($"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/{azureId}?api-version=7.0", content);

            if (response.IsSuccessStatusCode)
            {
                successCount++;
                Console.WriteLine($"{MapWorkItemType(record.Type)} updated for ID - {record.Id} with Azure ID : {azureId} | [{successCount}/{recordsToProcess.Count}]");
            }
            else
            {
                Console.WriteLine($"Failed to update ID - {record.Id} with Azure ID : {azureId}");
            }
        }

        Console.WriteLine($"{successCount} record's Story Points updated successfully.");
    }

    private static async Task UpdateComments(HttpClient client, string organization, string project, List<WorkItem> csvRecords, string attachmentPath)
    {
        Console.WriteLine($"Do you want to sync board: {project}? Press enter to continue.");
        string input = Console.ReadLine();
        if (!string.IsNullOrEmpty(input))
        {
            Console.WriteLine("Aborting process.");
            return;
        }

        var config = LoadConfiguration();
        string existingAzureIDPath = config["FilePaths:ExistAzureIDPath"];

        Dictionary<string, string> ptStoryToIdMap = new();
        using (var reader = new StreamReader(existingAzureIDPath))
        using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)))
        {
            var records = csv.GetRecords<dynamic>().ToList();
            foreach (var record in records)
            {
                string id = record.ID;
                string ptStory = record.PTStory;

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(ptStory))
                {
                    ptStoryToIdMap[ptStory] = id;
                }
            }
        }

        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        DirectoryInfo? directory = new DirectoryInfo(baseDirectory);
        while (directory != null && directory.Name != "AzureEPIC")
        {
            directory = directory.Parent;
        }

        string projectRoot = directory?.FullName ?? baseDirectory;
        string logDirectory = Path.Combine(projectRoot, "Log");
        string jsonFilePath = Path.Combine(logDirectory, "FailedWorkItems.json");
        if (!File.Exists(jsonFilePath))
        {
            Console.WriteLine("No failed work items found for retry.");
            return;
        }

        string jsonData = File.ReadAllText(jsonFilePath);
        List<FailedWorkItem> failedItems = JsonSerializer.Deserialize<List<FailedWorkItem>>(jsonData) ?? new List<FailedWorkItem>();
        if (!failedItems.Any())
        {
            Console.WriteLine("No failed work items found for retry.");
            return;
        }

        List<FailedWorkItem> retryFailures = new List<FailedWorkItem>();
        foreach (var item in failedItems)
        {
            if (!ptStoryToIdMap.TryGetValue(item.Id, out string azureId))
            {
                Console.WriteLine($"No Azure ID found for PTStory: {item.Id}");
                continue;
            }

            try
            {
                await ProcessComments(client, organization, project, azureId, item.Comments);
                processedCount++;
                int totalProcessed = totalWorkItemCount + failedItems.Count;
                Console.WriteLine($"Success: ID: {item.Id} - Comments added to Work Item {azureId} | [{processedCount}/{totalProcessed}]");
            }
            catch (Exception ex)
            {
                item.Reason = ex.Message;
                retryFailures.Add(item);
                LogError($"[Error] Failed to update comments for Work Item {azureId}: {ex.Message}");
            }
        }
    }

    private static string FindParentEpicId(WorkItem workItem, List<WorkItem> records, List<(string Id, string PTStory)> azurePTData)
    {
        var parentEpic = records.FirstOrDefault(e =>
            e.Type.Equals("epic", StringComparison.OrdinalIgnoreCase) &&
            Normalize(e.Title) == Normalize(workItem.Labels));

        if (parentEpic != null)
        {
            var matchingAzurePT = azurePTData.FirstOrDefault(e => e.PTStory == parentEpic.Id);
            return matchingAzurePT.Id ?? string.Empty;
        }

        return null;
    }

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
                RequestedBy = csv.GetField("RequestedBy"),
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
        string result = string.IsNullOrWhiteSpace(text) ? null : new string(text.Where(c => !char.IsPunctuation(c)).ToArray()).Trim().ToLowerInvariant();
        return result;
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
            return 4; // Default to 'none' priority (4)

        // Extract priority text
        string[] parts = priority.Split('-');
        if (parts.Length > 1)
        {
            string priorityText = parts[1].Trim();

            var priorityMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "High", 1 },
                { "Medium", 2 },
                { "Low", 3 },
                { "none", 4 }
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

    #region Failed Items

    private static void SaveFailedItemsToJson(List<FailedWorkItem> failedItems)
    {
        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

        DirectoryInfo? directory = new DirectoryInfo(baseDirectory);
        while (directory != null && directory.Name != "AzureEPIC")
        {
            directory = directory.Parent;
        }

        string projectRoot = directory?.FullName ?? baseDirectory;
        string logDirectory = Path.Combine(projectRoot, "Log");

        if (!Directory.Exists(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        string jsonFilePath = Path.Combine(logDirectory, "FailedWorkItems.json");

        try
        {
            List<FailedWorkItem> existingFailedItems = new List<FailedWorkItem>();

            if (File.Exists(jsonFilePath))
            {
                string existingJson = File.ReadAllText(jsonFilePath);
                if (!string.IsNullOrWhiteSpace(existingJson))
                {
                    existingFailedItems = JsonSerializer.Deserialize<List<FailedWorkItem>>(existingJson) ?? new List<FailedWorkItem>();
                }
            }

            existingFailedItems.AddRange(failedItems);

            string json = JsonSerializer.Serialize(existingFailedItems, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonFilePath, json);
        }
        catch (Exception ex)
        {
            LogError($"[ERROR] Error writing failed work items to JSON: {ex.Message}");
        }
    }

    private static async Task RetryFailedWorkItems(HttpClient client, string organization, string project, string attachmentPath)
    {
        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

        DirectoryInfo? directory = new DirectoryInfo(baseDirectory);
        while (directory != null && directory.Name != "AzureEPIC")
        {
            directory = directory.Parent;
        }

        string projectRoot = directory?.FullName ?? baseDirectory;
        string logDirectory = Path.Combine(projectRoot, "Log");
        string jsonFilePath = Path.Combine(logDirectory, "FailedWorkItems.json");

        if (!File.Exists(jsonFilePath))
        {
            Console.WriteLine("No failed work items found for retry.");
            return;
        }

        string jsonData = File.ReadAllText(jsonFilePath);
        List<FailedWorkItem> failedItems = JsonSerializer.Deserialize<List<FailedWorkItem>>(jsonData) ?? new List<FailedWorkItem>();

        if (!failedItems.Any())
        {
            Console.WriteLine("No failed work items found for retry.");
            return;
        }

        List<FailedWorkItem> retryFailures = new List<FailedWorkItem>();

        foreach (var item in failedItems)
        {
            if (item.FailedType == "Attachment")
            {
                string attachmentUrl = await UploadAttachment(client, organization, project, item.AttachmentPath, item.Id);

                if (!string.IsNullOrEmpty(attachmentUrl))
                {
                    Console.WriteLine($"[Retry Success] Attachment uploaded for {item.AttachmentPath}");
                }
                else
                {
                    item.Reason = "Retry failed.";
                    retryFailures.Add(item);
                    LogError($"[Retry Failed] Failed for attachment: {item.AttachmentPath}");
                }
            }
            else if (item.FailedType == "Comment")
            {
                List<string> comments = new List<string> { item.Comment };
                await ProcessComments(client, organization, project, item.Id, comments);
            }
            else
            {
                string workItemId = await CreateWorkItem(client, organization, project, MapWorkItemType(item.Type), new WorkItem
                {
                    Id = item.Id,
                    Title = item.Title,
                    Type = item.Type,
                    Labels = item.Labels,
                    Description = item.Description,
                    Estimate = item.Estimate,
                    Priority = item.Priority,
                    CurrentState = item.CurrentState,
                    Comment = item.Comment,
                    CreatedAt = item.CreatedAt,
                    AcceptedAt = item.AcceptedAt,
                    Deadline = item.Deadline,
                    OwnedBy1 = item.OwnedBy1,
                    OwnedBy2 = item.OwnedBy2,
                    Comments = item.Comments
                }, item.ParentId, attachmentPath);

                if (!string.IsNullOrEmpty(workItemId))
                {
                    processedCount++;
                    int totalProcessed = totalWorkItemCount + failedItems.Count;
                    Console.WriteLine($"Retry Success: {MapWorkItemType(item.Type)} - {item.Id} created with ID {workItemId} | [{processedCount}/{totalProcessed}]");
                }
                else
                {
                    item.Reason = "Retry Failed.";
                    retryFailures.Add(item);
                    LogError($"[Retry Failed] Failed for {item.FailedType}: {item.Id}");
                }
            }
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
    public string? RequestedBy { get; set; }
    public List<string> Comments { get; set; } = new List<string>();
}

class FailedWorkItem
{
    public string? Reason { get; set; }
    public string? AttachmentPath { get; set; }
    public string? FailedType { get; set; }
    public string? Id { get; set; }
    public string? ParentId { get; set; }
    public string? Type { get; set; }
    public string? Title { get; set; }
    public string? Labels { get; set; }
    public string? Description { get; set; }
    public double? Estimate { get; set; }
    public string? Priority { get; set; }
    public string? CurrentState { get; set; }
    public string? Comment { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? Deadline { get; set; }
    public string? OwnedBy1 { get; set; }
    public string? OwnedBy2 { get; set; }
    public string? RequestedBy { get; set; }
    public List<string> Comments { get; set; } = new List<string>();
}
class AzureItem
{
    public string Id { get; set; }
    public string PTId { get; set; }
}