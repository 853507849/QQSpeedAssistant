using OpenCvSharp;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>奖励截图条目。</summary>
public sealed record RewardItem(string Account, string FilePath, string FileName, string ModeName, DateTime CreatedAt);

/// <summary>
/// 奖励截图：按账号分目录保存，方便核对每个账号各自的奖励。
/// </summary>
public sealed class RewardService
{
    private readonly LogService _log;

    /// <summary>构造奖励服务。</summary>
    public RewardService(LogService log)
    {
        _log = log;
    }

    /// <summary>
    /// 抓取当前画面并保存到 User/rewards/{账号}/，返回文件路径；失败返回 null。
    /// </summary>
    public string? Capture(GameSession session, AccountEntry account, string modeName, int round)
    {
        try
        {
            using var frame = session.CaptureFrame();
            if (frame == null || frame.Empty())
            {
                _log.Warn("奖励截图失败：没有取到画面", account.Account);
                return null;
            }

            var directory = Path.Combine(PathService.RewardDir, PathService.SanitizeFileName(account.Account));
            Directory.CreateDirectory(directory);

            var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{PathService.SanitizeFileName(modeName)}_第{round}局.png";
            var path = Path.Combine(directory, fileName);

            // 用 ImEncode + WriteAllBytes 规避中文路径问题
            Cv2.ImEncode(".png", frame, out var buffer);
            File.WriteAllBytes(path, buffer);
            _log.Success($"奖励截图已保存：{fileName}", account.Account);
            return path;
        }
        catch (Exception ex)
        {
            _log.Error($"奖励截图异常：{ex.Message}", account.Account);
            return null;
        }
    }

    /// <summary>
    /// 判定到「永久」奖励时单独归档一张：文件名带「永久」前缀，
    /// 在「奖励截图」页可以直接筛出来，不会和普通结算截图混在一起。
    /// </summary>
    public string? CapturePermanent(GameSession session, AccountEntry account, string modeName, string? tag)
    {
        try
        {
            using var frame = session.CaptureFrame();
            if (frame == null || frame.Empty())
            {
                _log.Warn("永久截图失败：没有取到画面", account.Account);
                return null;
            }

            var directory = Path.Combine(PathService.RewardDir, PathService.SanitizeFileName(account.Account));
            Directory.CreateDirectory(directory);

            var suffix = string.IsNullOrWhiteSpace(tag) ? "" : $"_{PathService.SanitizeFileName(tag)}";
            var fileName = $"永久_{DateTime.Now:yyyyMMdd_HHmmss}_{PathService.SanitizeFileName(modeName)}{suffix}.png";
            var path = Path.Combine(directory, fileName);

            Cv2.ImEncode(".png", frame, out var buffer);
            File.WriteAllBytes(path, buffer);
            _log.Success($"[永久] 已截图归档：{fileName}", account.Account);
            return path;
        }
        catch (Exception ex)
        {
            _log.Error($"永久截图异常：{ex.Message}", account.Account);
            return null;
        }
    }

    /// <summary>列出全部奖励截图，按时间倒序。</summary>
    public IReadOnlyList<RewardItem> LoadAll()
    {
        var items = new List<RewardItem>();
        if (!Directory.Exists(PathService.RewardDir))
        {
            return items;
        }

        foreach (var accountDir in Directory.EnumerateDirectories(PathService.RewardDir))
        {
            var account = Path.GetFileName(accountDir);
            foreach (var file in Directory.EnumerateFiles(accountDir, "*.png"))
            {
                var name = Path.GetFileName(file);
                items.Add(new RewardItem(account, file, name, ParseModeName(name), File.GetCreationTime(file)));
            }
        }

        return items.OrderByDescending(i => i.CreatedAt).ToList();
    }

    /// <summary>从文件名解析玩法名，失败返回空字符串。</summary>
    private static string ParseModeName(string fileName)
    {
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('_');
        // 文件名格式：yyyyMMdd_HHmmss_玩法_第N局
        return parts.Length >= 3 ? parts[2] : "";
    }
}
