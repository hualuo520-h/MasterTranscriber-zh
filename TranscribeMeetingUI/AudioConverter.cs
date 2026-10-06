using NAudio.Wave;
using System;
using System.IO;
using System.Threading.Tasks;

namespace TranscribeMeetingUI
{
    public class AudioConverter
    {
        public static async Task<string> ConvertToWavAsync(string inputFilePath, IProgress<string>? progress = null)
        {
            try
            {
                string extension = Path.GetExtension(inputFilePath).ToLower();
                string outputPath = Path.Combine(
                    Path.GetDirectoryName(inputFilePath) ?? "",
                    Path.GetFileNameWithoutExtension(inputFilePath) + "_converted.wav"
                );

                System.Diagnostics.Debug.WriteLine($"[Converter] Input: {inputFilePath}");
                System.Diagnostics.Debug.WriteLine($"[Converter] Output: {outputPath}");
                System.Diagnostics.Debug.WriteLine($"[Converter] Format: {extension}");

                progress?.Report($"正在将 {extension} 转换为 WAV 格式……");

                // Handle different input formats
                switch (extension)
                {
                    case ".wav":
                        // Already WAV, just copy
                        progress?.Report("文件已是 WAV 格式……");
                        File.Copy(inputFilePath, outputPath, true);
                        break;

                    case ".mp3":
                        await ConvertMp3ToWavAsync(inputFilePath, outputPath, progress);
                        break;

                    case ".mp4":
                    case ".m4a":
                    case ".aac":
                    case ".wma":
                    case ".flac":
                    case ".ogg":
                        // Try using MediaFoundationReader for these formats
                        await ConvertMediaFoundationToWavAsync(inputFilePath, outputPath, progress);
                        break;

                    default:
                        throw new NotSupportedException($"不支持的文件格式 {extension}。支持的格式：MP3、MP4、M4A、WAV、AAC、WMA、FLAC、OGG");
                }

                progress?.Report("转换完成！");
                System.Diagnostics.Debug.WriteLine($"[Converter] Conversion complete: {outputPath}");

                return outputPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Converter] Error: {ex.Message}");
                throw new Exception($"转换音频文件失败：{ex.Message}", ex);
            }
        }

        private static async Task ConvertMp3ToWavAsync(string inputPath, string outputPath, IProgress<string>? progress = null)
        {
            await Task.Run(() =>
            {
                using (var reader = new Mp3FileReader(inputPath))
                {
                    var fileInfo = new FileInfo(inputPath);
                    progress?.Report($"正在转换 MP3（{fileInfo.Length / (1024.0 * 1024.0):F2} MB）……");

                    // Convert to 16-bit PCM WAV
                    var waveFormat = new WaveFormat(16000, 16, 1); // 16kHz mono for efficiency

                    using (var resampler = new MediaFoundationResampler(reader, waveFormat))
                    {
                        WaveFileWriter.CreateWaveFile(outputPath, resampler);
                    }
                }
            });
        }

        private static async Task ConvertMediaFoundationToWavAsync(string inputPath, string outputPath, IProgress<string>? progress = null)
        {
            await Task.Run(() =>
            {
                using (var reader = new MediaFoundationReader(inputPath))
                {
                    var fileInfo = new FileInfo(inputPath);
                    string extension = Path.GetExtension(inputPath).ToUpper();
                    progress?.Report($"正在转换 {extension}（{fileInfo.Length / (1024.0 * 1024.0):F2} MB）……");

                    // Convert to 16-bit PCM WAV
                    var waveFormat = new WaveFormat(16000, 16, 1); // 16kHz mono

                    using (var resampler = new MediaFoundationResampler(reader, waveFormat))
                    {
                        WaveFileWriter.CreateWaveFile(outputPath, resampler);
                    }
                }
            });
        }

        public static bool IsVideoFile(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLower();
            return extension == ".mp4" || extension == ".avi" || extension == ".mov" ||
                   extension == ".mkv" || extension == ".wmv" || extension == ".flv";
        }

        public static bool IsSupportedFormat(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLower();
            var supportedFormats = new[] { ".wav", ".mp3", ".mp4", ".m4a", ".aac", ".wma", ".flac", ".ogg", ".avi", ".mov", ".mkv", ".wmv", ".flv" };
            return Array.Exists(supportedFormats, ext => ext == extension);
        }

        public static string GetFormatDescription(string filePath)
        {
            if (IsVideoFile(filePath))
                return "视频文件（将提取音频）";

            string extension = Path.GetExtension(filePath).ToUpper().TrimStart('.');
            return $"{extension} 音频文件";
        }
    }
}