using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TranscribeMeetingUI
{
    public class CaptionUpdatedEventArgs : EventArgs
    {
        /// <summary>已确认的字幕文本（不会再变化）。</summary>
        public string Committed { get; set; } = "";
        /// <summary>尚未确认的临时文本（下一轮可能被改写）。</summary>
        public string Partial { get; set; } = "";
        /// <summary>本轮推理耗时（秒）。</summary>
        public double LatencySeconds { get; set; }
        /// <summary>观感延迟：这次新确认的文字，对应的音频已经过去多久（秒）。</summary>
        public double LagSeconds { get; set; }
    }

    /// <summary>
    /// 实时字幕引擎：麦克风采集 → 滚动缓冲 → whisper-server 推理 → LocalAgreement-2 确认。
    ///
    /// 核心思路（业界实时字幕的标准做法）：
    ///   1. 维护一个滚动音频缓冲（默认 30 秒），而不是把音频切成互不相干的小块。
    ///      切块会在边界处切断词语，实测会让转写质量严重劣化。
    ///   2. 每隔一小段时间（默认 1.5 秒）把整个缓冲重新送进 whisper 转写一次。
    ///      因为用的是常驻 server + GPU，30 秒音频只需约 0.3 秒，完全跟得上。
    ///   3. LocalAgreement-2：只有当相邻两次推理对同一段文字给出**完全一致**的结果时，
    ///      才把这段文字"确认"下来显示给用户。这样字幕不会闪烁或自我改写。
    ///   4. 确认后立刻把对应的音频从缓冲里裁掉，缓冲永远不会无限增长。
    /// </summary>
    public class LiveCaptionEngine : IDisposable
    {
        private const string Prompt = "以下是普通话的句子，请使用简体中文转写。";

        private readonly string inferenceUrl;
        private readonly string language;
        private readonly double stepSeconds;
        private readonly double windowSeconds;
        private readonly double minTailSeconds = 0.3;

        /// <summary>最后一次确认到的那一点，对应的绝对音频时间（秒）。</summary>
        private double lastCommittedAudioTime = 0;

        private WaveInEvent? capture;
        private readonly object bufferLock = new object();
        private readonly List<byte> pcmBuffer = new List<byte>();
        private long bufferStartSample = 0;   // pcmBuffer[0] 对应的绝对采样序号
        private long totalSamples = 0;        // 已采集到的绝对采样总数
        private int sampleRate = 16000;
        private int channels = 1;
        private int bitsPerSample = 16;

        private CancellationTokenSource? cts;
        private Task? loopTask;
        private readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        /// <summary>上一轮转写的全文（未归一化），用于字符级一致前缀比较。</summary>
        private string prevText = "";

        /// <summary>上一轮转写的全文（归一化：只留字母数字），用于字符级一致前缀比较。</summary>
        private string prevNormalized = "";

        /// <summary>prevText 对应的缓冲原点采样序号；与当前原点不一致说明缓冲已被裁剪，前缀不可比。</summary>
        private long prevOriginSample = -1;

        /// <summary>当前缓冲的全文里，已经吐出去多少个"有效字符"（归一化后的字符数）。</summary>
        private int emittedChars = 0;

        private readonly StringBuilder committed = new StringBuilder();
        private bool disposed = false;

        public event EventHandler<CaptionUpdatedEventArgs>? CaptionUpdated;
        public event EventHandler<string>? StatusUpdated;
        public event EventHandler<string>? ErrorOccurred;

        /// <summary>当前已确认的完整字幕文本。</summary>
        public string CommittedText
        {
            get { lock (bufferLock) return committed.ToString(); }
        }

        public bool IsRunning => loopTask != null && !loopTask.IsCompleted;

        private class Segment
        {
            public double Start;
            public double End;
            public string Text = "";
        }

        public LiveCaptionEngine(string inferenceUrl, string language, double stepSeconds, double windowSeconds)
        {
            this.inferenceUrl = inferenceUrl;
            this.language = string.IsNullOrEmpty(language) ? "auto" : language;
            this.stepSeconds = Math.Max(0.5, stepSeconds);
            this.windowSeconds = Math.Max(5.0, windowSeconds);
        }

        /// <summary>
        /// 不打开麦克风，直接以"喂数据"的方式启动引擎。仅供离线测试使用
        /// （把一段 WAV 按实时节奏喂进来，就能在不开界面、不点鼠标的情况下
        /// 复现真实的提交/裁剪行为）。
        /// </summary>
        public void StartFeedOnly(int rate, int ch, int bits)
        {
            if (IsRunning) return;

            cts = new CancellationTokenSource();
            committed.Clear();
            prevText = "";
            prevNormalized = "";
            prevOriginSample = -1;
            emittedChars = 0;
            lastCommittedAudioTime = 0;
            sampleRate = rate;
            channels = ch;
            bitsPerSample = bits;
            lock (bufferLock)
            {
                pcmBuffer.Clear();
                bufferStartSample = 0;
                totalSamples = 0;
            }

            StatusUpdated?.Invoke(this, "正在聆听……");
            loopTask = Task.Run(() => ProcessLoopAsync(cts.Token));
        }

        /// <summary>把一段 PCM 数据喂进滚动缓冲（等价于麦克风回调）。</summary>
        public void Feed(byte[] data, int offset, int count)
        {
            if (count <= 0) return;
            lock (bufferLock)
            {
                pcmBuffer.AddRange(new ArraySegment<byte>(data, offset, count));
                totalSamples += count / (bitsPerSample / 8) / channels;
            }
        }

        public void Start()
        {
            if (IsRunning) return;

            cts = new CancellationTokenSource();
            committed.Clear();
            prevText = "";
            prevNormalized = "";
            prevOriginSample = -1;
            emittedChars = 0;
            lastCommittedAudioTime = 0;
            lock (bufferLock)
            {
                pcmBuffer.Clear();
                bufferStartSample = 0;
                totalSamples = 0;
            }

            // 直接用 16kHz 单声道 16bit 采集——这正是 whisper 需要的格式，避免重采样
            capture = new WaveInEvent
            {
                WaveFormat = new WaveFormat(sampleRate, bitsPerSample, channels),
                BufferMilliseconds = 100,
                NumberOfBuffers = 4
            };
            sampleRate = capture.WaveFormat.SampleRate;
            channels = capture.WaveFormat.Channels;
            bitsPerSample = capture.WaveFormat.BitsPerSample;

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += (s, e) =>
            {
                if (e.Exception != null)
                {
                    ErrorOccurred?.Invoke(this, $"麦克风采集出错：{e.Exception.Message}");
                }
            };

            try
            {
                capture.StartRecording();
            }
            catch (Exception ex)
            {
                capture.Dispose();
                capture = null;
                throw new Exception($"无法打开麦克风：{ex.Message}", ex);
            }

            StatusUpdated?.Invoke(this, "正在聆听……");
            loopTask = Task.Run(() => ProcessLoopAsync(cts.Token));
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;
            lock (bufferLock)
            {
                pcmBuffer.AddRange(new ArraySegment<byte>(e.Buffer, 0, e.BytesRecorded));
                totalSamples += e.BytesRecorded / (bitsPerSample / 8) / channels;
            }
        }

        private async Task ProcessLoopAsync(CancellationToken token)
        {
            int stepMs = (int)(stepSeconds * 1000);

            // 先立刻跑一轮，让字幕尽快出现，而不是干等一个 step
            while (!token.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    await ProcessOnceAsync(token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LiveCaption] 推理出错：{ex.Message}");
                    StatusUpdated?.Invoke(this, $"识别出错，正在重试……（{ex.Message}）");
                }

                // 自适应的节奏控制：把本轮推理耗时从等待里扣掉，
                // 这样即使推理变慢，字幕的更新频率也不会被拖垮。
                sw.Stop();
                int waitMs = stepMs - (int)sw.Elapsed.TotalMilliseconds;
                if (waitMs < 100) waitMs = 100;

                try
                {
                    await Task.Delay(waitMs, token);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task ProcessOnceAsync(CancellationToken token)
        {
            byte[] snapshot;
            long originSample;
            long newestSample;
            lock (bufferLock)
            {
                if (pcmBuffer.Count < sampleRate * channels * (bitsPerSample / 8) / 4) return; // 少于 0.25 秒，不值得跑
                snapshot = pcmBuffer.ToArray();
                originSample = bufferStartSample;
                newestSample = totalSamples;
            }

            double originTime = (double)originSample / sampleRate;
            double newestTime = (double)newestSample / sampleRate;

            var wav = BuildWav(snapshot);
            var sw = Stopwatch.StartNew();
            var segments = await TranscribeAsync(wav, token);
            sw.Stop();

            if (segments.Count == 0)
            {
                // 没有识别到内容；若缓冲过长则丢弃旧音频，避免无限增长
                if (newestTime - originTime > windowSeconds)
                {
                    TrimTo(newestSample - (long)(minTailSeconds * sampleRate));
                }
                RaiseCaption("", sw.Elapsed.TotalSeconds);
                return;
            }

            string curText = string.Concat(segments.Select(s => s.Text));
            string curNormalized = Normalize(curText);

            // LocalAgreement-2：只有两次推理一致的部分才可信。
            //
            // 这里做的是**字符级**最长公共前缀，而不是逐段比对。原因是 whisper 对
            // "正在进行的连续说话"只返回**一个**段，而且段尾时间戳永远贴着缓冲末尾
            // （实测送 11 秒音频就返回 [0.00, 11.00]）。逐段比对时每轮文本都在增长，
            // 严格相等永不成立，一致前缀恒为 0，字幕会卡住十几秒（实测 19 秒空白）。
            //
            // 换成字符级前缀后，连续说话时前缀会随缓冲增长自然变长，
            // 每轮都能稳定吐出几个新字——完全不依赖时间戳。
            int agreedChars = 0;
            if (prevOriginSample == originSample && prevNormalized.Length > 0)
            {
                int agreedRawEnd = LooseCommonPrefixLength(prevNormalized, curNormalized);
                agreedChars = Normalize(curNormalized.Substring(0, agreedRawEnd)).Length;
            }

            // 把 [emittedChars, agreedChars) 这段新确认的字符吐出去
            if (agreedChars > emittedChars)
            {
                string piece = curText.Substring(RawPrefixLength(curText, emittedChars),
                                                 RawPrefixLength(curText, agreedChars) - RawPrefixLength(curText, emittedChars));
                if (piece.Trim().Length > 0)
                {
                    lock (bufferLock)
                    {
                        committed.Append(piece);
                    }
                    lastCommittedAudioTime = newestTime;
                }
                emittedChars = agreedChars;
            }

            // 裁剪：只在 whisper 自己给出 ≥2 段、且前面若干段已被完整吐出时才做。
            // 裁到"第一个未完整确认段的起点"——这是模型给的真实边界，
            // 不是贴着缓冲末尾的假边界，所以不会把正在说的话切掉。
            double trimTo = -1;
            int consumedChars = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                int len = Normalize(segments[i].Text).Length;
                if (consumedChars + len > emittedChars)
                {
                    if (i > 0) trimTo = segments[i].Start;
                    break;
                }
                consumedChars += len;
            }

            if (trimTo > 0)
            {
                long newOrigin = originSample + (long)(trimTo * sampleRate);
                TrimTo(newOrigin);

                prevText = string.Concat(segments.Where(s => s.End > trimTo + 0.001).Select(s => s.Text));
                prevNormalized = Normalize(prevText);
                prevOriginSample = newOrigin;
                emittedChars = Math.Max(0, emittedChars - consumedChars);

                RaiseCaption(TailAfter(prevText, emittedChars), sw.Elapsed.TotalSeconds, newestTime - lastCommittedAudioTime);
            }
            else
            {
                prevText = curText;
                prevNormalized = curNormalized;
                prevOriginSample = originSample;

                // 字幕上要显示的"还没确认的尾巴"——必须在强裁重置 emittedChars 之前算好，
                // 否则强裁后 emittedChars 归零会把整段已提交文字再显示一遍。
                string pendingTail = TailAfter(curText, emittedChars);

                // 缓冲过长仍无法裁剪时，按"已确认字符占比"估算切点强裁，防止无限增长
                if (newestTime - originTime > windowSeconds)
                {
                    double frac = emittedChars / (double)Math.Max(1, curNormalized.Length);
                    double est = originTime + frac * (newestTime - originTime);
                    double cut = Math.Max(0, est - 1.0);
                    if (cut > originTime)
                    {
                        TrimTo(originSample + (long)((cut - originTime) * sampleRate));
                        prevText = "";
                        prevNormalized = "";
                        prevOriginSample = -1;
                        emittedChars = 0;
                    }
                }

                // 还没确认任何东西时，字幕落后 = 缓冲里已采到的音频都还没变成字
                RaiseCaption(pendingTail, sw.Elapsed.TotalSeconds, newestTime - lastCommittedAudioTime);
            }
        }

        /// <summary>
        /// 取 text 中第 n 个有效字符之后的尾巴——即"还没被提交出去的部分"。
        /// 显示层会把 committed + partial 拼起来，所以 partial 必须去掉已提交的前缀，
        /// 否则同一段文字会在字幕条上重复出现并越滚越长。
        /// </summary>
        private static string TailAfter(string text, int n)
        {
            int idx = RawPrefixLength(text, n);
            return idx >= text.Length ? "" : text.Substring(idx);
        }

        /// <summary>
        /// 返回 text 中"前 n 个有效字符（字母数字）"之后的下标。
        /// 用于把归一化坐标系里的字符数映射回原文下标。
        /// </summary>
        private static int RawPrefixLength(string text, int n)
        {
            if (n <= 0) return 0;
            int cnt = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsLetterOrDigit(text[i])) continue;
                cnt++;
                if (cnt == n) return i + 1;
            }
            return text.Length;
        }

        private void RaiseCaption(string partial, double latency, double lag = 0)
        {
            string full;
            lock (bufferLock) full = committed.ToString();
            CaptionUpdated?.Invoke(this, new CaptionUpdatedEventArgs
            {
                Committed = full,
                Partial = partial,
                LatencySeconds = latency,
                LagSeconds = lag
            });
        }

        private void TrimTo(long newOriginSample)
        {
            lock (bufferLock)
            {
                long drop = newOriginSample - bufferStartSample;
                if (drop <= 0) return;
                int bytesPerSample = (bitsPerSample / 8) * channels;
                long dropBytes = drop * bytesPerSample;
                if (dropBytes >= pcmBuffer.Count)
                {
                    pcmBuffer.Clear();
                }
                else
                {
                    pcmBuffer.RemoveRange(0, (int)dropBytes);
                }
                bufferStartSample = newOriginSample;
            }
        }

        /// <summary>
        /// 字符级最长公共前缀，跳过标点与空白（whisper 的标点抖动不影响一致性判断）。
        /// 返回 b 中已匹配到的字符下标。
        /// </summary>
        private static int LooseCommonPrefixLength(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (IsIgnorable(a[i])) { i++; continue; }
                if (IsIgnorable(b[j])) { j++; continue; }
                if (a[i] != b[j]) break;
                i++; j++;
            }
            return j;
        }

        private static bool IsIgnorable(char c)
        {
            return char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);
        }

        private async Task<List<Segment>> TranscribeAsync(byte[] wav, CancellationToken token)
        {
            using var form = new MultipartFormDataContent();
            var audio = new ByteArrayContent(wav);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audio, "file", "chunk.wav");
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent(language), "language");
            form.Add(new StringContent(Prompt), "prompt");

            using var resp = await httpClient.PostAsync(inferenceUrl, form, token);
            string body = await resp.Content.ReadAsStringAsync(token);
            if (!resp.IsSuccessStatusCode)
            {
                throw new Exception($"识别服务返回 {(int)resp.StatusCode}");
            }

            var list = new List<Segment>();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in segs.EnumerateArray())
                {
                    string text = s.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
                    text = StripPromptEcho(text).Trim();    // 模型有时会把提示词原样吐回来
                    text = StripAnnotations(text).Trim();   // 去掉 (音乐) (听不懂) 这类标注
                    if (text.Length == 0) continue;
                    // 提示词的残缺变体（如「则是普通话的词」「则想用简体中文转写」）没法安全截断，
                    // 整段丢弃、把音频留在缓冲里下一轮重转，避免假文字被永久确认。
                    if (LooksLikePromptEcho(text)) continue;
                    list.Add(new Segment
                    {
                        Start = s.TryGetProperty("start", out var st) ? st.GetDouble() : 0,
                        End = s.TryGetProperty("end", out var en) ? en.GetDouble() : 0,
                        Text = text
                    });
                }
            }
            return list;
        }

        /// <summary>
        /// 去掉模型复读出来的提示词。
        ///
        /// whisper 在音乐/静音/噪声段上经常把 --prompt 原样吐回来，而且会连着复读很多遍，
        /// 开头还会漂移（"以下是…" 变 "这些是…"）。这些假文字一旦被确认就会永久留在字幕里，
        /// 所以按"去掉开头两字后的核心"来匹配并剔除，保留同一段里真正的正文。
        /// </summary>
        private static string StripPromptEcho(string text)
        {
            string a = Normalize(text);
            if (a.Length < 3) return text;
            string p = Normalize(Prompt);
            if (p.Length < 8) return text;

            // 整段就是提示词的一部分
            if (p.Contains(a)) return "";

            string core = p.Substring(2);   // "普通话的句子请使用简体中文转写"
            var sb = new StringBuilder(text);
            // 在归一化串上定位，再按比例映射回原文（归一化只删字符，不改变顺序）
            int hits = 0;
            while (true)
            {
                string cur = Normalize(sb.ToString());
                int idx = cur.IndexOf(core, StringComparison.Ordinal);
                if (idx < 0) break;
                if (++hits > 20) break;   // 防御：避免异常输入导致死循环

                // 把归一化下标映射回原文下标
                int seen = 0, start = -1, end = -1;
                for (int i = 0; i < sb.Length; i++)
                {
                    if (!char.IsLetterOrDigit(sb[i])) continue;
                    if (seen == idx) start = i;
                    if (seen == idx + core.Length - 1) { end = i; break; }
                    seen++;
                }
                if (start < 0 || end < 0) break;
                sb.Remove(start, end - start + 1);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 提示词的特征片段。whisper 复读提示词时开头经常漂移、结尾被截断
        /// （实测出现过「则是普通话的词」「则想用简体中文转写」这类残缺变体），
        /// 用完整提示词匹配抓不到，所以改成"命中任一片段即判定为泄漏"。
        ///
        /// 之所以整段丢弃而不是截断：这些片段在正常说话里几乎不可能连续出现，
        /// 而泄漏一旦被 LocalAgreement 确认就会永久留在字幕里（曾发生过复读 5 遍、115 字）。
        /// </summary>
        private static readonly string[] PromptMarkers =
        {
            "普通话的句子",
            "是普通话的",
            "请使用简体中文",
            "用简体中文",
            "简体中文转写",
        };

        private static bool LooksLikePromptEcho(string text)
        {
            string a = Normalize(text);
            if (a.Length < 5) return false;
            foreach (string m in PromptMarkers)
            {
                if (a.Contains(m)) return true;
            }
            return false;
        }

        /// <summary>
        /// 去掉 whisper 加的非语音标注，例如「(音乐)」「(听不懂)」「（掌声）」。
        /// 这些是模型对噪声段的说明，不是说话内容，留在字幕里会干扰阅读。
        /// 只删括号里命中已知噪声词的部分，正文里的括号（如「（1）」）保留。
        /// </summary>
        private static readonly string[] NoiseAnnotations =
        {
            "音乐", "听不懂", "听不清", "听不清楚", "掌声", "笑声", "静音", "无声", "空白",
            "噪音", "杂音", "咳嗽", "音乐声", "背景音乐", "applause", "music", "silence",
            "blank_audio", "inaudible", "no speech", "laughs", "laughter",
        };

        private static string StripAnnotations(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '(' || c == '（' || c == '[' || c == '【')
                {
                    char close = c == '(' ? ')' : c == '（' ? '）' : c == '[' ? ']' : '】';
                    int end = text.IndexOf(close, i + 1);
                    if (end > i)
                    {
                        string inner = text.Substring(i + 1, end - i - 1).Trim();
                        if (IsNoiseAnnotation(inner))
                        {
                            i = end + 1;
                            continue;
                        }
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static bool IsNoiseAnnotation(string inner)
        {
            if (inner.Length == 0 || inner.Length > 20) return false;
            string low = inner.ToLowerInvariant();
            foreach (string n in NoiseAnnotations)
            {
                if (low.Contains(n)) return true;
            }
            return false;
        }

        private static string Normalize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>把裸 PCM 数据包装成标准 WAV 文件字节。</summary>
        private byte[] BuildWav(byte[] pcm)
        {
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            short blockAlign = (short)(channels * bitsPerSample / 8);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.ASCII);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + pcm.Length);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1);              // PCM
            w.Write((short)channels);
            w.Write(sampleRate);
            w.Write(byteRate);
            w.Write(blockAlign);
            w.Write((short)bitsPerSample);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(pcm.Length);
            w.Write(pcm);
            w.Flush();
            return ms.ToArray();
        }

        public async Task StopAsync()
        {
            try { cts?.Cancel(); } catch { }

            try { capture?.StopRecording(); } catch { }
            capture?.Dispose();
            capture = null;

            if (loopTask != null)
            {
                try { await Task.WhenAny(loopTask, Task.Delay(3000)); } catch { }
                loopTask = null;
            }

            // 收尾：把缓冲里剩下的音频做最后一次转写，保证不漏字
            try
            {
                byte[] rest;
                lock (bufferLock) rest = pcmBuffer.ToArray();
                if (rest.Length > sampleRate * channels * (bitsPerSample / 8))
                {
                    StatusUpdated?.Invoke(this, "正在整理最后一段……");
                    var segments = await TranscribeAsync(BuildWav(rest), CancellationToken.None);
                    if (segments.Count > 0)
                    {
                        lock (bufferLock)
                        {
                            foreach (var s in segments) committed.Append(s.Text);
                        }
                    }
                    lock (bufferLock) pcmBuffer.Clear();
                    RaiseCaption("", 0);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LiveCaption] 收尾转写失败：{ex.Message}");
            }

            StatusUpdated?.Invoke(this, "已停止");
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { cts?.Cancel(); } catch { }
            try { capture?.Dispose(); } catch { }
            try { httpClient.Dispose(); } catch { }
            cts?.Dispose();
        }
    }
}
