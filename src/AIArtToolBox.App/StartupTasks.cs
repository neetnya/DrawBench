using System;

namespace AIArtToolBox.App;

/// <summary>
/// 启动期一次性收尾：注册命令行子命令、迁移旧版数据、清理上次异常退出留下的状态。
/// </summary>
internal static class StartupTasks
{
    public static void Run(AIArtToolBox.Core.AppPaths paths)
    {
        // 命令行：把反推模块的批量模式挂到底下
        AIArtToolBox.Core.Cli.CliDispatcher.Register(
            AIArtToolBox.Modules.ModuleIds.Tagger,
            AIArtToolBox.Modules.Tagger.TaggerModule.RunCli);

        // 旧版五个工具留下的结果文件搬进 data\
        foreach (string note in paths.MigrateLegacy())
            AIArtToolBox.Core.Logging.AppLog.Info(note);
    }
}
