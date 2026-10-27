using System.IO.Compression;

namespace ClashAcry.Core;

/// <summary>
/// release zip 的读取。<b>目标路径一律由调用方给定</b>，绝不用条自名拼路径——包内条目名是源站数据，
/// 拿它当路径就是路径穿越（真实 mihomo 包里 exe 的名字带资产后缀，也不是 <c>mihomo.exe</c>，
/// "取首个 .exe 条目"因此是唯一可靠的定位方式）。
/// </summary>
public static class ZipExtractor
{
    /// <summary>缓存命中判据的一半：包**能打开且有 .exe 条目**。坏包/半截包在这里就返回 false，交给调用方回落到重新下载。</summary>
    public static bool HasExecutable(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Any(e => IsExe(e.FullName));
        }
        // InvalidDataException（签名不对/被截断）与 IO 类失败都算"这块缓存不可用"，不算异常。
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                     or NotSupportedException or ArgumentException)
        { return false; }
    }

    /// <summary>把首个 .exe 条目流式写到 <paramref name="destFile"/>（覆盖）。没有 exe 条目返回 false 且不碰目标。</summary>
    public static bool TryExtractFirstExe(string zipPath, string destFile)
    {
        ZipArchiveEntry? entry;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            entry = zip.Entries.FirstOrDefault(e => IsExe(e.FullName));
            if (entry is null) return false;
            // 条目在这里只取一次流并拷进调用方给的目标，zip 关闭前完成写入。
            using var src = entry.Open();
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            using var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None);
            src.CopyTo(dst);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                     or NotSupportedException or ArgumentException)
        { return false; }
        return true;
    }

    private static bool IsExe(string entryName) =>
        Path.GetExtension(entryName).Equals(".exe", StringComparison.OrdinalIgnoreCase);
}
