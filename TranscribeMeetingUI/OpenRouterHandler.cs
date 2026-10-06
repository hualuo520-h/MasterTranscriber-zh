using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TranscribeMeetingUI
{
    /// <summary>
    /// DeepSeek（OpenAI 兼容）摘要客户端。
    /// 同时也适用于任何 OpenAI 格式的 /chat/completions 端点。
    /// </summary>
    public class DeepSeekHandler
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private readonly string apiKey;
        private readonly string modelName;
        private readonly string apiUrl;
        private string currentContext = "meeting";

        public DeepSeekHandler(string apiKey, string modelName, string apiUrl)
        {
            this.apiKey = apiKey;
            this.modelName = modelName;
            this.apiUrl = string.IsNullOrWhiteSpace(apiUrl)
                ? "https://api.deepseek.com/chat/completions"
                : apiUrl;
        }

        public void SetContext(string context)
        {
            currentContext = context.ToLower();
        }

        public async Task<string> SummarizeAsync(string transcript)
        {
            string prompt = BuildPrompt(transcript, currentContext);
            return await GetCompletionAsync(prompt);
        }

        public async Task<string> SummarizeChunkAsync(string chunkTranscript, int chunkNumber)
        {
            string prompt = BuildChunkPrompt(chunkTranscript, chunkNumber, currentContext);
            return await GetCompletionAsync(prompt);
        }

        public async Task<string> SummarizeCombinedChunksAsync(string combinedSummaries, int totalChunks)
        {
            string prompt = BuildCombinedPrompt(combinedSummaries, totalChunks, currentContext);
            return await GetCompletionAsync(prompt);
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

        private async Task<string> GetCompletionAsync(string userMessage)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[DeepSeek] Calling API with model: {modelName}");

                var messages = new List<object>
                {
                    new { role = "user", content = userMessage }
                };

                var requestBody = new
                {
                    model = modelName,
                    messages = messages,
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Set authorization header for this request
                using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
                request.Content = content;

                var response = await httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"[DeepSeek] Error: {response.StatusCode} - {errorBody}");
                    throw new Exception($"DeepSeek API 错误（{response.StatusCode}）：{errorBody}");
                }

                string responseBody = await response.Content.ReadAsStringAsync();

                using (JsonDocument doc = JsonDocument.Parse(responseBody))
                {
                    var message = doc.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("message")
                        .GetProperty("content")
                        .GetString();

                    System.Diagnostics.Debug.WriteLine($"[DeepSeek] Success: {message?.Length ?? 0} chars");
                    return message ?? "未收到响应内容。";
                }
            }
            catch (HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DeepSeek] HTTP Error: {ex.StatusCode} - {ex.Message}");
                throw new Exception($"DeepSeek API 调用失败：{ex.Message}", ex);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DeepSeek] Exception: {ex.Message}");
                throw;
            }
        }
    }
}