// ThoughtJsonConverter.cs
// Newtonsoft.Json 自定义转换器：将 LLM 返回的 thought 字段统一序列化为字符串。
// 当 LLM 按 Prompt 要求输出 thought 为 JSON 对象时，直接将其序列化成 JSON 字符串存入 string 字段；
// 若 LLM 已返回普通字符串，则原样保留，保证向后兼容。
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class ThoughtJsonConverter : JsonConverter<string>
{
    public override string ReadJson(JsonReader reader, Type objectType, string existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        var token = JToken.Load(reader);
        return token.Type == JTokenType.Null ? null : token.ToString(Formatting.None);
    }

    public override void WriteJson(JsonWriter writer, string value, JsonSerializer serializer)
    {
        writer.WriteValue(value);
    }
}
