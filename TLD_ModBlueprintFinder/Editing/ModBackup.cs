using System.IO.Compression;

namespace TLD_ModBlueprintFinder.Editing;

/// <summary>
/// modcomponent backups.
/// Backups land in &lt;program dir&gt;\TLDMBF_modcomponent_backup\ as
/// &lt;mod name&gt;.modcomponent-&lt;yyyyMMdd_HHmmss&gt;. The stamp is a suffix rather than a folder so
/// successive backups of one mod sit side by side in Explorer and the number of edits is visible.
/// Only "Back up" and "Save changes" (with auto-backup on) copy anything; every other path stays
/// read-only towards the Mods folder.
/// </summary>
public static class ModBackup
{
    /// <summary>Backup folder name, fixed, next to the executable.</summary>
    public const string FolderName = "TLDMBF_modcomponent_backup";

    /// <summary>Backup root (inside the program directory).</summary>
    public static string RootDirectory => Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>
    /// Backs up one container (.modcomponent/.zip file, or an extracted-folder mod) and returns its path.
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
        var name = isFolder
            ? new DirectoryInfo(containerPath).Name
            : Path.GetFileNameWithoutExtension(containerPath);

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

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);

        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}
