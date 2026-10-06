using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TranscribeMeetingUI
{
    public class Summarizer
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private readonly AppSettings settings;
        private DeepSeekHandler? deepSeekHandler;
        private string currentContext = "meeting"; // Default context

        public Summarizer(AppSettings settings)
        {
            this.settings = settings;

            if (settings.UseDeepSeek && !string.IsNullOrEmpty(settings.DeepSeekKey))
            {
                deepSeekHandler = new DeepSeekHandler(
                    settings.DeepSeekKey, settings.DeepSeekModel, settings.DeepSeekApiUrl);
            }
        }

        public void SetContext(string context)
        {
            currentContext = context.ToLower();
            System.Diagnostics.Debug.WriteLine($"[Summarizer] Context set to: {currentContext}");
        }

        public async Task<string> SummarizeAsync(string transcript)
        {
            if (settings.UseDeepSeek)
            {
                return await SummarizeWithDeepSeekAsync(transcript);
            }
            else
            {
                return await SummarizeWithOllamaAsync(transcript);
            }
        }

        public async Task<string> SummarizeChunkAsync(string chunkTranscript, int chunkNumber)
        {
            if (settings.UseDeepSeek)
            {
                if (deepSeekHandler == null)
                {
                    throw new Exception("DeepSeek 未配置。");
                }
                deepSeekHandler.SetContext(currentContext);
                return await deepSeekHandler.SummarizeChunkAsync(chunkTranscript, chunkNumber);
            }
            else
            {
                return await SummarizeChunkWithOllamaAsync(chunkTranscript, chunkNumber);
            }
        }

        public async Task<string> SummarizeCombinedChunksAsync(string combinedSummaries, int totalChunks)
        {
            if (settings.UseDeepSeek)
            {
                if (deepSeekHandler == null)
                {
                    throw new Exception("DeepSeek 未配置。");
                }
                deepSeekHandler.SetContext(currentContext);
                return await deepSeekHandler.SummarizeCombinedChunksAsync(combinedSummaries, totalChunks);
            }
            else
            {
                return await SummarizeCombinedChunksWithOllamaAsync(combinedSummaries, totalChunks);
            }
        }

        private async Task<string> SummarizeWithDeepSeekAsync(string transcript)
        {
            if (deepSeekHandler == null)
            {
                throw new Exception("DeepSeek 未配置，请检查 API 密钥。");
            }
            deepSeekHandler.SetContext(currentContext);
            return await deepSeekHandler.SummarizeAsync(transcript);
        }

        private async Task<string> SummarizeWithOllamaAsync(string transcript)
        {
            string prompt = BuildPrompt(transcript, currentContext);
            return await CallOllamaAsync(prompt);
        }

        private async Task<string> SummarizeChunkWithOllamaAsync(string chunkTranscript, int chunkNumber)
        {
            string prompt = BuildChunkPrompt(chunkTranscript, chunkNumber, currentContext);
            return await CallOllamaAsync(prompt);
        }

        private async Task<string> SummarizeCombinedChunksWithOllamaAsync(string combinedSummaries, int totalChunks)
        {
            string prompt = BuildCombinedPrompt(combinedSummaries, totalChunks, currentContext);
            return await CallOllamaAsync(prompt);
        }

        private string BuildPrompt(string transcript, string context)
        {
            return context switch
            {
                "meeting" => $@"以下是一段会议转写文本。请用中文提供简洁的摘要，包含：
- 会议概述
- 关键讨论点
- 达成的决定
- 行动项及负责人
- 后续步骤

转写文本：
""""""
{transcript}
""""""",

                "lecture" => $@"以下是一段讲座转写文本。请用中文提供全面的摘要，包含：
- 主题与学习目标
- 讲解的关键概念与理论
- 提到的重要例子或案例
- 学生的关键收获

转写文本：
""""""
{transcript}
""""""",

                "interview" => $@"以下是一段访谈转写文本。请用中文提供摘要，包含：
- 受访者背景
- 讨论的主要话题
- 分享的关键见解与观点
- 值得注意的引述或言论
- 结论或关键收获

转写文本：
""""""
{transcript}
""""""",

                "podcast" => $@"以下是一期播客的转写文本。请用中文提供摘要，包含：
- 节目主题与主旨
- 主要讨论点
- 分享的有趣见解或故事
- 嘉宾观点（如有）
- 听众的关键收获

转写文本：
""""""
{transcript}
""""""",

                _ => $@"以下是一段转写文本。请用中文提供简洁的摘要，包含要点与亮点。

转写文本：
""""""
{transcript}
"""""""
            };
        }

        private string BuildChunkPrompt(string chunkTranscript, int chunkNumber, string context)
        {
            string contextType = context == "lecture" ? "讲座" :
                                 context == "interview" ? "访谈" :
                                 context == "podcast" ? "播客" : "内容";

            return $@"这是一段较长{contextType}的第 {chunkNumber} 个片段。请用中文总结该片段中讨论的要点，保持简洁（3-5 条）。

转写文本：
""""""
{chunkTranscript}
""""""";
        }

        private string BuildCombinedPrompt(string combinedSummaries, int totalChunks, string context)
        {
            return context switch
            {
                "meeting" => $@"我有来自一场长会议 {totalChunks} 个片段的摘要。请用中文生成一份全面的最终摘要，包含：

1. 会议整体概述与目的
2. 讨论的主要话题（按时间或主题组织）
3. 达成的关键决定
4. 行动项及负责人
5. 后续步骤与跟进事项

以下是各片段摘要：

{combinedSummaries}

请为整场会议提供结构清晰、内容全面的摘要。",

                "lecture" => $@"我有来自一场长讲座 {totalChunks} 个片段的摘要。请用中文生成一份全面的最终摘要，包含：

1. 讲座标题与主题
2. 讲解的关键概念与理论（按讲解顺序）
3. 重要的例子或演示
4. 对学生的关键启示
5. 关键收获总结

以下是各片段摘要：

{combinedSummaries}

请为整场讲座提供结构清晰、内容全面的摘要。",

                "interview" => $@"我有来自一场长访谈 {totalChunks} 个片段的摘要。请用中文生成一份全面的最终摘要，包含：

1. 访谈概述与背景
2. 涉及的主要话题
3. 受访者的关键见解
4. 值得注意的引述或难忘时刻
5. 总体结论

以下是各片段摘要：

{combinedSummaries}

请为整场访谈提供结构清晰、内容全面的摘要。",

                "podcast" => $@"我有来自一期长播客 {totalChunks} 个片段的摘要。请用中文生成一份全面的最终摘要，包含：

1. 节目概述与主旨
2. 关键讨论话题（按顺序）
3. 分享的有趣故事或见解
4. 嘉宾贡献（如有）
5. 听众的主要收获

以下是各片段摘要：

{combinedSummaries}

请为整期播客提供结构清晰、内容全面的摘要。",

                _ => $@"我有来自 {totalChunks} 个片段的摘要。请用中文生成一份全面的最终摘要，包含：

1. 整体概述
2. 讨论的主要话题
3. 关键见解
4. 重要要点
5. 收获

以下是各片段摘要：

{combinedSummaries}

请提供结构清晰、内容全面的摘要。"
            };
        }

        private async Task<string> CallOllamaAsync(string prompt)
        {
            var requestBody = new OllamaChatRequest
            {
                Model = settings.OllamaModel,
                Messages = new[] { new OllamaMessage { Role = "user", Content = prompt } },
                Stream = false
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"{settings.OllamaUrl}/api/chat", content);

            try
            {
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException e)
            {
                throw new Exception($"调用 Ollama API 失败。Ollama 是否正在 {settings.OllamaUrl} 运行？错误：{e.Message}");
            }

            var responseString = await response.Content.ReadAsStringAsync();
            var ollamaResponse = JsonSerializer.Deserialize<OllamaChatResponse>(responseString);

            return ollamaResponse?.Message?.Content ?? "未收到摘要内容。";
        }

        private class OllamaChatRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = "";
            [JsonPropertyName("messages")]
            public OllamaMessage[] Messages { get; set; } = Array.Empty<OllamaMessage>();
            [JsonPropertyName("stream")]
            public bool Stream { get; set; }
        }

        private class OllamaMessage
        {
            [JsonPropertyName("role")]
            public string Role { get; set; } = "";
            [JsonPropertyName("content")]
            public string Content { get; set; } = "";
        }

        private class OllamaChatResponse
        {
            [JsonPropertyName("message")]
            public OllamaMessage? Message { get; set; }
        }
    }
}