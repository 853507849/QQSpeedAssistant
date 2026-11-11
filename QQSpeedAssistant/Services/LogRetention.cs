namespace QQSpeedAssistant.Services;

/// <summary>
/// 排查产物（AI 调用全文、请求图片、运行日志、调试截图）的定期清理。
/// 这些东西不清理会一直堆着，其中图片最占空间：一次验证码就是 7 张 jpg，
/// 一天跑下来几十 MB，几个月就是几个 G。
///
/// 清理口径统一按「保留最近 N 天」，N 来自设置页，0 表示永不清理：
/// ① User/log/ai/yyyy-MM-dd/  整个日期目录删掉（正文与 media 一起走）；
/// ② User/log/run-yyyyMMdd.log 按文件名里的日期判断；
/// ③ User/debug/*              调试截图按最后写入时间判断。
/// </summary>
public static class LogRetention
{
    private static readonly Lock Gate = new();

    private static DateTime _lastRun = DateTime.MinValue;

    /// <summary>当前保留天数。0 或负数表示不自动清理。</summary>
    public static int RetainDays { get; private set; } = 7;

    /// <summary>最近一次清理的结果，设置页用来告诉用户释放了多少空间。</summary>
    public static Result LastResult { get; private set; } = new(0, 0, 0, DateTime.MinValue);

    /// <summary>一次清理的结果。</summary>
    /// <param name="Days">删掉的日期目录数（AI 日志一天一个目录）。</param>
    /// <param name="Files">删掉的零散文件数（运行日志、调试截图）。</param>
    /// <param name="Bytes">释放的字节数。</param>
    /// <param name="At">清理时间。</param>
    public sealed record Result(int Days, int Files, long Bytes, DateTime At)
    {
        /// <summary>这次什么都没删。</summary>
        public bool IsEmpty => Days == 0 && Files == 0;

        /// <summary>释放的空间（MB），保留一位小数。</summary>
        public double Megabytes => Bytes / 1024.0 / 1024.0;
    }

    /// <summary>
    /// 应用保留天数。天数变了立刻清一次；没变就每天只清一次，
    /// 免得设置页每改一个字段都去扫一遍目录。
    /// </summary>
    public static void Apply(int retainDays)
    {
        var days = Math.Max(0, retainDays);
        var changed = days != RetainDays;
        RetainDays = days;

        if (changed || _lastRun.Date != DateTime.Today)
        {
            Run();
        }
    }

    /// <summary>程序跨天常驻时，由写日志的地方顺带触发，保证不会一整天都不清理。</summary>
    public static void PurgeIfNewDay()
    {
        if (_lastRun.Date != DateTime.Today)
        {
            Run();
        }
    }

    /// <summary>真正执行一次清理。失败只影响清理本身，不抛给调用方。</summary>
    public static Result Run()
    {
        lock (Gate)
        {
            if (RetainDays <= 0)
            {
                _lastRun = DateTime.Today;
                LastResult = new Result(0, 0, 0, DateTime.Now);
                return LastResult;
            }

            // 保留最近 RetainDays 天（含今天）：天数 7、今天 11-11 时，11-05 及以后保留。
            var cutoff = DateTime.Today.AddDays(1 - RetainDays);

            var days = 0;
            long bytes = 0;

            // ① AI 日志：一天一个目录，整目录删。
            try
            {
                if (Directory.Exists(AiRequestLogger.LogDir))
                {
                    foreach (var dir in Directory.EnumerateDirectories(AiRequestLogger.LogDir))
                    {
                        var name = Path.GetFileName(dir);
                        if (!AiRequestLogger.TryParseDay(name, AiRequestLogger.DayFormat, out var day) || day >= cutoff)
                        {
                            continue;
                        }

                        var size = DirectorySize(dir);
                        if (TryDeleteDirectory(dir))
                        {
                            days++;
                            bytes += size;
                        }
                    }
                }
            }
            catch
            {
                // 清理失败与业务无关。
            }

            // ② 运行日志：文件名里带日期，按日期判断。
            var files = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(PathService.LogDir, "run-*.log"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Length <= 4
                        || !AiRequestLogger.TryParseDay(name[4..], "yyyyMMdd", out var day)
                        || day >= cutoff)
                    {
                        continue;
                    }

                    var size = FileSize(file);
                    if (TryDeleteFile(file))
                    {
                        files++;
                        bytes += size;
                    }
                }
            }
            catch
            {
                // 同上。
            }

            // ③ 调试截图：文件名不带日期，只能看最后写入时间。
            try
            {
                if (Directory.Exists(PathService.DebugDir))
                {
                    foreach (var file in Directory.EnumerateFiles(PathService.DebugDir))
                    {
                        if (File.GetLastWriteTime(file) >= cutoff)
                        {
                            continue;
                        }

                        var size = FileSize(file);
                        if (TryDeleteFile(file))
                        {
                            files++;
                            bytes += size;
                        }
                    }
                }
            }
            catch
            {
                // 同上。
            }

            _lastRun = DateTime.Today;
            LastResult = new Result(days, files, bytes, DateTime.Now);
            return LastResult;
        }
    }

    /// <summary>目录总大小；读不到的项按 0 计。</summary>
    private static long DirectorySize(string dir)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                total += FileSize(file);
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>单文件大小；文件刚好被删掉时返回 0。</summary>
    private static long FileSize(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>删目录。有文件仍被占用时会失败，留到下次再删。</summary>
    private static bool TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>删文件。</summary>
    private static bool TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
