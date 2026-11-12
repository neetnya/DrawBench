namespace DrawBench.App;

/// <summary>
/// 启动期一次性收尾：注册命令行子命令。
/// </summary>
internal static class StartupTasks
{
    public static void Run()
    {
        // 命令行：把反推模块的批量模式挂到底下
        DrawBench.Core.Cli.CliDispatcher.Register(
            DrawBench.Modules.ModuleIds.Tagger,
            DrawBench.Modules.Tagger.TaggerModule.RunCli);
    }
}
