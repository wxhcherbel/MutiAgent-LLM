// LLM_API/LLMJsonUtils.cs
// 共用 JSON 提取工具类，合并 ActionDecisionModule 与 PlanningModule 中的重复实现。
using System.Text.RegularExpressions;

/// <summary>
/// 从 LLM 回复字符串中提取 JSON 主体（去除 ```json...``` 包裹及前后文字）。
/// </summary>
public static class LLMJsonUtils
{
    private static readonly Regex JsonBlockRe =
        new Regex(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.Compiled);

    /// <summary>
    /// 容错提取 JSON：
    /// 1) 优先匹配 ```json...``` 代码块
    /// 2) 退而找到第一个 '[' 或 '{' 到最后一个 ']' 或 '}'
    /// 3) 实在找不到返回原始字符串
    /// </summary>
    public static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        Match m = JsonBlockRe.Match(raw);
        if (m.Success) return m.Groups[1].Value.Trim();

        int start    = raw.IndexOf('[');
        int startObj = raw.IndexOf('{');
        if (startObj >= 0 && (start < 0 || startObj < start)) start = startObj;

        if (start >= 0)
        {
            char open  = raw[start];
            char close = open == '[' ? ']' : '}';
            int end    = raw.LastIndexOf(close);
            if (end > start) return raw.Substring(start, end - start + 1);
        }

        return raw.Trim();
    }
}
