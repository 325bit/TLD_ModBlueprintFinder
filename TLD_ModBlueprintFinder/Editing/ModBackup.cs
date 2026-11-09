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
