using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace MelvorBridge;

/// <summary>本地 HTTP 控制桥。只监听 127.0.0.1，供本机 AI agent 调用。</summary>
public static class Server
{
    public const string Version = "0.1.0";
    private static HttpListener? _listener;

    public static void Start()
    {
        var port = Environment.GetEnvironmentVariable("MELVOR_BRIDGE_PORT") ?? "27500";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        Log.Write($"MelvorBridge {Version} listening on http://127.0.0.1:{port}/");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => { try { Handle(ctx); } catch (Exception ex) { Log.Write($"handler crash: {ex}"); } }, ctx);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        });
    }

    private static void Handle(HttpListenerContext ctx)
    {
        string method, path;
        try
        {
            var req = ctx.Request;
            method = req.HttpMethod;
            path = req.Url!.AbsolutePath.TrimEnd('/');
        }
        catch { return; } // 客户端已断开
        var q = ctx.Request.QueryString;
        using var sw = new MemoryStream();
        try
        {
            ctx.Request.InputStream.CopyTo(sw);
        }
        catch { }
        var body = sw.ToArray();

        object result;
        try
        {
            result = Route(path, q, body, method);
        }
        catch (Exception ex)
        {
            result = new Dictionary<string, object?> { ["error"] = ex.Message, ["type"] = ex.GetType().Name };
        }

        try
        {
            var json = JsonSerializer.Serialize(result, JsonOpts);
            var buf = System.Text.Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.Close();
            Log.Write($"{method} {path} -> {json.Length} bytes");
        }
        catch { Log.Write($"{method} {path} -> client gone"); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // ---------- 路由 ----------

    private static object Route(string path, System.Collections.Specialized.NameValueCollection q, byte[] body, string method = "GET")
    {
        var game = GameContext.GetGame();
        switch (path)
        {
            case "/health":
                return Dict(
                    ("bridge", Version),
                    ("gameAttached", game != null));

            case "/state": return Handlers.BuildState(game!);

            case "/entities":
                {
                    RequireGame(game);
                    var id = q.Get("id");
                    var withOptions = q.Get("options") == "1";
                    if (id != null) return Handlers.EntityDetail(game!, id, withOptions);
                    return Handlers.ListEntities(game!);
                }

            case "/select":
                {
                    RequireGame(game);
                    var p = ParseBody(body);
                    return Handlers.SelectOption(game!, S(p, "entityId"), S(p, "optionId"));
                }

            case "/start":
                {
                    RequireGame(game);
                    var p = ParseBody(body);
                    return Handlers.StartAction(game!, S(p, "entityId"));
                }

            case "/stop":
                {
                    RequireGame(game);
                    return Handlers.StopAll(game!);
                }

            case "/click":
                {
                    var p = ParseBody(body);
                    return Handlers.Click(D(p, "x", 0.5), D(p, "y", 0.5), D(p, "hold", 0.0));
                }

            case "/screenshot": return Handlers.Screenshot(q);

            case "/packages":
                {
                    var name = q.Get("name");
                    return name != null ? Handlers.ReadPackage(name) : (object)Handlers.ListPackages();
                }

            case "/language":
                {
                    var p = ParseBody(body);
                    return p.Count == 0 || p.TryGetValue("locale", out var _) == false
                        ? Handlers.LanguageInfo()
                        : Handlers.SetLanguage(S(p, "locale") ?? "");
                }

            case "/dir":
                return Handlers.ListDir(q.Get("path") ?? "res://");

            case "/project-setting":
                {
                    var name = q.Get("name") ?? throw new ArgumentException("name required");
                    return Handlers.ReadProjectSetting(name);
                }

            case "/astrology":
                {
                    RequireGame(game);
                    if (method == "POST")
                    {
                        var p2 = ParseBody(body);
                        var study = p2.TryGetValue("study", out var st) ? st?.ToString() : null;
                        return study != null ? Handlers.StudyConstellation(game!, study) : Handlers.AstrologyUnlock(game!);
                    }
                    return Handlers.Astrology(game!);
                }

            case "/bridge-log":
                {
                    int n = int.TryParse(q.Get("n"), out var v) ? v : 50;
                    return Dict(("lines", Log.Tail(n)));
                }

            default:
                return Dict(("error", $"unknown path {path}, try /health /state /entities /select /start /stop /screenshot /packages"));
        }
    }

    private static void RequireGame(object? game)
    {
        if (game == null)
            throw new InvalidOperationException("game not attached (main menu? cloud login?) — load a save first");
    }

    internal static Dictionary<string, object?> Dict(params (string, object?)[] kv)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    private static Dictionary<string, object?> ParseBody(byte[] body)
    {
        if (body.Length == 0) return new();
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(body, JsonOpts) ?? new();
    }

    private static string? S(Dictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) && v != null ? v.ToString() : null;

    private static double D(Dictionary<string, object?> d, string key, double def)
        => d.TryGetValue(key, out var v) && v is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out var d2) ? d2 : def;
}

/// <summary>所有业务 handler。此时 Godot 已就绪，允许强类型调用。</summary>
public static class Handlers
{
    private const BindingFlags PI = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // ---------- 状态 ----------

    public static object BuildState(object game)
    {
        var skills = new List<object>();
        var leveledProp = game.GetType().GetProperty("LeveledEntities");
        if (leveledProp?.GetValue(game) is IEnumerable leveled)
        {
            foreach (var e in leveled)
            {
                var et = e.GetType();
                string? id = et.GetProperty("Id")?.GetValue(e) as string;
                string? name = TryGetDisplay(et.GetProperty("DisplayName")?.GetValue(e));
                var exp = et.GetProperty("Experience")?.GetValue(e);
                var expT = exp?.GetType();
                skills.Add(Server.Dict(
                    ("id", id),
                    ("name", name),
                    ("level", expT?.GetProperty("Level")?.GetValue(exp)),
                    ("levelCap", expT?.GetProperty("LevelCap")?.GetValue(exp))));
            }
        }

        var active = new List<object>();
        var am = GameContext.GetActionManager(game);
        var acts = am?.GetType().GetProperty("ActiveActions")?.GetValue(am) as IEnumerable;
        if (acts != null)
        {
            foreach (var a in acts)
            {
                var at = a.GetType();
                var entity = at.GetProperty("Entity")?.GetValue(a);
                var desc = DescribeAction(a);
                active.Add(Server.Dict(
                    ("entity", entity?.GetType().GetProperty("Id")?.GetValue(entity)),
                    ("entityName", TryGetDisplay(entity?.GetType().GetProperty("DisplayName")?.GetValue(entity))),
                    ("isStarted", at.GetProperty("IsStarted")?.GetValue(a)),
                    ("description", desc)));
            }
        }

        return Server.Dict(
            ("now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
            ("skills", skills),
            ("activeActions", active));
    }

    private static object? DescribeAction(object action)
    {
        try
        {
            // 伐木: CuttingTree -> Tree；其它技能各有"当前选项"属性，这里找常见形态
            foreach (var prop in action.GetType().GetProperties(PI))
            {
                var name = prop.Name;
                if (name is "ActionTimer") continue;
                var pt = prop.PropertyType;
                if (pt.IsPrimitive || pt == typeof(string)) continue;
                if (!name.Contains("Current") && !name.Contains("Selected") && !name.Contains("Cutting")) continue;
                var val = prop.GetValue(action);
                if (val == null) continue;
                var vt = val.GetType();
                if (vt == typeof(string) || pt.Namespace?.StartsWith("System") == true) continue;
                var id = vt.GetProperty("Id")?.GetValue(val) as string;
                var nm = TryGetDisplay(vt.GetProperty("DisplayName")?.GetValue(val));
                if (id != null || nm != null) return $"{name}={id ?? nm}";
            }
        }
        catch { }
        return null;
    }

    // ---------- 实体 ----------

    /// <summary>通用字典枚举（ReadOnlyDictionary&lt;K,V&gt; 无法 as 到 object 版本）。</summary>
    internal static IEnumerable<(string?, object?)> EnumerateDict(object? dictObj)
    {
        if (dictObj is not IEnumerable en) yield break;
        foreach (var kv in en)
        {
            var kvt = kv.GetType();
            yield return (kvt.GetProperty("Key")?.GetValue(kv) as string, kvt.GetProperty("Value")?.GetValue(kv));
        }
    }

    public static object ListEntities(object game)
    {
        var outList = new List<object>();
        var entities = GameContext.GetEntities(game);
        var getAll = entities?.GetType().GetMethod("GetAllObjects");
        if (getAll?.Invoke(entities, null) is not IEnumerable all) return Server.Dict(("error", "no entities registry"));
        foreach (var e in all)
        {
            var et = e.GetType();
            var comps = EnumerateDict(et.GetProperty("Components")?.GetValue(e)).ToList();
            var exp = et.GetProperty("Experience")?.GetValue(e);
            outList.Add(Server.Dict(
                ("id", et.GetProperty("Id")?.GetValue(e) as string),
                ("name", TryGetDisplay(et.GetProperty("DisplayName")?.GetValue(e))),
                ("level", exp != null ? exp.GetType().GetProperty("Level")?.GetValue(exp) : null),
                ("components", comps.Select(c => c.Item1).ToList())));
        }
        return outList;
    }

    public static object EntityDetail(object game, string id, bool withOptions)
    {
        var e = GetEntityById(game, id) ?? throw new InvalidOperationException($"entity '{id}' not found");
        var et = e.GetType();
        var comps = EnumerateDict(et.GetProperty("Components")?.GetValue(e)).ToList();

        var compDump = new Dictionary<string, object?>();
        foreach (var (k, comp) in comps)
            compDump[k ?? "?"] = Dump.Shallow(comp, depth: 2);

        object? options = null;
        if (withOptions)
        {
            var actionComp = comps.Select(c => c.Item2).FirstOrDefault(c =>
                c != null && c.GetType().BaseTypeChain().Any(t => t.Name is "ActionComponent" or "BaseActionComponent"));
            if (actionComp != null) options = Dump.Options(actionComp);
        }

        return Server.Dict(
            ("id", et.GetProperty("Id")?.GetValue(e) as string),
            ("name", TryGetDisplay(et.GetProperty("DisplayName")?.GetValue(e))),
            ("components", compDump),
            ("options", options));
    }

    internal static object? GetEntityById(object game, string id)
    {
        var entities = GameContext.GetEntities(game);
        var getAll = entities?.GetType().GetMethod("GetAllObjects");
        if (getAll?.Invoke(entities, null) is not IEnumerable all) return null;
        foreach (var e in all)
            if (e.GetType().GetProperty("Id")?.GetValue(e) as string == id) return e;
        return null;
    }

    internal static object? GetActionComponent(object game, string entityId)
    {
        var e = GetEntityById(game, entityId) ?? throw new InvalidOperationException($"entity '{entityId}' not found");
        return EnumerateDict(e.GetType().GetProperty("Components")?.GetValue(e))
            .Select(c => c.Item2)
            .FirstOrDefault(c => c != null && c.GetType().BaseTypeChain().Any(t => t.Name == "ActionComponent"));
    }

    // ---------- 动作控制 ----------

    public static object SelectOption(object game, string entityId, string optionId)
    {
        if (optionId == null) throw new ArgumentException("optionId required");
        return GameContext.RunOnMainThread(() =>
        {
            var comp = GetActionComponent(game, entityId) ?? throw new InvalidOperationException($"no action component on '{entityId}'");

            // 找 Select* 方法与匹配选项对象
            var selects = comp.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name.StartsWith("Select") && m.GetParameters().Length == 1 && !m.IsGenericMethodDefinition)
                .ToList();
            foreach (var (argObj, argId, alt) in EnumerateOptionObjects(comp))
            {
                if (argId != optionId) continue;
                var m = selects.FirstOrDefault(s => s.GetParameters()[0].ParameterType.IsInstanceOfType(argObj));
                object? arg = argObj;
                if (m == null && alt != null)
                {
                    // id 在 Recipe 上、Select 吃 value 的场景（如伐木 SelectTree(Tree)）
                    m = selects.FirstOrDefault(s => s.GetParameters()[0].ParameterType.IsInstanceOfType(alt));
                    if (m != null) arg = alt;
                }
                if (m == null)
                    throw new InvalidOperationException($"no Select method accepts {argObj.GetType().Name}");
                var r = m.Invoke(comp, new[] { arg });
                return Server.Dict(
                    ("selected", optionId),
                    ("method", m.Name),
                    ("argType", arg.GetType().Name),
                    ("invokeResult", r?.ToString() ?? "(void)"),
                    ("isStarted", comp.GetType().GetProperty("IsStarted")?.GetValue(comp)));
            }
            throw new InvalidOperationException($"option '{optionId}' not found on '{entityId}'");
        });
    }

    public static object StartAction(object game, string entityId)
    {
        return GameContext.RunOnMainThread(() =>
        {
            var comp = GetActionComponent(game, entityId) ?? throw new InvalidOperationException($"no action component on '{entityId}'");
            var tryStart = comp.GetType().GetMethod("TryStart", Type.EmptyTypes)
                ?? throw new InvalidOperationException("no TryStart");
            var ok = (bool)tryStart.Invoke(comp, null)!;
            return Server.Dict(("ok", ok), ("isStarted", comp.GetType().GetProperty("IsStarted")?.GetValue(comp)));
        });
    }

    public static object StopAll(object game)
    {
        return GameContext.RunOnMainThread(() =>
        {
            var am = GameContext.GetActionManager(game) ?? throw new InvalidOperationException("no action manager");
            var stop = am.GetType().GetMethod("TryStopActive", new[] { typeof(bool) })!;
            var ok = (bool)stop.Invoke(am, new object[] { true })!;
            return Server.Dict(("ok", ok));
        });
    }

    // ---------- 运行时文件系统/项目设置（PCK 在运行时目录已解密） ----------

    public static object ListDir(string path)
    {
        var outList = new List<string>();
        GameContext.RunOnMainThread(() =>
        {
            var dir = Godot.DirAccess.Open(path);
            if (dir == null) return;
            dir.ListDirBegin();
            while (dir.GetNext() is { } f && f.Length > 0) outList.Add(f);
            dir.ListDirEnd();
        });
        return Server.Dict(("path", path), ("entries", outList));
    }

    public static object ReadProjectSetting(string name)
    {
        return GameContext.RunOnMainThread<object>(() =>
        {
            var v = Godot.ProjectSettings.GetSetting(name);
            return Server.Dict(("name", name), ("value", Godot.Variant.From(v).ToString()));
        });
    }

    // ---------- 星象学 ----------

    public static object Astrology(object game)
    {
        var comp = GetActionComponent(game, "MelvorBase:Astrology")
            ?? throw new InvalidOperationException("astrology action component not found");
        return GameContext.RunOnMainThread<object>(() =>
        {
            object? P(object? o, string name) => o?.GetType().GetProperty(name)?.GetValue(o);
            var sc = P(comp, "StudiedConstellation");
            var constellation = P(sc, "Constellation");
            return Server.Dict(
                ("studied", P(constellation, "Id")),
                ("knowledge", P(sc, "Knowledge")),
                ("minorUnlocked", P(sc, "MinorNodeUnlockCount")),
                ("minorAvailable", P(sc, "AvailableMinorNodeCount")),
                ("majorUnlocked", P(sc, "MajorNodeUnlockCount")),
                ("majorAvailable", P(sc, "AvailableMajorNodeCount")),
                ("isStarted", P(comp, "IsStarted")));
        });
    }

    /// <summary>解锁当前星座全部可解节点（小节点优先，条件不满足的自动跳过）。</summary>
    public static object AstrologyUnlock(object game)
    {
        var comp = GetActionComponent(game, "MelvorBase:Astrology")
            ?? throw new InvalidOperationException("astrology action component not found");
        return GameContext.RunOnMainThread<object>(() =>
        {
            object? P(object? o, string name) => o?.GetType().GetProperty(name)?.GetValue(o);
            var sc = P(comp, "StudiedConstellation") ?? throw new InvalidOperationException("no studied constellation");
            var unlockMinor = sc.GetType().GetMethod("UnlockMinorNode")!;
            var unlockMajor = sc.GetType().GetMethod("UnlockMajorNode")!;
            var isMinorUnlockedM = sc.GetType().GetMethod("IsMinorNodeUnlocked")!;
            var isMajorUnlockedM = sc.GetType().GetMethod("IsMajorNodeUnlocked")!;

            static bool RequirementsMet(object? node)
            {
                var req = node?.GetType().GetProperty("UnlockRequirements")?.GetValue(node);
                var areMet = req?.GetType().GetProperty("AreMet")?.GetValue(req);
                return areMet is null or true;
            }

            bool Progress(IEnumerable nodes, MethodInfo unlock, Func<object, bool> already, Func<object, bool> met, List<string> acc)
            {
                foreach (var n in nodes)
                {
                    if (already(n)) continue;
                    if (!met(n)) continue;
                    if ((bool)unlock.Invoke(sc, new[] { n })!)
                    {
                        acc.Add(P(n, "Id") as string ?? "?");
                        return true;
                    }
                }
                return false;
            }

            var minorNodes = P(P(sc, "Constellation"), "MinorNodes") as IEnumerable ?? Array.Empty<object>();
            var majorNodes = P(P(sc, "Constellation"), "MajorNodes") as IEnumerable ?? Array.Empty<object>();
            var unlocked = new List<string>();
            bool changed = true;
            while (changed)
            {
                changed = false;
                if (Progress(minorNodes, unlockMinor,
                        n => (bool)isMinorUnlockedM.Invoke(sc, new[] { n })!, RequirementsMet, unlocked))
                { changed = true; continue; }
                if (Progress(majorNodes, unlockMajor,
                        n => (bool)isMajorUnlockedM.Invoke(sc, new[] { n })!, RequirementsMet, unlocked))
                { changed = true; }
            }

            return Server.Dict(
                ("unlockedNodes", unlocked),
                ("count", unlocked.Count),
                ("knowledge", P(sc, "Knowledge")),
                ("minorUnlocked", P(sc, "MinorNodeUnlockCount")),
                ("minorAvailable", P(sc, "AvailableMinorNodeCount")),
                ("majorUnlocked", P(sc, "MajorNodeUnlockCount")),
                ("majorAvailable", P(sc, "AvailableMajorNodeCount")));
        });
    }

    /// <summary>切换研究星座（自动开始研究）。传入星座实体 Id，如 MelvorBase:Ko。</summary>
    public static object StudyConstellation(object game, string constellationId)
    {
        var comp = GetActionComponent(game, "MelvorBase:Astrology")
            ?? throw new InvalidOperationException("astrology action component not found");
        return GameContext.RunOnMainThread<object>(() =>
        {
            object? P(object? o, string name) => o?.GetType().GetProperty(name)?.GetValue(o);
            var actives = P(comp, "ActiveConstellations");
            var study = comp.GetType().GetMethod("StudyConstellation", new[] { Type.GetType("GamesByMalcs.Melvor2.Entities.Astrology.ActiveAstrologyConstellation, GamesByMalcs.Melvor2") })
                ?? comp.GetType().GetMethods().First(m => m.Name == "StudyConstellation");
            foreach (var kv in (System.Collections.IEnumerable)actives!)
            {
                var kvt = kv.GetType();
                var key = kvt.GetProperty("Key")?.GetValue(kv);          // AstrologyConstellation
                var val = kvt.GetProperty("Value")?.GetValue(kv);        // ActiveAstrologyConstellation
                if (P(key, "Id") as string != constellationId) continue;
                study.Invoke(comp, new[] { val });
                return Server.Dict(
                    ("studying", constellationId),
                    ("isStarted", P(comp, "IsStarted")),
                    ("knowledge", P(val, "Knowledge")),
                    ("minorUnlocked", P(val, "MinorNodeUnlockCount")),
                    ("minorAvailable", P(val, "AvailableMinorNodeCount")),
                    ("majorUnlocked", P(val, "MajorNodeUnlockCount")),
                    ("majorAvailable", P(val, "AvailableMajorNodeCount")));
            }
            throw new InvalidOperationException($"constellation '{constellationId}' not active/available (level gate?)");
        });
    }

    // ---------- 语言 ----------

    private static (object? user, System.Reflection.PropertyInfo langProp) GetUserSettings()
    {
        var lsm = Type.GetType("GamesByMalcs.Melvor2.Settings.LocalSettingsManager, GamesByMalcs.Melvor2")
            ?? throw new InvalidOperationException("LocalSettingsManager type not found");
        var user = lsm.GetProperty("User", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
            ?? throw new InvalidOperationException("LocalSettingsManager.User is null");
        var langProp = user.GetType().GetProperty("Language")
            ?? throw new InvalidOperationException("UserSettings.Language prop not found");
        return (user, langProp);
    }

    public static object LanguageInfo()
    {
        return GameContext.RunOnMainThread<object>(() =>
        {
            var (user, langProp) = GetUserSettings();
            var current = langProp.GetValue(user) as string;
            var loaded = Godot.TranslationServer.GetLoadedLocales().ToList();
            return Server.Dict(("current", current), ("loadedLocales", loaded));
        });
    }

    public static object SetLanguage(string locale)
    {
        return GameContext.RunOnMainThread<object>(() =>
        {
            var (user, langProp) = GetUserSettings();
            langProp.SetValue(user, locale);
            var now = langProp.GetValue(user) as string;
            return Server.Dict(("requested", locale), ("current", now),
                ("ok", now == locale), ("activeLocale", Godot.TranslationServer.GetLocale()));
        });
    }

    // ---------- 通用 UI 点击（归一化坐标 0..1） ----------

    public static object Click(double nx, double ny, double holdMs)
    {
        var r = GameContext.RunOnMainThread<string?>(() =>
        {
            var root = (Godot.Engine.GetMainLoop() as Godot.SceneTree)?.Root;
            if (root == null) return null;
            var size = root.Size;
            var pos = new Godot.Vector2((float)(nx * size.X), (float)(ny * size.Y));
            var motion = new Godot.InputEventMouseMotion { Position = pos, GlobalPosition = pos };
            Godot.Input.ParseInputEvent(motion);
            var press = new Godot.InputEventMouseButton
            {
                Position = pos, GlobalPosition = pos,
                ButtonIndex = Godot.MouseButton.Left, Pressed = true,
            };
            Godot.Input.ParseInputEvent(press);
            if (holdMs > 0) Thread.Sleep((int)holdMs);
            var release = new Godot.InputEventMouseButton
            {
                Position = pos, GlobalPosition = pos,
                ButtonIndex = Godot.MouseButton.Left, Pressed = false,
            };
            Godot.Input.ParseInputEvent(release);
            return $"clicked ({pos.X:0},{pos.Y:0}) of {size.X:0}x{size.Y:0}";
        });
        return Server.Dict(("result", r ?? "no viewport"));
    }

    // ---------- 截图 ----------

    public static object Screenshot(System.Collections.Specialized.NameValueCollection q)
    {
        var png = GameContext.RunOnMainThread<byte[]?>(() =>
        {
            var vp = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            var root = vp?.Root;
            if (root == null) return null;
            var img = root.GetViewport().GetTexture().GetImage();
            int w = q.Get("width") is string ws && int.TryParse(ws, out var wv) ? wv : 960;
            if (w > 0 && img.GetSize().X > w)
            {
                float ratio = img.GetSize().Y / img.GetSize().X;
                img.Resize(w, (int)(w * ratio));
            }
            return img.SavePngToBuffer();
        }) ?? throw new InvalidOperationException("viewport not available");
        return Server.Dict(("pngBase64", Convert.ToBase64String(png)), ("bytes", png.Length));
    }

    // ---------- 数据包 ----------

    public static object ListPackages()
    {
        var files = new List<string>();
        GameContext.RunOnMainThread(() =>
        {
            var dir = Godot.DirAccess.Open("res://Data/Packages");
            if (dir == null) return;
            dir.ListDirBegin();
            while (dir.GetNext() is { } f && f.Length > 0) if (f.EndsWith(".json")) files.Add(f);
            dir.ListDirEnd();
        });
        return Server.Dict(("namespace", "res://Data/Packages/"), ("files", files));
    }

    public static object ReadPackage(string name)
    {
        if (!name.EndsWith(".json")) name += ".json";
        if (name.Contains("..") || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("bad package name");
        var text = GameContext.RunOnMainThread<string?>(() =>
        {
            using var fa = Godot.FileAccess.Open($"res://Data/Packages/{name}", Godot.FileAccess.ModeFlags.Read);
            return fa?.GetAsText();
        }) ?? throw new InvalidOperationException($"cannot open {name}");
        return Server.Dict(("name", name), ("chars", text.Length), ("json", JsonDocument.Parse(text).RootElement.Clone()));
    }

    // ---------- 工具 ----------

    internal static string? TryGetDisplay(object? displayName)
    {
        if (displayName == null) return null;
        var t = displayName.GetType();
        if (t.Name == "LocalizedString")
        {
            try { return t.GetProperty("Text")?.GetValue(displayName) as string; } catch { }
        }
        try { return displayName.ToString(); } catch { return $"<{t.Name}>"; }
    }

    /// <summary>
    /// 枚举动作组件上的全部可选对象：(对象, id, 配对对象)。
    /// 字典容器里 id 常在 Recipe(key) 上，而 Select 方法可能吃 value —— 配对对象作为回退候选。
    /// </summary>
    internal static IEnumerable<(object obj, string id, object? alt)> EnumerateOptionObjects(object component)
    {
        foreach (var prop in component.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            object? val;
            try { val = prop.GetValue(component); } catch { continue; }
            if (val == null) continue;
            if (val is not IEnumerable en || val is string) continue;

            foreach (var item in en)
            {
                var it = item.GetType();
                if (it.IsGenericType && it.GetGenericTypeDefinition().Name.StartsWith("KeyValuePair"))
                {
                    var k = it.GetProperty("Key")?.GetValue(item);
                    var v = it.GetProperty("Value")?.GetValue(item);
                    var kid = GetId(k);
                    if (kid != null && k != null) yield return (k, kid, v);
                    var vid = GetId(v);
                    if (vid != null && v != null) yield return (v, vid, k);
                }
                else if (item is DictionaryEntry de)
                {
                    var kid = GetId(de.Key);
                    if (kid != null && de.Key != null) yield return (de.Key, kid, de.Value);
                    var vid = GetId(de.Value);
                    if (vid != null && de.Value != null) yield return (de.Value, vid, de.Key);
                }
                else
                {
                    var id = GetId(item);
                    if (id != null) yield return (item, id, null);
                }
            }
        }
    }

    internal static string? GetId(object? o)
    {
        if (o == null) return null;
        return o.GetType().GetProperty("Id")?.GetValue(o) as string;
    }
}

/// <summary>反射 dump 工具：把游戏对象安全压平成 JSON 友好结构。</summary>
public static class Dump
{
    public static object? Shallow(object o, int depth)
    {
        if (o == null) return null;
        var t = o.GetType();
        if (t.IsPrimitive || t == typeof(string) || t == typeof(decimal) || t.IsEnum)
            return t.IsEnum ? o.ToString() : o;

        if (depth <= 0) return $"<{t.Name}>";
        if (t.Namespace?.StartsWith("System.Collections") == true || o is IEnumerable)
        {
            if (o is string) return o;
            var list = new List<object?>();
            int i = 0;
            foreach (var x in (IEnumerable)o) { if (++i > 64) { list.Add($"...({i}+)"); break; } list.Add(Shallow(x, depth - 1)); }
            return list;
        }

        var d = new Dictionary<string, object?>();
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            try
            {
                var pt = p.PropertyType;
                if (pt.Namespace?.StartsWith("Godot") == true)
                {
                    // Godot 原生类型只取名字，避免触发 native 副作用
                    if (pt.Name == "Texture2D" || pt.Name == "AudioStream" || pt.BaseType?.Name == "Resource") { d[p.Name] = $"<{pt.Name}>"; continue; }
                }
                var v = p.GetValue(o);
                if (v == null) { d[p.Name] = null; continue; }
                var vt = v.GetType();
                if (vt.IsPrimitive || vt == typeof(string) || vt.IsEnum || vt == typeof(decimal))
                    d[p.Name] = vt.IsEnum ? v.ToString() : v;
                else if (vt.Name == "LocalizedString")
                    d[p.Name] = vt.GetProperty("Text")?.GetValue(v);
                else
                    d[p.Name] = $"<{vt.Name}>";
            }
            catch { d[p.Name] = "<error>"; }
        }
        return d;
    }

    /// <summary>动作组件的可选项列表（树/鱼/矿石…），通用反射发现。</summary>
    public static object Options(object actionComponent)
    {
        var t = actionComponent.GetType();
        var result = new List<object>();
        int scanned = 0;
        foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            object? val;
            try { val = prop.GetValue(actionComponent); } catch { continue; }
            if (val == null) continue;
            if (val is not IEnumerable en || val is string) continue;

            foreach (var item in en)
            {
                if (++scanned > 200) goto done;
                string? id = null; string? name = null; string? kind = null;
                object? target = item;

                // ReadOnlyDictionary 枚举出 KeyValuePair<K,V>（结构体）；IDictionary 枚举出 DictionaryEntry
                var it = item.GetType();
                if (it.IsGenericType && it.GetGenericTypeDefinition().Name.StartsWith("KeyValuePair"))
                {
                    target = it.GetProperty("Key")?.GetValue(item);
                    if (target == null || target.GetType().GetProperty("Id") == null)
                        target = it.GetProperty("Value")?.GetValue(item);
                }
                else if (item is DictionaryEntry de)
                {
                    // 优先 key（Recipe 有 Id/DisplayName），value 是运行时状态
                    target = de.Key;
                    if (target == null || target.GetType().GetProperty("Id") == null) target = de.Value;
                }

                if (target != null)
                {
                    var tt = target.GetType();
                    id = tt.GetProperty("Id")?.GetValue(target) as string;
                    var dn = tt.GetProperty("DisplayName")?.GetValue(target);
                    name = dn?.GetType().GetProperty("Text")?.GetValue(dn) as string ?? dn?.ToString();
                    kind = tt.Name;
                }
                if (id != null)
                    result.Add(Server.Dict(("id", id), ("name", name), ("type", kind), ("from", prop.Name)));
            }
        }
    done:
        return result.DistinctBy(x => ((Dictionary<string, object?>)x).TryGetValue("id", out var v) ? v : null).ToList();
    }
}

public static class ReflectExt
{
    public static IEnumerable<Type> BaseTypeChain(this Type t)
    {
        var cur = t;
        while (cur != null) { yield return cur; cur = cur.BaseType!; }
    }
}
