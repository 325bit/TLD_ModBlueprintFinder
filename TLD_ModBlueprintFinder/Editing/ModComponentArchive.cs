using System.IO.Compression;
using System.Text;

namespace TLD_ModBlueprintFinder.Editing;

/// <summary>
/// One entry in a container (file or directory). Directory entries are listed but never edited.
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

    /// <summary>Shows only "month-day hour:minute": the year is noise for mod files, the width belongs to the path.</summary>
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

/// <summary>Content read back from an entry. Binary entries (containing NUL) are shown, never written.</summary>
public sealed record EntryContent(string Text, bool IsBinary, long SizeBytes);

/// <summary>
/// The only way in or out for entry reads and writes - list, read, create, update, delete, rename.
/// A .modcomponent is a ZIP, so one file cannot be patched in place: the archive is rewritten (copy
/// every entry to a temp file, swapping / dropping / renaming the target, then replace). The original is
/// therefore complete at every moment: a failure leaves a temp file, never a half-written container the
/// game cannot load. Extracted-folder mods take the file-system path with the same external behavior.
/// </summary>
public static class ModComponentArchive
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Lookup

    /// <summary>Lists every entry in the container (directories included), sorted by path.</summary>
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

    /// <summary>Reads entry content; null when the entry does not exist.</summary>
    public static EntryContent? ReadContent(string containerPath, string entryPath)
    {
        var normalized = Normalize(entryPath);
        if (normalized.Length == 0) return null;

        var bytes = ReadBytes(containerPath, normalized);
        if (bytes is null) return null;

        // A NUL byte means binary: containers hold bundles, fonts and textures, and forcing them through a
        // text editor yields mojibake and corrupts them on write.
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

    // Save and create

    /// <summary>
    /// Writes an entry: replaces it when present, creates it (with parent folders) when absent.
    /// An existing BOM is preserved - editing text should not change the author's encoding.
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

    // Delete

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

    // Rename

    /// <summary>
    /// Moves an entry to another path. Content is copied as-is (BOM included), never re-encoded.
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

    // Path helpers

    /// <summary>Normalizes to an in-container path: forward slashes, no leading slash.</summary>
    public static string Normalize(string? entryPath) =>
        (entryPath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

    /// <summary>
    /// Validates a path about to be created or renamed. Returns null when acceptable.
    /// Format only; duplicate names are the caller's job via <see cref="Exists"/>.
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

    // ZIP rewrite core

    /// <summary>The single change one rewrite applies. Exactly one field is set.</summary>
    private sealed class ZipEdit
    {
        public string? Remove { get; init; }
        public (string Path, byte[] Bytes)? Upsert { get; init; }
        public (string From, string To)? Rename { get; init; }

        /// <summary>Whether the target entry was actually found (catches edits to the wrong archive).</summary>
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
                // Record existing directory entries, so a new file knows which folders are missing
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

            // A move within one volume is a rename, so the original stays intact until it is replaced
            File.Move(temp, zipPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Adds the directory entries a new nested file implies.
    /// ZIP does not require them, but many unpackers (including ModComponent's own) rely on them to restore
    /// empty folders; without them the mod's structure looks broken.
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

    // Small helpers

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

    /// <summary>Removes directories that became empty after a delete (extracted-folder containers only).</summary>
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
            // Failing to drop an empty folder does not affect the result
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
            // A leftover temp file does not affect the result
        }
    }
}
