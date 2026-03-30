// Scripts/API/LLMInterface.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class LLMInterface : MonoBehaviour
{
    // ─── 可靠性常量 ──────────────────────────────────────────────
    private const int MaxRetries = 3;
    private const int TimeoutSeconds = 30;

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

        // 优先从环境变量加载 API Key，避免明文写入代码/Inspector
        string envKey = System.Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            providerConfig.apiKey = envKey;
            Debug.Log("[LLMInterface] API key loaded from environment variable DASHSCOPE_API_KEY.");
        }

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

        string agentId = AgentProps?.AgentID ?? "Unknown";
        Debug.Log($"{agentId}: Sending request [{options.callTag}]: {jsonData}");

        AppendToLog(
            $"send:\n provider: {providerConfig.providerName}\n url: {providerConfig.apiUrl}\n model: {resolvedModel}\n temperature: {options.temperature}\n maxTokens: {options.maxTokens}\n tag: {options.callTag}\n prompt: {options.prompt}",
            "send", resolvedModel, options.temperature, options.maxTokens);

        // ─── 超时+重试循环 ────────────────────────────────────────────
        string responseText = null;
        bool success = false;
        string lastError = null;
        DateTime sendStart = DateTime.Now;

        for (int attempt = 1; attempt <= MaxRetries && !success; attempt++)
        {
            // 指数退避：第2次等1s，第3次等2s
            if (attempt > 1)
            {
                float delaySec = Mathf.Pow(2f, attempt - 2f); // 1, 2
                Debug.LogWarning($"[LLMInterface] [{options.callTag}] Retry {attempt}/{MaxRetries} in {delaySec}s...");
                yield return new WaitForSeconds(delaySec);
            }

            using (UnityWebRequest request = new UnityWebRequest(providerConfig.apiUrl, "POST"))
            {
                request.uploadHandler   = new UploadHandlerRaw(rawData);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", providerConfig.contentType);
                request.timeout = TimeoutSeconds;
                ApplyHeaders(request);

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    string extracted = ExtractContent(request.downloadHandler.text);
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        responseText = extracted.Trim();
                        success = true;
                    }
                    else
                    {
                        lastError = "no usable content in response";
                        Debug.LogError($"[LLMInterface] [{options.callTag}] {lastError}. Raw: {request.downloadHandler.text}");
                    }
                    break; // 请求成功（无论内容是否有效），不再重试
                }
                else
                {
                    long httpCode = request.responseCode;
                    lastError = $"{request.error} (HTTP {httpCode})";
                    Debug.LogError($"[LLMInterface] [{options.callTag}] Attempt {attempt}/{MaxRetries} failed: {lastError}");
                    Debug.LogError($"[LLMInterface] Response body: {request.downloadHandler.text}");

                    // 4xx 客户端错误（参数错误/鉴权失败）不重试
                    bool isClientError = httpCode >= 400 && httpCode < 500;
                    if (isClientError)
                    {
                        Debug.LogError($"[LLMInterface] 4xx error, no retry.");
                        break;
                    }
                }
            }
        }

        // ─── 处理最终结果 ─────────────────────────────────────────────
        long latencyMs = (long)(DateTime.Now - sendStart).TotalMilliseconds;

        if (success)
        {
            resultText = responseText;
            callback?.Invoke(resultText);
            AppendToLog($"ReceiveResponse [{options.callTag}] ({latencyMs}ms):\n {resultText}", "receive", resolvedModel, options.temperature, options.maxTokens);
        }
        else
        {
            callback?.Invoke(null);
            AppendToLog($"Error [{options.callTag}] ({latencyMs}ms): {lastError ?? "unknown"}", "error", resolvedModel, options.temperature, options.maxTokens);
        }

        WriteJsonlMetric(options.callTag, resolvedModel, success, latencyMs, success ? null : lastError);
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

                // JSON 输出模式：强制 LLM 返回合法 JSON，无需正则提取
                // 注意：仅在 Prompt 已明确要求输出 JSON 时启用，否则部分 API 会报错
                if (options.enableJsonMode)
                {
                    body["response_format"] = new JObject { ["type"] = "json_object" };
                }
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

    /// <summary>
    /// 追加写入一条结构化 JSONL 指标日志，每行一个 JSON 对象，便于后续统计分析。
    /// 文件路径：LLM_Logs/llm_metrics_{sessionId}.jsonl
    /// </summary>
    private void WriteJsonlMetric(string callTag, string model, bool success, long latencyMs, string error = null)
    {
        if (string.IsNullOrEmpty(logDirectoryPath)) return;
        try
        {
            var metric = new
            {
                timestamp  = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                agent      = AgentProps?.AgentID ?? "Unknown",
                tag        = callTag ?? string.Empty,
                model,
                latency_ms = latencyMs,
                success,
                error      = error ?? string.Empty
            };
            string line = JsonConvert.SerializeObject(metric);
            string metricsPath = Path.Combine(logDirectoryPath, $"llm_metrics_{sessionId}.jsonl");
            File.AppendAllText(metricsPath, line + "\n", System.Text.Encoding.UTF8);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[LLMInterface] Failed to write JSONL metric: {e.Message}");
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
