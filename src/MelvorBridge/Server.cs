using System.Reflection;

namespace MelvorBridge;

/// <summary>
/// 定位并持有游戏对象图：SceneTree → Main → GameDriver →(反射)→ Game。
/// 早期等待全程纯反射；确认 Godot 就绪之后才允许强类型调用（见 Server）。
/// GameDriver 在切换存档/重进游戏时会 Bind 新的 Game，因此每次请求都重新解析。
/// </summary>
public static class GameContext
{
    private static volatile bool _godotUp;
    private static volatile bool _connected;
    private static volatile bool _connecting;
    private static object? _gameDriver;
    private static PropertyInfo? _gameProp;

    public static bool Ready => _gameDriver != null;

    public static void Run()
    {
        // HTTP 服务立即上线（/health 可查连接状态）。
        // 游戏连接由 OnNativeObjectCreated 钩子驱动（注入到 GodotObject ctor，主线程）。
        Log.Write("bridge thread up (native hook will drive connection)");
        Server.Start();
    }

    /// <summary>
    /// 由注入到 GodotObject..ctor 的回调调用——永远在 Godot 主线程。
    /// 每次创建 Godot 对象时尝试连接，连上后短路。
    /// </summary>
    public static void OnNativeObjectCreated()
    {
        EnsureTranslations();
        if (_connected) return;
        if (_connecting) return;
        _connecting = true;
        try
        {
            if (TryConnect())
            {
                _connected = true;
                Log.Write("connected to game (from native object ctor hook).");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"connect attempt: {ex.Message}");
        }
        finally { _connecting = false; }
    }

    private static string? _lastFailReason;
    private static DateTime _lastFailLog = DateTime.MinValue;

    private static void FailLog(string reason)
    {
        // 同原因 15 秒最多记一次，避免云登录等界面刷爆日志
        if (reason == _lastFailReason && (DateTime.UtcNow - _lastFailLog).TotalSeconds < 15) return;
        _lastFailReason = reason;
        _lastFailLog = DateTime.UtcNow;
        Log.Write($"connect: {reason}");
    }

    private static bool TryConnect()
    {
        var sceneTree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
        var root = sceneTree?.Root;
        if (root == null) { FailLog("no root"); return false; }

        var mainNode = FindNodeByTypeFullName(root, "GamesByMalcs.Melvor2.Main");
        if (mainNode == null)
        {
            if (!_treeDumped)
            {
                _treeDumped = true;
                Log.Write("scene tree dump:\n" + DumpTree(root, 0, 4));
            }
            FailLog("Main node not found");
            return false;
        }

        var gdProp = mainNode.GetType().GetProperty("GameDriver", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (gdProp == null) { FailLog("no GameDriver prop"); return false; }
        var gd = gdProp.GetValue(mainNode);
        if (gd == null) { FailLog("GameDriver null"); return false; }

        var gameProp = gd.GetType().GetProperty("Game", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (gameProp == null) { FailLog("no Game prop"); return false; }
        var game = gameProp.GetValue(gd);
        if (game == null) { FailLog("game not bound yet (menu?)"); return false; }

        _gameDriver = gd;
        _gameProp = gameProp;
        return true;
    }

    private static bool _treeDumped;

    /// <summary>
    /// 运行时注册 PCK 内的全部翻译（res://Locale/*.po）。
    /// 游戏默认只注册 en_GB.po；device_settings/override.cfg 的追加会被游戏重写抹掉，
    /// 因此在桥启动时（主线程）注册，并按存档保存的语言偏好恢复界面语言。
    /// </summary>
    private static bool _translationsDone;
    private static void EnsureTranslations()
    {
        if (_translationsDone) return;
        _translationsDone = true;
        try
        {
            // 1) 注册 PCK 内所有翻译
            var pos = new List<string>();
            var dir = Godot.DirAccess.Open("res://Locale");
            if (dir != null)
            {
                dir.ListDirBegin();
                while (dir.GetNext() is { } f && f.Length > 0)
                    if (f.EndsWith(".po")) pos.Add($"res://Locale/{f}");
                dir.ListDirEnd();
            }
            foreach (var po in pos)
            {
                if (Godot.ResourceLoader.Exists(po))
                {
                    var t = Godot.ResourceLoader.Load<Godot.Translation>(po);
                    if (t != null) Godot.TranslationServer.AddTranslation(t);
                }
            }
            Log.Write($"translations registered: {string.Join(",", Godot.TranslationServer.GetLoadedLocales())}");

            // 2) 若游戏启动时因翻译缺失把语言回退成 en_GB，按存档偏好恢复
            var lsm = Type.GetType("GamesByMalcs.Melvor2.Settings.LocalSettingsManager, GamesByMalcs.Melvor2");
            var user = lsm?.GetProperty("User", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            var langProp = user?.GetType().GetProperty("Language");
            string? saved = ReadSavedLanguagePreference();
            if (!string.IsNullOrEmpty(saved) && saved != (langProp?.GetValue(user) as string)
                && Godot.TranslationServer.GetLoadedLocales().Contains(saved))
            {
                langProp?.SetValue(user, saved);
                Log.Write($"language restored to saved preference: {saved}");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"EnsureTranslations failed: {ex.Message}");
        }
    }

    /// <summary>读取 userSettings.json 里保存的 Language（steam/&lt;id&gt;/userSettings.json）。</summary>
    private static string? ReadSavedLanguagePreference()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var baseDir = System.IO.Path.Combine(appData, "Games By Malcs", "Melvor Idle 2", "steam");
            if (!System.IO.Directory.Exists(baseDir)) return null;
            foreach (var f in System.IO.Directory.GetFiles(baseDir, "userSettings.json", System.IO.SearchOption.AllDirectories))
            {
                var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("Language", out var lang) && lang.ValueKind == System.Text.Json.JsonValueKind.String)
                    return lang.GetString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>用全部参数默认值调用实例方法（Godot API 大量使用默认参数）。</summary>
    private static object? InvokeWithDefaults(MethodInfo m, object? target)
    {
        var args = m.GetParameters()
            .Select(p => p.DefaultValue is DBNull ? (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null) : p.DefaultValue)
            .ToArray();
        return m.Invoke(target, args);
    }

    private static string DumpTree(Godot.Node node, int depth, int maxDepth)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            sb.AppendLine($"{new string(' ', depth * 2)}{node.Name} [{node.GetType().FullName}]");
            if (depth >= maxDepth) { sb.AppendLine($"{new string(' ', (depth + 1) * 2)}..."); return sb.ToString(); }
            int n = node.GetChildCount();
            for (int i = 0; i < n && i < 40; i++)
            {
                var child = node.GetChild(i);
                if (child != null) sb.Append(DumpTree(child, depth + 1, maxDepth));
            }
        }
        catch (Exception ex) { sb.AppendLine($"{new string(' ', depth * 2)}<error {ex.Message}>"); }
        return sb.ToString();
    }

    private static Godot.Node? FindNodeByTypeFullName(Godot.Node node, string fullName)
    {
        if (node.GetType().FullName == fullName) return node;
        int n = node.GetChildCount();
        for (int i = 0; i < n; i++)
        {
            var child = node.GetChild(i);
            if (child == null) continue;
            var found = FindNodeByTypeFullName(child, fullName);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>当前 Game 实例（可能为 null：玩家退回主菜单时）。</summary>
    public static object? GetGame()
    {
        EnsureConnected();
        var gd = _gameDriver;
        var prop = _gameProp;
        if (gd == null || prop == null) return null;
        try { return prop.GetValue(gd); } catch { return null; }
    }

    /// <summary>
    /// 未连接时主动在主线程重试一次（RunOnMainThread 的 CallDeferred 任何线程可安全调用）。
    /// ctor 钩子在界面稳定后触发变少，这里补上轮询兜底。
    /// </summary>
    public static void EnsureConnected()
    {
        if (_connected) return;
        try
        {
            RunOnMainThread(() =>
            {
                if (_connected) return 0;
                if (TryConnect())
                {
                    _connected = true;
                    Log.Write("connected to game (ensure path).");
                }
                return 0;
            }, timeoutMs: 3000);
        }
        catch { /* 主线程忙或未就绪，下次再试 */ }
    }

    public static object? GetActionManager(object? game = null)
    {
        game ??= GetGame();
        return game?.GetType().GetProperty("ActionManager")?.GetValue(game);
    }

    public static object? GetEntities(object? game = null)
    {
        game ??= GetGame();
        return game?.GetType().GetProperty("Entities")?.GetValue(game);
    }

    /// <summary>
    /// 把操作投递到 Godot 主线程执行（Callable.From(action).CallDeferred()）。
    /// Godot 的 call_deferred 队列线程安全。同步等待结果，超时抛 TimeoutException。
    /// 注意：本方法只在游戏运行期间（GodotSharp 已加载）被 HTTP handler 调用。
    /// </summary>
    public static T? RunOnMainThread<T>(Func<T> fn, int timeoutMs = 15000)
    {
        T? result = default;
        var done = new ManualResetEventSlim(false);
        Exception? err = null;

        Godot.Callable.From((Action)(() =>
        {
            try { result = fn(); }
            catch (Exception ex) { err = ex; Log.Write($"main-thread action failed: {ex}"); }
            finally { done.Set(); }
        })).CallDeferred();

        if (!done.Wait(timeoutMs))
            throw new TimeoutException($"main thread did not process within {timeoutMs}ms");
        if (err != null) throw err;
        return result;
    }

    public static void RunOnMainThread(Action fn, int timeoutMs = 15000) =>
        RunOnMainThread<object?>(() => { fn(); return null; }, timeoutMs);
}
