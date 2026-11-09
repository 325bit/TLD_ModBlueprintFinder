using TLD_ModBlueprintFinder.Ui;

namespace TLD_ModBlueprintFinder;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Headless mode: scriptable queries, and a way to exercise the parsers without a GUI (see Cli.cs).
        if (args.Length > 0 && args[0].Equals("--cli", StringComparison.OrdinalIgnoreCase))
            return Cli.Run(args);

        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);

        try
        {
            // Optional presets so a desktop shortcut can run one query in a double-click:
            //   TLD_ModBlueprintFinder.exe --path "<game root>" --item GEAR_AcornShelled
            Application.Run(new MainForm(GetArg(args, "path"), GetArg(args, "item")));
        }
        catch (Exception ex)
        {
            ShowFatal(ex);
        }

        return 0;
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--" + name, StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-" + name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static void ShowFatal(Exception ex)
    {
        MessageBox.Show(
            "程序遇到未处理的错误：\n\n" + ex.Message + "\n\n" + ex.StackTrace,
            "TLD Mod Blueprint Finder",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
