using System;
using System.Collections.Generic;
using DrawBench.Core;
using DrawBench.Core.Logging;

namespace DrawBench.App;

/// <summary>
/// 通过反射创建 Modules 程序集里所有 <see cref="IToolModule"/> 实现。
/// 这样外壳不需要逐个引用模块，加模块也只是往 Modules 里扔一个类。
/// </summary>
internal static class ReflectiveModuleFactory
{
    public static IEnumerable<IToolModule> CreateAll()
    {
        var result = new List<IToolModule>();

        Type[] types;
        try
        {
            types = typeof(DrawBench.Modules.ModuleAssemblyMarker).Assembly.GetTypes();
        }
        catch (Exception ex)
        {
            AppLog.Error("读取模块程序集失败", ex);
            return result;
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!typeof(IToolModule).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) == null) continue;

            IToolModule? module = null;
            try
            {
                module = (IToolModule?)Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                AppLog.Error($"创建模块 {type.Name} 失败", ex);
            }

            if (module != null) result.Add(module);
        }

        // 导航顺序按 Order 约定：模块类里不用写，按标题手工排一下
        result.Sort((a, b) => ModuleOrder(a.Id).CompareTo(ModuleOrder(b.Id)));
        return result;
    }

    /// <summary>导航顺序：批量提交打头，其后是抽卡 → 回读 → 反推 → 对比。</summary>
    private static int ModuleOrder(string id) => id.ToLowerInvariant() switch
    {
        "batch" => 0,
        "gacha" => 1,
        "metadata" => 2,
        "tagger" => 3,
        "collage" => 4,
        _ => 99
    };
}
