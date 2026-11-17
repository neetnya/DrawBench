using DrawBench.Core;

namespace DrawBench.Modules.Gacha;

/// <summary>抽卡模块：按模块化 Danbooru 词库自由组合各维度并抽取提示词。</summary>
public sealed class GachaModule : IToolModule, IActivatableModule
{
    private Views.GachaView? _view;

    public string Id => ModuleIds.Gacha;
    public string Title => "抽卡";
    public string Description => "按模块化 Danbooru 词库自由选择要抽取的模块，生成提示词";
    public string Glyph => "\uE8EC";   // Game

    public System.Windows.FrameworkElement CreateView() => _view ??= new Views.GachaView();

    public void OnActivated() { }

    public void OnDeactivated() => _view?.SaveSettings();
}
