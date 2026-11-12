using System;
using AIArtToolBox.Core;

namespace AIArtToolBox.Modules.Collage;

/// <summary>对比图模块的设置，存在统一 settings.json 的 "collage" 段（原在 %APPDATA%\ComfyCollage）。</summary>
public sealed class CollageSettings
{
    public string? LastDirectory { get; set; }
    public int TileSize { get; set; } = 500;
    public int Rows { get; set; } = 3;
    public int Gap { get; set; } = 8;
    public int Quality { get; set; } = 92;
    public int FitMode { get; set; }
    public int Format { get; set; }
    public int Match { get; set; }
    public string OutputFolder { get; set; } = "_output";
    public string? PythonPath { get; set; }
    public bool ShowHeader { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public bool OnlyCommon { get; set; }
    public bool SkipSingle { get; set; } = true;
    public bool Clean { get; set; }
    public bool OpenAfter { get; set; }
}

/// <summary>对比图模块：把各模型出图横向拼成对比图。</summary>
public sealed class CollageModule : IToolModule, IActivatableModule
{
    private readonly SettingsStore _store = SettingsStore.Shared;
    private Views.CollageView? _view;

    public string Id => ModuleIds.Collage;
    public string Title => "对比图";
    public string Description => "把不同模型的同名出图横向拼成对比图（需要 Python + Pillow）";
    public string Glyph => "\uE8A9";   // ViewAll

    public System.Windows.FrameworkElement CreateView()
    {
        return _view ??= new Views.CollageView(_store);
    }

    public void OnActivated()
    {
        // 进模块时不做额外动作：目录与模型列表在 View 的 Loaded 里恢复
    }

    public void OnDeactivated() => _view?.SaveSettings();
}
