using System.IO.Compression;

namespace TLD_ModBlueprintFinder.Editing;

/// <summary>
/// modcomponent 备份。
///
/// 备份统一落在 <c>&lt;程序目录&gt;\TLDMBF_modcomponent_backup\</c>，
/// 文件名为 <c>&lt;mod名&gt;.modcomponent-&lt;yyyyMMdd_HHmmss&gt;</c>：
/// 以后缀而不是目录来区分时间，是为了让同一个 mod 的历次备份在资源管理器里
/// 紧挨着排在一起，一眼能看出改了几次。
///
/// 只在用户明确点击「备份」或「保存修改」（且勾了自动备份）时才会复制，
/// 其余路径对 Mods 目录零写入。
/// </summary>
public static class ModBackup
{
    /// <summary>备份目录名，与程序同级的固定名字。</summary>
    public const string FolderName = "TLDMBF_modcomponent_backup";

    /// <summary>备份根目录（程序所在目录下）。</summary>
    public static string RootDirectory => Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>
    /// 备份一个容器（.modcomponent / .zip 文件，或解压目录形式的 mod）。
    /// 返回备份出来的路径。
    /// </summary>
    public static string Create(string containerPath, DateTime? when = null)
    {
        if (string.IsNullOrWhiteSpace(containerPath))
            throw new ArgumentException("没有指定要备份的 modcomponent。", nameof(containerPath));

        var isFile = File.Exists(containerPath);
        var isFolder = Directory.Exists(containerPath);

        if (!isFile && !isFolder)
            throw new FileNotFoundException($"找不到 modcomponent：{containerPath}", containerPath);

        Directory.CreateDirectory(RootDirectory);

        var target = BuildTargetPath(containerPath, isFolder, when ?? DateTime.Now);

        if (isFolder)
            CopyDirectory(containerPath, target);
        else
            File.Copy(containerPath, target, overwrite: false);

        return target;
    }

    /// <summary>
    /// 生成备份路径。同名同秒重复备份时追加 -2、-3，绝不覆盖已有备份。
    /// </summary>
    private static string BuildTargetPath(string containerPath, bool isFolder, DateTime when)
    {
        var name = isFolder
            ? new DirectoryInfo(containerPath).Name
            : Path.GetFileNameWithoutExtension(containerPath);

        if (name.Length == 0) name = "modcomponent";

        var stamp = when.ToString("yyyyMMdd_HHmmss");

        // .modcomponent 源文件直接接时间戳；其它扩展名（少数 mod 用 .zip）保留扩展名，
        // 免得备份出来分不清是什么格式。
        var extension = isFolder ? "" : Path.GetExtension(containerPath);
        if (extension.Equals(".modcomponent", StringComparison.OrdinalIgnoreCase)) extension = "";

        var baseName = $"{name}.modcomponent-{stamp}{extension}";
        var target = Path.Combine(RootDirectory, baseName);

        for (var i = 2; File.Exists(target) || Directory.Exists(target); i++)
            target = Path.Combine(RootDirectory, $"{baseName}-{i}");

        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);

        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}

/// <summary>
/// 往 modcomponent 里写回一个 JSON 条目。
///
/// .modcomponent 是 ZIP，改其中一份文件不能就地打补丁，只能整体重写：
/// 读原包 → 逐条复制到临时文件（目标条目换成新内容）→ 用临时文件替换原包。
/// 这样做的好处是**任何时候原包都是完整的**：中途失败只会留下一个临时文件，
/// 不会出现「改了一半、连游戏都读不了」的包。
/// </summary>
public static class ModComponentWriter
{
    /// <summary>
    /// 把 <paramref name="text"/> 写进容器的指定条目。
    /// 文件型容器走 ZIP 重写，目录型容器直接写文件。
    /// </summary>
    public static void SaveEntry(string containerPath, string entryPath, string text)
    {
        if (string.IsNullOrWhiteSpace(containerPath))
            throw new ArgumentException("没有指定 modcomponent。", nameof(containerPath));

        var normalized = NormalizeEntryPath(entryPath);
        if (normalized.Length == 0)
            throw new ArgumentException("没有指定要保存的条目。", nameof(entryPath));

        var bytes = Encode(text, OriginalHasBom(containerPath, normalized));

        if (Directory.Exists(containerPath))
            SaveToFolder(containerPath, normalized, bytes);
        else if (File.Exists(containerPath))
            SaveToZip(containerPath, normalized, bytes);
        else
            throw new FileNotFoundException($"找不到 modcomponent：{containerPath}", containerPath);
    }

    /// <summary>读出条目的原始字节，用于判断要不要保留 BOM。</summary>
    public static byte[]? ReadEntryBytes(string containerPath, string entryPath)
    {
        var normalized = NormalizeEntryPath(entryPath);

        if (Directory.Exists(containerPath))
        {
            var path = Path.Combine(containerPath, normalized.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        using var archive = ZipFile.OpenRead(containerPath);
        var entry = FindEntry(archive, normalized);
        if (entry is null) return null;

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static bool OriginalHasBom(string containerPath, string entryPath)
    {
        try
        {
            var bytes = ReadEntryBytes(containerPath, entryPath);
            return bytes is { Length: >= 3 } &&
                   bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }
        catch
        {
            // 读不出来就按无 BOM 处理：本来也只是保留原作者习惯的细节
            return false;
        }
    }

    private static byte[] Encode(string text, bool withBom)
    {
        var body = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        if (!withBom) return body;

        var result = new byte[body.Length + 3];
        result[0] = 0xEF;
        result[1] = 0xBB;
        result[2] = 0xBF;
        body.CopyTo(result, 3);
        return result;
    }

    private static void SaveToFolder(string folder, string entryPath, byte[] bytes)
    {
        var path = Path.Combine(folder, entryPath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(path);

        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(path, bytes);
    }

    private static void SaveToZip(string zipPath, string entryPath, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath)) ?? AppContext.BaseDirectory;
        var temp = Path.Combine(dir, $"{Path.GetFileName(zipPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var source = ZipFile.OpenRead(zipPath))
            using (var target = new ZipArchive(File.Create(temp), ZipArchiveMode.Create))
            {
                var replaced = false;

                foreach (var entry in source.Entries)
                {
                    if (!replaced && IsTarget(entry, entryPath))
                    {
                        WriteEntry(target, entry.FullName, entry.LastWriteTime, bytes);
                        replaced = true;
                        continue;
                    }

                    CopyEntry(target, entry);
                }

                if (!replaced)
                    throw new InvalidOperationException($"这个 modcomponent 里找不到 {entryPath}，可能已被其它程序改过。");
            }

            // 同一卷内 Move 是重命名，原包在被替换前一直是完整的
            File.Move(temp, zipPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void WriteEntry(ZipArchive target, string name, DateTimeOffset lastWrite, byte[] bytes)
    {
        var entry = target.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = lastWrite;

        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void CopyEntry(ZipArchive target, ZipArchiveEntry source)
    {
        var copy = target.CreateEntry(source.FullName, CompressionLevel.Optimal);
        copy.LastWriteTime = source.LastWriteTime;

        if (IsDirectoryEntry(source)) return;

        using var input = source.Open();
        using var output = copy.Open();
        input.CopyTo(output);
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal);

    private static bool IsTarget(ZipArchiveEntry entry, string entryPath) =>
        !IsDirectoryEntry(entry) && NormalizeEntryPath(entry.FullName).Equals(entryPath, StringComparison.OrdinalIgnoreCase);

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string entryPath)
    {
        foreach (var entry in archive.Entries)
        {
            if (IsTarget(entry, entryPath)) return entry;
        }

        return null;
    }

    private static string NormalizeEntryPath(string? entryPath) =>
        (entryPath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 临时文件删不掉不影响结果，下次覆盖写会重用它
        }
    }
}
