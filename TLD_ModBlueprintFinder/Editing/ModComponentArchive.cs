using System.IO.Compression;
using System.Text;

namespace TLD_ModBlueprintFinder.Editing;

/// <summary>
/// 容器里的一个条目（文件或目录）。目录条目只在列表里出现，不参与编辑。
/// </summary>
public sealed record ArchiveEntry(string Path, long SizeBytes, DateTimeOffset? LastWriteTime, bool IsDirectory)
{
    public bool IsJson => !IsDirectory && Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    public string FileName
    {
        get
        {
            var slash = Path.LastIndexOf('/');
            return slash < 0 ? Path : Path[(slash + 1)..];
        }
    }

    public string SizeText => IsDirectory ? "" : FormatSize(SizeBytes);

    /// <summary>列表里只显示「月-日 时:分」：年份对 mod 文件没有意义，宽度留给路径。</summary>
    public string TimeText => LastWriteTime is null
        ? ""
        : LastWriteTime.Value.LocalDateTime.ToString("MM-dd HH:mm");

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.##} MB"
    };
}

/// <summary>读出来的条目内容。二进制条目（含 NUL 字节）只展示不写回。</summary>
public sealed record EntryContent(string Text, bool IsBinary, long SizeBytes);

/// <summary>
/// modcomponent 容器内的条目读写 —— 增、删、改、查四件事的唯一入口。
///
/// .modcomponent 是 ZIP，改一份文件不能就地打补丁，只能整体重写：
/// 读原包 → 逐条复制到临时文件（目标条目换内容 / 跳过 / 改名）→ 用临时文件替换原包。
/// 这样**任何时候原包都是完整的**：中途失败只会留下一个临时文件，
/// 不会出现「改了一半、连游戏都读不了」的包。
///
/// 解压目录形式的 mod 直接走文件系统，代码路径与 ZIP 分开但对外行为一致。
/// </summary>
public static class ModComponentArchive
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // ────────────────────────────────────────────────────────────────────
    // 查
    // ────────────────────────────────────────────────────────────────────

    /// <summary>列出容器内全部条目（含目录），按路径排序。</summary>
    public static IReadOnlyList<ArchiveEntry> ListEntries(string containerPath)
    {
        var list = new List<ArchiveEntry>();

        if (Directory.Exists(containerPath))
        {
            var root = Path.GetFullPath(containerPath);

            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                var rel = ToRelative(root, dir);
                list.Add(new ArchiveEntry(rel, 0, Directory.GetLastWriteTime(dir), true));
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = ToRelative(root, file);
                var info = new FileInfo(file);
                list.Add(new ArchiveEntry(rel, info.Length, info.LastWriteTime, false));
            }
        }
        else if (File.Exists(containerPath))
        {
            using var archive = ZipFile.OpenRead(containerPath);

            foreach (var entry in archive.Entries)
            {
                var name = Normalize(entry.FullName);
                var isDirectory = IsDirectoryEntry(entry);
                list.Add(new ArchiveEntry(
                    isDirectory ? name.TrimEnd('/') : name,
                    isDirectory ? 0 : entry.Length,
                    entry.LastWriteTime,
                    isDirectory));
            }
        }
        else
        {
            throw new FileNotFoundException($"找不到 modcomponent：{containerPath}", containerPath);
        }

        return list
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool Exists(string containerPath, string entryPath)
    {
        var normalized = Normalize(entryPath);
        if (normalized.Length == 0) return false;

        if (Directory.Exists(containerPath))
            return File.Exists(CombineInFolder(containerPath, normalized));

        using var archive = ZipFile.OpenRead(containerPath);
        return FindEntry(archive, normalized) is not null;
    }

    /// <summary>读条目内容；条目不存在返回 null。</summary>
    public static EntryContent? ReadContent(string containerPath, string entryPath)
    {
        var normalized = Normalize(entryPath);
        if (normalized.Length == 0) return null;

        var bytes = ReadBytes(containerPath, normalized);
        if (bytes is null) return null;

        // 含 NUL 就按二进制处理：mod 里有 bundle、字体、贴图之类，
        // 用文本编辑器硬开只会得到乱码，写回去更是灾难。
        var isBinary = Array.IndexOf(bytes, (byte)0) >= 0;

        return new EntryContent(isBinary ? "" : Decode(bytes), isBinary, bytes.Length);
    }

    public static byte[]? ReadBytes(string containerPath, string entryPath)
    {
        var normalized = Normalize(entryPath);
        if (normalized.Length == 0) return null;

        if (Directory.Exists(containerPath))
        {
            var path = CombineInFolder(containerPath, normalized);
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

    // ────────────────────────────────────────────────────────────────────
    // 改 / 增
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 写入一个条目：已存在就替换，不存在就新建（父目录条目会一并补上）。
    /// 原条目带 BOM 就保留 BOM —— 改文本不该顺手改掉原作者的编码习惯。
    /// </summary>
    public static void WriteText(string containerPath, string entryPath, string text)
    {
        var normalized = Normalize(entryPath);
        if (normalized.Length == 0)
            throw new ArgumentException("没有指定要写入的条目。", nameof(entryPath));

        var bytes = Encode(text, ReadBytes(containerPath, normalized) is { } origin && HasBom(origin));

        if (Directory.Exists(containerPath))
        {
            var path = CombineInFolder(containerPath, normalized);
            var dir = Path.GetDirectoryName(path);

            if (dir is not null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
        }
        else if (File.Exists(containerPath))
        {
            RewriteZip(containerPath, new ZipEdit { Upsert = (normalized, bytes) });
        }
        else
        {
            throw new FileNotFoundException($"找不到 modcomponent：{containerPath}", containerPath);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // 删
    // ────────────────────────────────────────────────────────────────────

    public static void Delete(string containerPath, string entryPath)
    {
        var normalized = Normalize(entryPath);
        if (normalized.Length == 0)
            throw new ArgumentException("没有指定要删除的条目。", nameof(entryPath));

        if (Directory.Exists(containerPath))
        {
            var path = CombineInFolder(containerPath, normalized);
            if (!File.Exists(path))
                throw new InvalidOperationException($"这个 modcomponent 里找不到 {normalized}。");

            File.Delete(path);
            RemoveEmptyParents(containerPath, Path.GetDirectoryName(path));
        }
        else if (File.Exists(containerPath))
        {
            RewriteZip(containerPath, new ZipEdit { Remove = normalized });
        }
        else
        {
            throw new FileNotFoundException($"找不到 modcomponent：{containerPath}", containerPath);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // 重命名
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 改条目路径。内容原样搬运（连 BOM 一起），不做重新编码。
    /// </summary>
    public static void Rename(string containerPath, string fromPath, string toPath)
    {
        var from = Normalize(fromPath);
        var to = Normalize(toPath);

        if (from.Length == 0 || to.Length == 0)
            throw new ArgumentException("源路径和目标路径都不能为空。");

        if (NameEquals(from, to))
            throw new InvalidOperationException("新路径与原路径相同。");

        if (Directory.Exists(containerPath))
        {
            var source = CombineInFolder(containerPath, from);
            var target = CombineInFolder(containerPath, to);

            if (!File.Exists(source))
                throw new InvalidOperationException($"这个 modcomponent 里找不到 {from}。");
            if (File.Exists(target))
                throw new InvalidOperationException($"目标路径已存在：{to}");

            var dir = Path.GetDirectoryName(target);
            if (dir is not null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.Move(source, target);
            RemoveEmptyParents(containerPath, Path.GetDirectoryName(source));
        }
        else if (File.Exists(containerPath))
        {
            RewriteZip(containerPath, new ZipEdit { Rename = (from, to) });
        }
        else
        {
            throw new FileNotFoundException($"找不到 modcomponent：{containerPath}", containerPath);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // 路径工具
    // ────────────────────────────────────────────────────────────────────

    /// <summary>统一成容器内部路径：正斜杠、无前导斜杠。</summary>
    public static string Normalize(string? entryPath) =>
        (entryPath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

    /// <summary>
    /// 校验一个「准备新建 / 改名」的条目路径。返回 null 表示合法。
    /// 只做格式校验，是否重名由调用方查 <see cref="Exists"/>。
    /// </summary>
    public static string? ValidateEntryPath(string? entryPath)
    {
        var normalized = Normalize(entryPath);

        if (normalized.Length == 0) return "条目路径不能为空。";

        if (normalized.Contains(".."))
            return "路径里不能出现「..」。";

        if (normalized.Contains(':'))
            return "路径里不能出现盘符或冒号。";

        foreach (var bad in new[] { '*', '?', '"', '<', '>', '|' })
        {
            if (normalized.Contains(bad))
                return $"路径里不能出现「{bad}」。";
        }

        if (!normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return "这个编辑器只处理 .json 文件，路径请以 .json 结尾。";

        if (normalized.Length > 240)
            return "路径太长了。";

        return null;
    }

    // ────────────────────────────────────────────────────────────────────
    // ZIP 重写内核
    // ────────────────────────────────────────────────────────────────────

    /// <summary>一次重写要做的改动。三个字段互斥，同一时间只填一个。</summary>
    private sealed class ZipEdit
    {
        public string? Remove { get; init; }
        public (string Path, byte[] Bytes)? Upsert { get; init; }
        public (string From, string To)? Rename { get; init; }

        /// <summary>是否真的命中了目标条目（用于发现「改错包」）。</summary>
        public bool Removed { get; set; }
        public bool Upserted { get; set; }
        public bool Renamed { get; set; }
    }

    private static void RewriteZip(string zipPath, ZipEdit edit)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath)) ?? AppContext.BaseDirectory;
        var temp = Path.Combine(dir, $"{Path.GetFileName(zipPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var source = ZipFile.OpenRead(zipPath))
            using (var target = new ZipArchive(File.Create(temp), ZipArchiveMode.Create))
            {
                // 记下已有的目录条目，新建文件时才知道还缺哪几层
                var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in source.Entries)
                {
                    var name = Normalize(entry.FullName);

                    if (IsDirectoryEntry(entry))
                    {
                        folders.Add(name);
                        CopyEntry(target, entry);
                        continue;
                    }

                    if (edit.Remove is not null && NameEquals(name, edit.Remove))
                    {
                        edit.Removed = true;
                        continue;
                    }

                    if (edit.Upsert is { } upsert && NameEquals(name, upsert.Path))
                    {
                        WriteEntry(target, entry.FullName, entry.LastWriteTime, upsert.Bytes);
                        edit.Upserted = true;
                        continue;
                    }

                    if (edit.Rename is { } rename && NameEquals(name, rename.From))
                    {
                        WriteEntry(target, rename.To, entry.LastWriteTime, ReadAll(entry));
                        edit.Renamed = true;
                        continue;
                    }

                    CopyEntry(target, entry);
                }

                if (edit.Upsert is { } addition && !edit.Upserted)
                {
                    EnsureParentFolders(target, folders, addition.Path);
                    WriteEntry(target, addition.Path, DateTimeOffset.Now, addition.Bytes);
                }
            }

            if (edit.Remove is not null && !edit.Removed)
                throw new InvalidOperationException($"这个 modcomponent 里找不到 {edit.Remove}，可能已被其它程序改过。");

            if (edit.Rename is not null && !edit.Renamed)
                throw new InvalidOperationException($"这个 modcomponent 里找不到 {edit.Rename.Value.From}，可能已被其它程序改过。");

            // 同一卷内 Move 是重命名，原包在被替换前一直是完整的
            File.Move(temp, zipPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// 新建深层条目时补上缺失的目录条目。
    /// ZIP 本身不要求目录条目，但不少解包工具（含 ModComponent 自己的解压流程）
    /// 依赖它来还原空目录，缺了会让 mod 的结构看着怪。
    /// </summary>
    private static void EnsureParentFolders(ZipArchive target, HashSet<string> existing, string entryPath)
    {
        var parts = entryPath.Split('/');
        var prefix = "";

        for (var i = 0; i < parts.Length - 1; i++)
        {
            prefix += parts[i] + "/";
            if (existing.Add(prefix))
                target.CreateEntry(prefix);
        }
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        return memory.ToArray();
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

    // ────────────────────────────────────────────────────────────────────
    // 小工具
    // ────────────────────────────────────────────────────────────────────

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
        entry.FullName.EndsWith("\\", StringComparison.Ordinal);

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string entryPath)
    {
        foreach (var entry in archive.Entries)
        {
            if (IsDirectoryEntry(entry)) continue;
            if (NameEquals(Normalize(entry.FullName), entryPath)) return entry;
        }

        return null;
    }

    private static bool NameEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string CombineInFolder(string folder, string entryPath) =>
        Path.Combine(folder, entryPath.Replace('/', Path.DirectorySeparatorChar));

    private static string ToRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>删掉条目后收掉变空的目录（只在解压目录形式下有意义）。</summary>
    private static void RemoveEmptyParents(string root, string? dir)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

            while (!string.IsNullOrEmpty(dir))
            {
                var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
                if (full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return;
                if (!Directory.Exists(full)) return;
                if (Directory.EnumerateFileSystemEntries(full).Any()) return;

                Directory.Delete(full);
                dir = Path.GetDirectoryName(full);
            }
        }
        catch
        {
            // 收不掉空目录不影响结果
        }
    }

    private static bool HasBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static string Decode(byte[] bytes)
    {
        if (HasBom(bytes))
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] Encode(string text, bool withBom)
    {
        var body = Utf8NoBom.GetBytes(text);
        if (!withBom) return body;

        var result = new byte[body.Length + 3];
        result[0] = 0xEF;
        result[1] = 0xBB;
        result[2] = 0xBF;
        body.CopyTo(result, 3);
        return result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 临时文件删不掉不影响结果
        }
    }
}
