using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TranscribeMeetingUI
{
    /// <summary>
    /// 管理常驻的 whisper-server 子进程，用于实时字幕的低延迟推理。
    /// 模型只在启动时加载一次，之后每次请求仅需几十到几百毫秒。
    /// </summary>
    public class WhisperServerManager : IDisposable
    {
        private readonly string exePath;
        private readonly string modelPath;
        private readonly string language;
        private readonly int port;
        private Process? serverProcess;
        private readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        private bool disposed = false;

        public int Port => port;
        public string InferenceUrl => $"http://127.0.0.1:{port}/inference";
        public bool IsRunning => serverProcess != null && !serverProcess.HasExited;

        public WhisperServerManager(string exePath, string modelPath, string language, int port)
        {
            this.exePath = exePath;
            this.modelPath = modelPath;
            this.language = language;
            this.port = port;
        }

        /// <summary>
        /// 从 whisper-cli.exe 的路径推导 whisper-server.exe 的路径。
        /// </summary>
        public static string DeriveServerPath(string cliPath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(cliPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    string candidate = Path.Combine(dir, "whisper-server.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 启动 server 并等待它就绪。
        /// </summary>
        public async Task<bool> StartAsync(IProgress<string>? progress = null)
        {
            if (IsRunning) return true;

            if (!File.Exists(exePath))
            {
                throw new FileNotFoundException($"找不到 whisper-server：{exePath}");
            }
            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException($"找不到 Whisper 模型：{modelPath}");
            }

            // 若端口已被占用（可能是上次残留的 server），直接复用
            if (await PingAsync())
            {
                Debug.WriteLine("[LiveCaption] 复用已在运行的 whisper-server");
                return true;
            }

            string workDir = Path.GetDirectoryName(exePath) ?? ".";
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(modelPath);
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add(string.IsNullOrEmpty(language) ? "auto" : language);
            psi.ArgumentList.Add("--host");
            psi.ArgumentList.Add("127.0.0.1");
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(port.ToString());
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("8");

            serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            // 必须持续排空输出，否则管道写满会让 server 卡死
            serverProcess.OutputDataReceived += (s, e) => { if (e.Data != null) Debug.WriteLine($"[whisper-server] {e.Data}"); };
            serverProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) Debug.WriteLine($"[whisper-server] {e.Data}"); };

            progress?.Report("正在启动语音识别引擎……");
            serverProcess.Start();
            serverProcess.BeginOutputReadLine();
            serverProcess.BeginErrorReadLine();

            // 等待就绪：模型加载通常需要 1-3 秒
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 60)
            {
                if (serverProcess.HasExited)
                {
                    throw new Exception($"语音识别引擎启动失败（退出代码 {serverProcess.ExitCode}）。");
                }
                if (await PingAsync())
                {
                    progress?.Report($"语音识别引擎已就绪（{sw.Elapsed.TotalSeconds:F1} 秒）");
                    return true;
                }
                await Task.Delay(300);
            }
            throw new Exception("语音识别引擎启动超时。");
        }

        /// <summary>
        /// 探测 server 是否已就绪。向 /inference 发一个空请求，任何 HTTP 响应都说明服务在监听。
        /// </summary>
        private async Task<bool> PingAsync()
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, InferenceUrl);
                req.Content = new StringContent("");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var resp = await httpClient.SendAsync(req, cts.Token);
                return true; // 有响应即视为存活
            }
            catch
            {
                return false;
            }
        }

        public void Stop()
        {
            try
            {
                if (serverProcess != null && !serverProcess.HasExited)
                {
                    serverProcess.Kill(entireProcessTree: true);
                    serverProcess.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LiveCaption] 停止 server 出错：{ex.Message}");
            }
            finally
            {
                serverProcess?.Dispose();
                serverProcess = null;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stop();
            httpClient.Dispose();
        }
    }
}
