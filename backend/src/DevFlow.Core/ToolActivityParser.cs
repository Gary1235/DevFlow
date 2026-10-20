using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFlow.Core;

// Read protocol metadata only. Never forward model reasoning, command arguments or tool output.
public sealed class ToolActivityParser(Action<string> report)
{
    private readonly StringBuilder line = new();
    private bool oversized;
    public void Append(string chunk)
    {
        foreach (var character in chunk)
        {
            if (character == '\n') { if (!oversized) Parse(line.ToString()); line.Clear(); oversized = false; }
            else if (!oversized)
            {
                if (line.Length >= 65536) { line.Clear(); oversized = true; }
                else line.Append(character);
            }
        }
    }
    public void Finish() { if (!oversized && line.Length > 0) Parse(line.ToString()); line.Clear(); oversized = false; }
    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private void Parse(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            string? summary = null; bool done = false;
            if (Text(root, "method") is "item/started" or "item/completed" && root.TryGetProperty("params", out var p) && p.TryGetProperty("item", out var native))
            {
                summary = Text(native, "type") switch { "commandExecution" => "執行命令", "fileChange" => "修改檔案", "agentMessage" => "整理回覆", _ => null };
                done = Text(root, "method") == "item/completed";
            }
            else if (Text(root, "method") == "session/update" && root.TryGetProperty("params", out var a) && a.TryGetProperty("update", out var update))
            {
                summary = Text(update, "kind") switch { "execute" => "執行命令", "read" => "讀取專案檔案", "edit" => "修改檔案", "search" => "搜尋專案內容", _ => null };
                done = Text(update, "status") == "completed";
            }
            else if (Text(root, "event") == "step_update" && root.TryGetProperty("step_update", out var step))
            {
                if (Text(step, "step_type") == "tool") summary = Tool(Text(step, "tool_name"));
                else if (Text(step, "step_type") == "agent_response") summary = "整理回覆";
                done = Text(step, "state") == "DONE";
            }
            else if (Text(root, "type") is "item.started" or "item.updated" or "item.completed" && root.TryGetProperty("item", out var item))
            {
                summary = Text(item, "type") switch
                {
                    "command_execution" => IsTest(Text(item, "command")) ? "執行測試" : "執行命令",
                    "file_change" => "修改檔案",
                    "mcp_tool_call" => "呼叫外部工具",
                    "web_search" => "查詢網路資料",
                    "agent_message" => "整理回覆",
                    "plan_update" => "更新工作計畫",
                    _ => null
                };
                done = Text(root, "type") == "item.completed";
            }
            if (summary is not null) report((done ? "已完成：" : "正在") + summary);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or RegexMatchTimeoutException) { }
    }
    public static string? Tool(string? name) => name switch
    {
        "view_file" or "view_file_outline" or "read_file" => "讀取專案檔案",
        "grep_search" or "find_by_name" or "search" => "搜尋專案內容",
        "list_dir" => "查看專案結構",
        "run_command" => "執行命令",
        "write_to_file" or "replace_file_content" or "multi_replace_file_content" or "notebook_edit" or "sed_file" => "修改檔案",
        "read_url_content" or "search_web" => "查詢網路資料",
        _ => null
    };
    private static bool IsTest(string? command) => command is not null && Regex.IsMatch(command, @"^\s*(?:dotnet\s+test|npm\s+(?:run\s+)?test|npx\s+vitest|pytest)(?:\s|$)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(20));
}
