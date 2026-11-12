using System;
using AIArtToolBox.Core;
using AIArtToolBox.Modules.Tagger.Core;
using AIArtToolBox.Modules.Tagger.ViewModels;
using AIArtToolBox.Modules.Tagger.Views;

namespace AIArtToolBox.Modules.Tagger;

/// <summary>
/// 反推模块：WD14 本地 ONNX 推理，把图片反推成标签。
/// 同时提供命令行入口（AIArtToolBox.exe tagger --cli …），兼容旧的 WD14Tagger.exe --cli 用法。
/// </summary>
public sealed class TaggerModule : IToolModule, IActivatableModule
{
    private TaggerView? _view;

    public string Id => ModuleIds.Tagger;
    public string Title => "反推标签";
    public string Description => "用 WD14 本地模型把图片反推成提示词标签（需要自备 onnx 模型）";
    public string Glyph => "\uE8AC";   // Tag

    public System.Windows.FrameworkElement CreateView() => _view ??= new TaggerView();

    public void OnActivated()
    {
        // 模型可能在别的机器上刚放好，切进来时补扫一次
        if (_view?.DataContext is MainViewModel { HasModels: false } vm)
            vm.ReloadModels();
    }

    public void OnDeactivated()
    {
        if (_view?.DataContext is MainViewModel vm) vm.SaveConfig();
    }

    /// <summary>命令行：tagger --cli &lt;路径...&gt;</summary>
    public static int RunCli(string[] args, AppPaths paths) => CliRunner.Run(args, paths);
}
