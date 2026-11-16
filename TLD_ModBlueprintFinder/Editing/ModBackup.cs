using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TLD_ModBlueprintFinder.Editing;

/// <summary>
/// modcomponent backups. Manual "Back up" copies on every click; auto-backup copies the untouched
/// original of a container exactly once and reuses that copy for all later saves. The baseline is
/// recorded in &lt;program dir&gt;\TLDMBF_modcomponent_backup\_backup_state.json, so it survives restarts
/// and is shared by both editors. A container changed outside the tool gets a fresh baseline.
/// </summary>
public static class ModBackup
{
    /// <summary>Backup folder name, fixed, next to the executable.</summary>
    public const string FolderName = "TLDMBF_modcomponent_backup";

    /// <summary>Baseline record inside the backup folder. Not a modcomponent copy; never restored.</summary>
    private const string StateFileName = "_backup_state.json";

    /// <summary>Backup root (inside the program directory).</summary>
    public static string RootDirectory => Path.Combine(AppContext.BaseDirectory, FolderName);

    private static string StateFile => Path.Combine(RootDirectory, StateFileName);

    /// <summary>
    /// Ensures one pristine backup exists for the container and returns its path. Null when auto-backup is
    /// off. Later saves return the same path without copying an intermediate state. See §7.1.
    /// </summary>
    public static string? EnsureOriginal(string containerPath, bool enabled)
    {
        if (!enabled) return null;
        if (string.IsNullOrWhiteSpace(containerPath))
            throw new ArgumentException("没有指定要备份的 modcomponent。", nameof(containerPath));

        var state = LoadState();
        var fingerprint = Fingerprint(containerPath);
        var key = Key(containerPath);

        var known = state.Containers.TryGetValue(key, out var entry);

        if (known
            && BaselineExists(entry!.Backup)
            && string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Path.Combine(RootDirectory, entry.Backup);
        }

        // Backups left by the old "copy on every save" behaviour are adopted oldest-first, but only for a
        // container the record has never seen: after a baseline was deleted, or after the mod was replaced
        // outside the tool, the current container is a new original and needs its own copy.
        var baseline = (fingerprint.Length > 0 && !known ? OldestExistingBackup(containerPath) : null)
                       ?? Path.GetFileName(Create(containerPath));

        state.Containers[key] = new Baseline { Backup = baseline, Fingerprint = fingerprint };
        SaveState(state);
        return Path.Combine(RootDirectory, baseline);
    }

    /// <summary>Records the post-write fingerprint. Without it the next save looks like an external edit.</summary>
    public static void NoteWritten(string containerPath)
    {
        // Bookkeeping must never turn a completed write into a reported failure.
        try
        {
            var state = LoadState();
            var key = Key(containerPath);
            if (!state.Containers.TryGetValue(key, out var entry)) return;

            var fingerprint = Fingerprint(containerPath);
            if (fingerprint.Length == 0) return;

            entry.Fingerprint = fingerprint;
            SaveState(state);
        }
        catch
        {
            // Ignored: the worst case is one extra baseline copy on the next run.
        }
    }

    /// <summary>
    /// Backs up one container (.modcomponent/.zip file, or an extracted-folder mod) and returns its path.
    /// Manual operation: always makes a fresh copy, independent of the auto-backup baseline.
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
    /// Builds the backup path. A second backup in the same second gets -2, -3; an existing backup is never
    /// overwritten.
    /// </summary>
    private static string BuildTargetPath(string containerPath, bool isFolder, DateTime when)
    {
        var name = BaseName(containerPath, isFolder);
        if (name.Length == 0) name = "modcomponent";

        var stamp = when.ToString("yyyyMMdd_HHmmss");

        // A .modcomponent drops its extension; other extensions (a few mods ship .zip) are kept so the
        // backup's format stays obvious.
        var extension = isFolder ? "" : Path.GetExtension(containerPath);
        if (extension.Equals(".modcomponent", StringComparison.OrdinalIgnoreCase)) extension = "";

        var baseName = $"{name}.modcomponent-{stamp}{extension}";
        var target = Path.Combine(RootDirectory, baseName);

        for (var i = 2; File.Exists(target) || Directory.Exists(target); i++)
            target = Path.Combine(RootDirectory, $"{baseName}-{i}");

        return target;
    }

    private static string BaseName(string containerPath, bool isFolder) => isFolder
        ? new DirectoryInfo(containerPath).Name
        : Path.GetFileNameWithoutExtension(containerPath);

    /// <summary>Oldest existing copy of this container, or null. Matches file and folder backups alike.</summary>
    private static string? OldestExistingBackup(string containerPath)
    {
        if (!Directory.Exists(RootDirectory)) return null;

        var prefix = BaseName(containerPath, Directory.Exists(containerPath)) + ".modcomponent-";
        var found = new List<string>();

        foreach (var path in Directory.EnumerateFileSystemEntries(RootDirectory))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) found.Add(name);
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found.Count > 0 ? found[0] : null;
    }

    private static bool BaselineExists(string name) =>
        name.Length > 0
        && Path.GetFileName(name) == name
        && (File.Exists(Path.Combine(RootDirectory, name))
            || Directory.Exists(Path.Combine(RootDirectory, name)));

    /// <summary>Content hash of a file container, or of a folder's file list. Empty when the container is gone.</summary>
    private static string Fingerprint(string containerPath)
    {
        if (File.Exists(containerPath))
        {
            using var stream = File.OpenRead(containerPath);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        if (!Directory.Exists(containerPath)) return "";

        var root = Path.GetFullPath(containerPath);
        var lines = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            lines.Add($"{rel}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        }

        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    private static string Key(string containerPath) => Path.GetFullPath(containerPath)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>A corrupt or unreadable record starts clean: a lost baseline costs only one extra copy.</summary>
    private static BaselineState LoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return new BaselineState();

            var state = JsonSerializer.Deserialize<BaselineState>(File.ReadAllText(StateFile));
            var containers = state?.Containers ?? new Dictionary<string, Baseline>();

            // The serializer builds a case-sensitive dictionary; Windows paths are not.
            return new BaselineState
            {
                Version = state?.Version ?? 1,
                Containers = new Dictionary<string, Baseline>(containers, StringComparer.OrdinalIgnoreCase)
            };
        }
        catch
        {
            return new BaselineState();
        }
    }

    /// <summary>A read-only backup folder must not block a write; the next run simply takes a fresh baseline.</summary>
    private static void SaveState(BaselineState state)
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        }
        catch
        {
            // Ignored: the backup itself already succeeded, so the write may proceed.
        }
    }

    private sealed class BaselineState
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Baseline> Containers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Baseline
    {
        public string Backup { get; set; } = "";
        public string Fingerprint { get; set; } = "";
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
