using System;

[System.Serializable]
public class LLMResponse
{
    public string id;
    public string @object;
    public long created;
    public string model;
    public Choice[] choices;
    public Usage usage;

    [System.Serializable]
    public class Choice
    {
        public int index;
        public Message message;
        public string finish_reason;
        public object logprobs; // 通常是null

        [System.Serializable]
        public class Message
        {
            public string role;
            public string content;
        }
    }

    [System.Serializable]
    public class Usage
    {
        public int prompt_tokens;
        public int completion_tokens;
        public int total_tokens;
    }
}