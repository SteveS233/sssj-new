using System.Runtime.CompilerServices;

namespace SssjServer.Game.Tests;

/// <summary>
/// 单测用的路径解析。
///
/// ★ 为什么不用 <c>ItemBaseTable.ResolveSettingDirectory()</c>：那个方法会优先读
/// 全局静态 <c>ItemBaseTable.DefaultDirectory</c>，一旦被同进程里的别的测试写过
/// （历史上 <c>GameModule</c> 就干过这事），后面的测试全部读到错目录 ——
/// 症状是配置表悄悄变空、断言值全不对，而且**只在多测试一起跑时才复现**。
///
/// 这里用 <see cref="CallerFilePathAttribute"/> 从本文件的编译期路径向上找
/// <c>extracted/setting</c>，与运行时的工作目录、环境变量、全局静态都无关。
/// </summary>
internal static class TestPaths
{
    private static readonly Lazy<string> _settingDir = new(() => Resolve());

    /// <summary>仓库里的 <c>extracted/setting</c> 绝对路径。</summary>
    public static string SettingDir => _settingDir.Value;

    /// <summary>仓库根目录绝对路径。</summary>
    public static string RepoRoot => Directory.GetParent(SettingDir)!.Parent!.FullName;

    private static string Resolve([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "extracted", "setting");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException($"从 {thisFile} 向上找不到 extracted/setting");
    }
}
