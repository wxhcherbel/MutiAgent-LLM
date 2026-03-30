using System;

/// <summary>
/// 聊天请求中的单条消息。
/// </summary>
[Serializable]
public class Message
{
    /// <summary>消息角色，例如 user。</summary>
    public string role;

    /// <summary>消息正文。</summary>
    public string content;
}

/// <summary>
/// 单条 LLM 调用日志。
/// </summary>
[Serializable]
public class LogEntry
{
    /// <summary>日志时间戳字符串。</summary>
    public string timestamp;

    /// <summary>日志类型，例如 send、receive 或 error。</summary>
    public string type;

    /// <summary>日志主体内容。</summary>
    public string content;

    /// <summary>请求使用的模型名。</summary>
    public string model;

    /// <summary>请求温度参数。</summary>
    public float temperature;

    /// <summary>请求的最大 token 数。</summary>
    public int max_tokens;
}

/// <summary>
/// LLM 提供方协议类型。
/// </summary>
[Serializable]
public enum LLMProviderKind
{
    OpenAICompatibleChatCompletions,
    CustomJson
}

/// <summary>
/// LLM 提供方连接配置。
/// </summary>
[Serializable]
public class LLMProviderConfig
{
    /// <summary>提供方显示名称。</summary>
    public string providerName = "DeepSeek";

    /// <summary>请求协议类型。</summary>
    public LLMProviderKind providerKind = LLMProviderKind.OpenAICompatibleChatCompletions;

    /// <summary>HTTP 接口地址。</summary>
    public string apiUrl = "https://api.deepseek.com/v1/chat/completions";

    /// <summary>访问接口所用的 API Key。</summary>
    public string apiKey = "sk-6bad1704eca042d4a19133a7837749af";

    /// <summary>默认模型名称。</summary>
    public string defaultModel = "deepseek-chat";

    /// <summary>认证请求头名称。</summary>
    public string authHeaderName = "Authorization";

    /// <summary>认证请求头前缀，例如 Bearer。</summary>
    public string authHeaderScheme = "Bearer";

    /// <summary>Content-Type 请求头值。</summary>
    public string contentType = "application/json";

    /// <summary>请求体中消息数组字段名。</summary>
    public string messagesFieldName = "messages";

    /// <summary>请求体中模型字段名。</summary>
    public string modelFieldName = "model";

    /// <summary>请求体中温度字段名。</summary>
    public string temperatureFieldName = "temperature";

    /// <summary>请求体中最大 token 字段名。</summary>
    public string maxTokensFieldName = "max_tokens";

    /// <summary>自定义 JSON 协议下的 prompt 字段名。</summary>
    public string promptFieldName = "prompt";

    /// <summary>从响应 JSON 中提取文本的 SelectToken 路径。</summary>
    public string responseContentPath = "choices[0].message.content";

    /// <summary>额外请求头 JSON 配置文本。</summary>
    [UnityEngine.TextArea(2, 6)] public string extraHeadersJson = "";

    /// <summary>额外请求体 JSON 配置文本。</summary>
    [UnityEngine.TextArea(3, 10)] public string extraBodyJson = "";
}

/// <summary>
/// 单次 LLM 请求选项。
/// </summary>
[Serializable]
public class LLMRequestOptions
{
    /// <summary>本次请求的主提示词。</summary>
    public string prompt;

    /// <summary>本次请求指定的模型名，留空时使用默认模型。</summary>
    public string model;

    /// <summary>本次请求的温度参数。</summary>
    public float temperature = 0.7f;

    /// <summary>本次请求允许生成的最大 token 数。</summary>
    public int maxTokens = 500;

    /// <summary>
    /// 是否启用 JSON 输出模式（response_format: json_object）。
    /// 设为 true 时，LLM 被强制输出合法 JSON，无需正则抠取。
    /// 仅当 Prompt 中已明确要求输出 JSON 时才启用，否则部分 API 会报错。
    /// </summary>
    public bool enableJsonMode = false;

    /// <summary>可选的调用标签，用于结构化日志（如 "LLM#1"、"ADM_Roll"）。</summary>
    public string callTag = "";
}
