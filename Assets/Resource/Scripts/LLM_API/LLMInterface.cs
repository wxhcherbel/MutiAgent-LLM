// Scripts/API/LLMInterface.cs
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using System;
using UnityEngine.UI;
using TMPro;
using System.IO;
using Newtonsoft.Json;

[System.Serializable]
public class Message
{
    public string role;
    public string content;
}

[System.Serializable]
public class ChatRequest
{
    public string model;
    public Message[] messages;
    public float temperature;
    public int max_tokens;
}

[System.Serializable]
public class LogEntry
{
    public string timestamp;
    public string type;
    public string content;
    public string model;
    public float temperature;
    public int max_tokens;
}

[System.Serializable]
public class LogData
{
    public LogEntry[] entries;
    public int total_requests;
    public string session_start_time;
    public string session_end_time;
}

public class LLMInterface : MonoBehaviour
{
    [SerializeField] private string apiKey = "sk-3ea0c304f779484cbb1b3ff24cdcb228";
    [SerializeField] private string apiUrl = "https://api.deepseek.com/v1/chat/completions";
    
    [SerializeField] public TMP_Text logContentUI;
    [SerializeField] public ScrollRect logScrollRect;
    private string fullLog = "";
    private string resultText = "";

    private List<LogEntry> logEntries = new List<LogEntry>();
    private int requestCount = 0;
    private string sessionId;
    private string logDirectoryPath;
    private string logFilePath;
    private DateTime sessionStartTime;

    void Start()
    {
        // 初始化会话ID和日志目录
        sessionStartTime = DateTime.Now;
        sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        
        // 获取脚本所在目录的路径
        string scriptPath = Path.GetDirectoryName(Application.dataPath) + "/Scripts/API/LLM_Logs";
        logDirectoryPath = scriptPath;
        
        // 创建日志目录（如果不存在）
        if (!Directory.Exists(logDirectoryPath))
        {
            Directory.CreateDirectory(logDirectoryPath);
        }
        
        // 设置日志文件路径
        logFilePath = Path.Combine(logDirectoryPath, $"llm_session_{sessionId}.json");
        
        //Debug.Log($"日志目录: {logDirectoryPath}");
        //Debug.Log($"日志文件: {logFilePath}");
        
        // 启动自动保存协程
        StartCoroutine(AutoSaveCoroutine());
    }

    private void AppendToLog(string message, string type = "", string model = "", float temperature = 0f, int max_tokens = 0)
    {
        fullLog += message + "\n\n---\n\n";
        
        if (logContentUI != null)
            logContentUI.text = fullLog;
        
        if (logScrollRect != null)
        {
            StartCoroutine(ScrollToBottomAfterLayout());
        }

        LogEntry entry = new LogEntry
        {
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            type = type,
            content = message,
            model = model,
            temperature = temperature,
            max_tokens = max_tokens
        };
        
        logEntries.Add(entry);
    }

    private IEnumerator ScrollToBottomAfterLayout()
    {
        yield return null;
        logScrollRect.verticalNormalizedPosition = 0f;
    }

    // 保存日志到TXT文件（覆盖模式）
    private void SaveLogToFile()
    {
        if (string.IsNullOrEmpty(logFilePath) || logEntries.Count == 0)
            return;

        try
        {
            // 把扩展名改成 .txt
            string txtFilePath = Path.ChangeExtension(logFilePath, ".txt");

            using (StreamWriter writer = new StreamWriter(txtFilePath, false, System.Text.Encoding.UTF8))
            {
                writer.WriteLine("=== LLM Session Log ===");
                writer.WriteLine($"Session Start: {sessionStartTime:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"Session End  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"Total Requests: {requestCount}");
                writer.WriteLine("========================\n");

                foreach (var entry in logEntries)
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

            Debug.Log($"日志已保存到: {txtFilePath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"保存日志文件失败: {e.Message}");
        }
    }


    // 使用Chat Completions API
    public IEnumerator SendRequest(string prompt, System.Action<string> callback,
        string model = "deepseek-chat", float temperature = 0.7f, int maxTokens = 500)
    {
        requestCount++;
        
        ChatRequest requestData = new ChatRequest
        {
            model = model,
            messages = new Message[]
            {
                new Message { role = "user", content = prompt }
            },
            temperature = temperature,
            max_tokens = maxTokens
        };

        string jsonData = JsonUtility.ToJson(requestData);
        byte[] rawData = System.Text.Encoding.UTF8.GetBytes(jsonData);

        Debug.Log("Sending request: " + jsonData);

        using (UnityWebRequest request = new UnityWebRequest(apiUrl, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(rawData);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + apiKey);

            AppendToLog($"send:\n model: {model}\n temperature: {temperature}\n maxTokens: {maxTokens}\n prompt: {prompt}", 
                       "send", model, temperature, maxTokens);

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                LLMResponse response = JsonUtility.FromJson<LLMResponse>(request.downloadHandler.text);
                if (response.choices != null && response.choices.Length > 0)
                {
                    resultText = response.choices[0].message.content.Trim();
                    callback?.Invoke(resultText);
                    
                    AppendToLog($"ReceiveResponse:\n {resultText}", "receive", model, temperature, maxTokens);
                }
                else
                {
                    Debug.LogError("No Return Choices found in response.");
                    callback?.Invoke(null);
                    
                    AppendToLog("错误: 响应中没有返回选择", "error", model, temperature, maxTokens);
                }
            }
            else
            {
                Debug.LogError("DeepSeek API错误: " + request.error);
                Debug.LogError("响应: " + request.downloadHandler.text);
                callback?.Invoke(null);
                
                AppendToLog($"API Error: {request.error}\n Response Content: {request.downloadHandler.text}", 
                           "error", model, temperature, maxTokens);
            }
        }

        // 每次请求后自动保存日志（覆盖同一个文件）
        SaveLogToFile();
    }

    // 手动保存方法
    public void ManualSaveLog()
    {
        SaveLogToFile();
    }

    // 定期自动保存
    private IEnumerator AutoSaveCoroutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(60); // 每1分钟自动保存一次
            if (logEntries.Count > 0)
            {
                SaveLogToFile();
            }
        }
    }

    // 应用退出时自动保存
    private void OnApplicationQuit()
    {
        SaveLogToFile();
        Debug.Log("应用程序退出，日志已保存");
    }

    // 应用暂停时保存（移动设备）
    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            SaveLogToFile();
        }
    }
}