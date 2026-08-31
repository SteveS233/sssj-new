using System.Reflection;
using System.Text.Json;
using SssjServer.Core;
using SssjServer.Game;
using Xunit;
using Xunit.Abstractions;

namespace SssjServer.Game.Tests;

/// <summary>
/// 覆盖率审计：**装配真实 <see cref="GameModule"/> 后枚举它的活路由表**，
/// 与 <c>docs/luacall-table.json</c>（354 条客户端命令）对账。
///
/// ★ 为什么用这种方式而不是 grep 源码：`MapLuaCall(XxxCmd, ...)` 的实参是**常量名**，
/// grep 只能数出「有多少个调用点」，数不出**实际命令号**，也不知道有没有重复注册/被覆盖。
/// 反射读路由字典是唯一权威口径。
///
/// 本类**不做断言**（除了几条防退化下限），只把数字打到测试输出里 —— 它是**度量工具**。
/// </summary>
public class CoverageAuditTests
{
    private readonly ITestOutputHelper _out;

    public CoverageAuditTests(ITestOutputHelper output) => _out = output;

    private static Dictionary<string, T> PrivateField<T>(object obj, string name)
    {
        var f = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"字段 {name} 不存在");
        return (Dictionary<string, T>)f.GetValue(obj)!;
    }

    private static Dictionary<int, T> PrivateIntField<T>(object obj, string name)
    {
        var f = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"字段 {name} 不存在");
        return (Dictionary<int, T>)f.GetValue(obj)!;
    }

    [Fact]
    public void 审计_服务端实际注册的命令面()
    {
        var options = new ServerOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "cov-" + Guid.NewGuid().ToString("N")),
            SettingDirectory = TestPaths.SettingDir,
            ServerGmFile = "",
            GmFile = "",
        };
        Directory.CreateDirectory(options.DataDirectory);

        var pf = new PfRouter();
        var luaCalls = new LuaCallRouter();
        var module = new GameModule(options);
        module.Install(pf, luaCalls);

        // ---- 活路由表 ----
        var luaMap = PrivateIntField<LuaCallHandler>(luaCalls, "_map");
        var funcMap = PrivateField<ScriptCallHandler>(luaCalls, "_funcMap");
        var rawMap = PrivateField<RawScriptCallHandler>(luaCalls, "_rawFuncMap");
        var coMap = PrivateField<List<ScriptCoHandler>>(luaCalls, "_funcCoHandlers");

        var pfField = typeof(PfRouter).GetField("_map",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var pfMap = (System.Collections.IDictionary)pfField.GetValue(pf)!;

        _out.WriteLine("=========== 服务端实际注册（反射读活路由表）===========");
        _out.WriteLine($"LuaCall 数字命令 : {luaMap.Count}");
        _out.WriteLine($"sFunction 主handler: {funcMap.Count}  ({string.Join(", ", funcMap.Keys.OrderBy(k => k))})");
        _out.WriteLine($"sFunction raw     : {rawMap.Count}  ({string.Join(", ", rawMap.Keys.OrderBy(k => k))})");
        _out.WriteLine($"sFunction 协处理器: {coMap.Count}  ({string.Join(", ", coMap.Select(k => $"{k.Key}x{k.Value.Count}"))})");
        _out.WriteLine($"pf 处理器         : {pfMap.Count}");
        var pfKeys = new List<int>();
        foreach (var k in pfMap.Keys)
            pfKeys.Add((int)k!);
        pfKeys.Sort();
        _out.WriteLine($"已注册 pf         : {string.Join(",", pfKeys)}");
        _out.WriteLine($"已注册 LuaCall id : {string.Join(",", luaMap.Keys.OrderBy(k => k))}");

        // ---- 与 docs/luacall-table.json 对账 ----
        string tablePath = Path.Combine(TestPaths.RepoRoot, "docs", "luacall-table.json");
        if (File.Exists(tablePath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(tablePath));
            // 结构是 { "commands": { "<id>": {id,name,comment,has_server_branch,...} } }
            var all = new List<(int Id, string Name, string Comment, bool HasServer)>();
            foreach (var kv in doc.RootElement.GetProperty("commands").EnumerateObject())
            {
                var el = kv.Value;
                int id = el.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int v) ? v : -1;
                string name = el.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                string comment = el.TryGetProperty("comment", out var cEl) ? cEl.GetString() ?? "" : "";
                bool hasServer = el.TryGetProperty("has_server_branch", out var sEl)
                                 && sEl.ValueKind == JsonValueKind.True;
                if (id > 0)
                    all.Add((id, name, comment, hasServer));
            }

            var implemented = all.Where(x => luaMap.ContainsKey(x.Id)).ToList();
            var missing = all.Where(x => !luaMap.ContainsKey(x.Id)).ToList();

            _out.WriteLine("");
            _out.WriteLine("=========== 与 docs/luacall-table.json 对账 ===========");
            _out.WriteLine($"客户端命令总数   : {all.Count}");
            _out.WriteLine($"服务端已实现     : {implemented.Count}");
            _out.WriteLine($"未实现           : {missing.Count}");
            _out.WriteLine($"原始覆盖率       : {implemented.Count * 100.0 / all.Count:F1}%");

            // 有原服分支的才是「真需要服务端做」的（无分支的多是纯客户端/下行）
            var needServer = all.Where(x => x.HasServer).ToList();
            var needDone = needServer.Count(x => luaMap.ContainsKey(x.Id));
            _out.WriteLine($"其中有 MODULE_SERVER 分支: {needServer.Count}，已实现 {needDone}" +
                           $" ⇒ {needDone * 100.0 / Math.Max(1, needServer.Count):F1}%");

            var extra = luaMap.Keys.Where(k => all.All(x => x.Id != k)).OrderBy(k => k).ToList();
            _out.WriteLine($"注册了但不在表里 : {extra.Count}  ({string.Join(",", extra)})");

            _out.WriteLine("");
            _out.WriteLine("---------- 未实现命令清单（id | 名称 | 中文注释）----------");
            foreach (var m in missing.OrderBy(x => x.Id))
                _out.WriteLine($"  {m.Id,6} | {m.Name,-40} | {m.Comment}");
        }
        else
        {
            _out.WriteLine($"!! 找不到 {tablePath}");
        }

        try { Directory.Delete(options.DataDirectory, true); } catch { }

        // 防退化下限（不是精确断言）
        Assert.True(luaMap.Count >= 90, $"LuaCall 注册数不该少于 90，实际 {luaMap.Count}");
        Assert.True(pfMap.Count >= 10, $"pf 注册数不该少于 10，实际 {pfMap.Count}");
    }
}
