// Scripts/API/LLMInterface.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

[Serializable]
public class Message
{
    public string role;
    public string content;
}

[Serializable]
public class ChatRequest
{
    public string model;
    public Message[] messages;
    public float temperature;
    public int max_tokens;
}

[Serializable]
public class LogEntry
{
    public string timestamp;
    public string type;
    public string content;
    public string model;
    public float temperature;
    public int max_tokens;
}

[Serializable]
public class LogData
{
    public LogEntry[] entries;
    public int total_requests;
    public string session_start_time;
    public string session_end_time;
}

[Serializable]
public enum LLMProviderKind
{
    OpenAICompatibleChatCompletions,
    CustomJson
}

[Serializable]
public class LLMProviderConfig
{
    public string providerName = "DeepSeek";
    public LLMProviderKind providerKind = LLMProviderKind.OpenAICompatibleChatCompletions;
    public string apiUrl = "https://api.deepseek.com/v1/chat/completions";
    public string apiKey = "sk-6bad1704eca042d4a19133a7837749af";
    public string defaultModel = "deepseek-chat";
    public string authHeaderName = "Authorization";
    public string authHeaderScheme = "Bearer";
    public string contentType = "application/json";
    public string messagesFieldName = "messages";
    public string modelFieldName = "model";
    public string temperatureFieldName = "temperature";
    public string maxTokensFieldName = "max_tokens";
    public string promptFieldName = "prompt";
    public string responseContentPath = "choices[0].message.content";
    [TextArea(2, 6)] public string extraHeadersJson = "";
    [TextArea(3, 10)] public string extraBodyJson = "";
}

[Serializable]
public class LLMRequestOptions
{
    public string prompt;
    public string model;
    public float temperature = 0.7f;
    public int maxTokens = 500;
}

public class LLMInterface : MonoBehaviour
{
    [Header("LLM Provider")]
    [SerializeField] private LLMProviderConfig providerConfig = new LLMProviderConfig
    {
        providerName = "Qwen",
        providerKind = LLMProviderKind.OpenAICompatibleChatCompletions,
        apiUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
        apiKey = "sk-6bad1704eca042d4a19133a7837749af",
        defaultModel = "qwen3.5-plus",
        authHeaderName = "Authorization",
        authHeaderScheme = "Bearer",
        contentType = "application/json",
        messagesFieldName = "messages",
        modelFieldName = "model",
        temperatureFieldName = "temperature",
        maxTokensFieldName = "max_tokens",
        promptFieldName = "prompt",
        responseContentPath = "choices[0].message.content",
        extraHeadersJson = "",
        extraBodyJson = ""
    };


    [SerializeField] public TMP_Text logContentUI;
    [SerializeField] public ScrollRect logScrollRect;

    private string fullLog = "";
    private string resultText = "";
    private readonly List<LogEntry> _logEntries = new List<LogEntry>();
    public IReadOnlyList<LogEntry> LogEntries => _logEntries;

    private int requestCount;
    private string sessionId;
    private string logDirectoryPath;
    private string logFilePath;
    private DateTime sessionStartTime;
    private AgentProperties agentProperties;

    private AgentProperties AgentProps =>
        agentProperties ??= GetComponent<IntelligentAgent>()?.Properties;

    private void Start()
    {
        sessionStartTime = DateTime.Now;
        sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        string scriptPath = Path.GetDirectoryName(Application.dataPath) + "/Scripts/API/LLM_Logs";
        logDirectoryPath = scriptPath;

        if (!Directory.Exists(logDirectoryPath))
        {
            Directory.CreateDirectory(logDirectoryPath);
        }

        logFilePath = Path.Combine(logDirectoryPath, $"llm_session_{sessionId}.json");
        StartCoroutine(AutoSaveCoroutine());
    }

    private void AppendToLog(string message, string type = "", string model = "", float temperature = 0f, int maxTokens = 0)
    {
        fullLog += message + "\n\n---\n\n";

        if (logContentUI != null)
            logContentUI.text = fullLog;

        if (logScrollRect != null)
            StartCoroutine(ScrollToBottomAfterLayout());

        LogEntry entry = new LogEntry
        {
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            type = type,
            content = message,
            model = model,
            temperature = temperature,
            max_tokens = maxTokens
        };

        _logEntries.Add(entry);
    }

    private IEnumerator ScrollToBottomAfterLayout()
    {
        yield return null;
        logScrollRect.verticalNormalizedPosition = 0f;
    }

    private void SaveLogToFile()
    {
        if (string.IsNullOrEmpty(logFilePath) || _logEntries.Count == 0)
            return;

        try
        {
            string txtFilePath = Path.ChangeExtension(logFilePath, ".txt");

            using (StreamWriter writer = new StreamWriter(txtFilePath, false, System.Text.Encoding.UTF8))
            {
                writer.WriteLine("=== LLM Session Log ===");
                writer.WriteLine($"Session Start: {sessionStartTime:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"Session End  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"Total Requests: {requestCount}");
                writer.WriteLine("========================\n");

                foreach (LogEntry entry in _logEntries)
                {
                    writer.WriteLine($"[{entry.timestamp}] [{entry.type}]");
                    writer.WriteLine($"Model       : {entry.model}");
                    writer.WriteLine($"Temperature : {entry.temperature}");
                    writer.WriteLine($"Max Tokens  : {entry.max_tokens}");
                    writer.WriteLine("Content:");
                    writer.WriteLine(entry.content);
                    writer.WriteLine("\n--------------------------------\n");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[LLMInterface] Failed to save log file: {e.Message}");
        }
    }

    public IEnumerator SendRequest(string prompt, Action<string> callback, string model = null, float temperature = 0.7f, int maxTokens = 500)
    {
        yield return SendRequest(
            new LLMRequestOptions
            {
                prompt = prompt,
                model = model,
                temperature = temperature,
                maxTokens = maxTokens
            },
            callback);
    }

    public IEnumerator SendRequest(LLMRequestOptions options, Action<string> callback)
    {
        requestCount++;

        if (options == null || string.IsNullOrWhiteSpace(options.prompt))
        {
            Debug.LogError("[LLMInterface] SendRequest missing prompt.");
            callback?.Invoke(null);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(providerConfig.apiUrl))
        {
            Debug.LogError("[LLMInterface] Provider API URL is not configured.");
            callback?.Invoke(null);
            yield break;
        }

        string resolvedModel = string.IsNullOrWhiteSpace(options.model)
            ? providerConfig.defaultModel
            : options.model;

        string jsonData = BuildRequestJson(options, resolvedModel);
        byte[] rawData = System.Text.Encoding.UTF8.GetBytes(jsonData);

        Debug.Log($"{AgentProps?.AgentID ?? "Unknown"}: Sending request: {jsonData}");

        using (UnityWebRequest request = new UnityWebRequest(providerConfig.apiUrl, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(rawData);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", providerConfig.contentType);
            ApplyHeaders(request);

            AppendToLog(
                $"send:\n provider: {providerConfig.providerName}\n url: {providerConfig.apiUrl}\n model: {resolvedModel}\n temperature: {options.temperature}\n maxTokens: {options.maxTokens}\n prompt: {options.prompt}",
                "send",
                resolvedModel,
                options.temperature,
                options.maxTokens);

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                string responseText = ExtractContent(request.downloadHandler.text);
                if (!string.IsNullOrWhiteSpace(responseText))
                {
                    resultText = responseText.Trim();
                    callback?.Invoke(resultText);
                    AppendToLog($"ReceiveResponse:\n {resultText}", "receive", resolvedModel, options.temperature, options.maxTokens);
                }
                else
                {
                    Debug.LogError("[LLMInterface] No usable text content found in response.");
                    callback?.Invoke(null);
                    AppendToLog("Error: no usable text content found in response.", "error", resolvedModel, options.temperature, options.maxTokens);
                }
            }
            else
            {
                Debug.LogError($"[LLMInterface] {providerConfig.providerName} API error: {request.error}");
                Debug.LogError($"Response: {request.downloadHandler.text}");
                callback?.Invoke(null);
                AppendToLog($"API Error: {request.error}\nResponse Content: {request.downloadHandler.text}", "error", resolvedModel, options.temperature, options.maxTokens);
            }
        }

        SaveLogToFile();
    }

    private string BuildRequestJson(LLMRequestOptions options, string resolvedModel)
    {
        JObject body = ParseJsonObject(providerConfig.extraBodyJson);

        switch (providerConfig.providerKind)
        {
            case LLMProviderKind.CustomJson:
                if (!string.IsNullOrWhiteSpace(providerConfig.promptFieldName))
                    body[providerConfig.promptFieldName] = options.prompt;
                if (!string.IsNullOrWhiteSpace(providerConfig.modelFieldName))
                    body[providerConfig.modelFieldName] = resolvedModel;
                if (!string.IsNullOrWhiteSpace(providerConfig.temperatureFieldName))
                    body[providerConfig.temperatureFieldName] = options.temperature;
                if (!string.IsNullOrWhiteSpace(providerConfig.maxTokensFieldName))
                    body[providerConfig.maxTokensFieldName] = options.maxTokens;
                break;

            case LLMProviderKind.OpenAICompatibleChatCompletions:
            default:
                body[providerConfig.modelFieldName] = resolvedModel;
                body[providerConfig.messagesFieldName] = new JArray
                {
                    JObject.FromObject(new Message { role = "user", content = options.prompt })
                };
                body[providerConfig.temperatureFieldName] = options.temperature;
                body[providerConfig.maxTokensFieldName] = options.maxTokens;
                break;
        }

        return body.ToString();
    }

    private void ApplyHeaders(UnityWebRequest request)
    {
        if (!string.IsNullOrWhiteSpace(providerConfig.authHeaderName) &&
            !string.IsNullOrWhiteSpace(providerConfig.apiKey))
        {
            string authValue = string.IsNullOrWhiteSpace(providerConfig.authHeaderScheme)
                ? providerConfig.apiKey
                : $"{providerConfig.authHeaderScheme} {providerConfig.apiKey}";
            request.SetRequestHeader(providerConfig.authHeaderName, authValue);
        }

        JObject extraHeaders = ParseJsonObject(providerConfig.extraHeadersJson);
        foreach (JProperty header in extraHeaders.Properties())
        {
            request.SetRequestHeader(header.Name, header.Value?.ToString() ?? string.Empty);
        }
    }

    private string ExtractContent(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            return null;

        try
        {
            JObject response = JObject.Parse(responseJson);
            JToken token = response.SelectToken(providerConfig.responseContentPath);
            if (token == null)
                return null;

            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
        }
        catch (Exception e)
        {
            Debug.LogError($"[LLMInterface] Failed to parse response: {e.Message}");
            return null;
        }
    }

    private JObject ParseJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JObject();

        try
        {
            return JObject.Parse(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[LLMInterface] Failed to parse JSON config and ignored it: {e.Message}");
            return new JObject();
        }
    }

    public void ManualSaveLog()
    {
        SaveLogToFile();
    }

    public void SetModel(string model)
    {
        if (!string.IsNullOrWhiteSpace(model))
            providerConfig.defaultModel = model;
    }

    public string GetCurrentModel() => providerConfig.defaultModel;

    private IEnumerator AutoSaveCoroutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(60);
            if (_logEntries.Count > 0)
                SaveLogToFile();
        }
    }

    private void OnApplicationQuit()
    {
        SaveLogToFile();
        Debug.Log("[LLMInterface] Application quit, log saved.");
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
            SaveLogToFile();
    }
}
