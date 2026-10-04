using AEAssist.CombatRoutine.View.JobView;

namespace HealerACR.Common;

/// <summary>
/// QT 开关的统一入口。
///
/// ⚠️ 0.2.7 及以前这里有个致命 bug：
///     <c>GetQt(名称, 兜底)</c> 直接 return 窗口的值，**兜底参数压根没用上**。
///     而窗口里如果没有这个开关，GetQt 会返回 false ——
///     于是"抽卡 / 占卜"这类职业开关全被读成"关"，Check() 第一行就返回了，
///     表现出来就是"这个技能永远不打"。
///
/// 现在：
///   1. 注册默认值时就顺手登记进窗口（不再依赖调用时机）
///   2. 只有"确实登记成功"的开关才信窗口的值（用户能正常开关）
///   3. 没登记成功的用注册的默认值，绝不再误判成 false
/// </summary>
public static class HealQt
{
    private static JobViewWindow? _窗口;

    /// <summary>默认值表：Reset() 时恢复到这里</summary>
    private static readonly Dictionary<string, bool> _默认值 = new();

    /// <summary>已经成功登记进窗口的开关（只有这些才信窗口的值）</summary>
    private static readonly HashSet<string> _已登记 = new(StringComparer.Ordinal);

    public static void 绑定(JobViewWindow window)
    {
        // ⚠️ 只清"登记标记"，**绝不清 _默认值**。
        //
        // 踩过两次坑，两次都在这一个方法里：
        //
        //   第一次（0.4.5 之前）：把 _默认值 里的东西全补登记到新窗口，
        //     结果学者面板冒出"光速/占卜/抽卡/地星"（别的职业的开关）。
        //
        //   第二次（0.4.5）：改成把默认值表也一起清掉，
        //     结果别的职业运行时 GetQt 既不在 _已登记 里、也不在 _默认值 里，
        //     直接落到兜底值 false —— ACR 整个不动了。
        //
        // 正确做法：只清登记标记（这样不会串到新窗口），
        // 默认值表保留（这样任何职业任何时候都能查到自己的默认值）。
        // 当前职业的开关会在它自己的 构建QT() 里重新登记到它的窗口上。
        // 直接清空窗口上的所有开关，再让本职业的 构建QT() 重新登记。
        // 对照 JobViewWindow.RemoveAllQt() —— 我之前在"清 _已登记 还是清 _默认值"
        // 之间反复踩坑（一次串开关、一次把 ACR 弄哑），原来人家直接提供了清空 API。
        try { window.RemoveAllQt(); } catch { }

        // 只清登记标记，**绝不动 _默认值**（那是所有职业的默认值表，
        // 清掉会让别的职业读开关时落到 false，ACR 直接不动）
        _已登记.Clear();
        _窗口 = window;
    }

    private static void 登记(string 名称, bool 默认)
    {
        if (_窗口 == null) return;

        try
        {
            _窗口.AddQt(名称, 默认);

            // 强制初始化一次：旧配置文件里可能残留着这个键的错误状态
            // （比如从 0.1.x 升上来时，开关被存成了 false）
            _窗口.SetQt(名称, 默认);

            _已登记.Add(名称);
        }
        catch
        {
            // 登记失败就不要把它标记成"已登记"，
            // 这样 GetQt 会退回默认值，而不是误判成 false
        }
    }

    /// <summary>注册一个开关的默认值（构建 QT 时调用）</summary>
    public static void 注册默认值(string 名称, bool 默认)
    {
        _默认值[名称] = 默认;
        登记(名称, 默认);
    }

    public static bool GetQt(string 名称, bool 兜底 = false)
    {
        // 只有登记成功的开关才信窗口（这样用户手动开关依然有效）
        if (_窗口 != null && _已登记.Contains(名称))
        {
            return _窗口.GetQt(名称);
        }

        if (_默认值.TryGetValue(名称, out var d)) return d;

        return 兜底;
    }

    public static void SetQt(string 名称, bool 值)
    {
        // 注意：不改 _默认值 —— 那是 Reset 的基准，不该被运行时操作污染
        _窗口?.SetQt(名称, 值);
    }

    /// <summary>战斗结束/重置时把所有开关恢复默认</summary>
    public static void Reset()
    {
        if (_窗口 == null) return;

        foreach (var (名称, 值) in _默认值)
        {
            _窗口.SetQt(名称, 值);
        }
    }

    // ==================== QT 同步（参考同类 ACR 的 PullQtSyncService）====================

    /// <summary>
    /// 安全读 QT：窗口拿不到 / 抛异常时返回兜底值，绝不把异常抛给调用方。
    /// 对照 PullQtSyncService.SafeGetQt。
    /// </summary>
    public static bool SafeGetQt(string 名称, bool 兜底 = true)
    {
        try
        {
            if (_窗口 != null && _已登记.Contains(名称)) return _窗口.GetQt(名称);
            if (_默认值.TryGetValue(名称, out var d)) return d;
            return 兜底;
        }
        catch
        {
            return 兜底;
        }
    }

    /// <summary>安全写 QT：抛异常也不影响调用方。对照 PullQtSyncService.SafeSetQt。</summary>
    public static void SafeSetQt(string 名称, bool 值)
    {
        try { _窗口?.SetQt(名称, 值); } catch { }
    }

    /// <summary>
    /// 把当前所有 QT 存成快照。
    /// 用途：切职业 / 重载 ACR 前存一份，回来时能还原（对照 PullQtSyncService.UpdateCache）。
    /// </summary>
    public static Dictionary<string, bool> 快照()
    {
        var 结果 = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var 名称 in _已登记)
        {
            try { 结果[名称] = SafeGetQt(名称); } catch { }
        }

        // 没登记进窗口的（默认值表里的）也带上
        foreach (var kv in _默认值)
        {
            if (!结果.ContainsKey(kv.Key)) 结果[kv.Key] = kv.Value;
        }

        return 结果;
    }

    /// <summary>
    /// 把一批 QT 一次性对齐到指定值（对照 PullQtSyncService.AlignAllQtTo）。
    /// 用途：拉怪倒计时统一开关、时间轴批量控制。
    /// </summary>
    public static void AlignAllQtTo(IEnumerable<string> 名称列表, bool 值)
    {
        if (名称列表 == null) return;

        foreach (var 名称 in 名称列表)
        {
            if (string.IsNullOrEmpty(名称)) continue;
            SafeSetQt(名称, 值);
        }
    }

    /// <summary>还原快照</summary>
    public static void 还原(Dictionary<string, bool> 快照)
    {
        if (快照 == null) return;

        foreach (var kv in 快照) SafeSetQt(kv.Key, kv.Value);
    }

    /// <summary>当前登记了多少个 QT 开关（诊断用）</summary>
    public static int 登记数 => _已登记.Count;

    // ==================== 每技能 QT（单技能开关，如学者 8 技能）====================

    /// <summary>技能 id → QT 开关名。登记过的技能由那个开关决定是否放行。</summary>
    private static readonly Dictionary<uint, string> _每技能QT = new();

    /// <summary>登记一个「每技能开关」：技能 id 对应某个 QT 名。</summary>
    public static void 每技能注册(uint 技能Id, string QT名)
    {
        if (技能Id == 0 || string.IsNullOrEmpty(QT名)) return;
        _每技能QT[技能Id] = QT名;
    }

    /// <summary>该技能的每技能开关是否放行（没登记过 = 不设单技能开关 = 放行）。</summary>
    public static bool 每技能通过(uint 技能Id)
    {
        if (技能Id == 0) return true;
        if (!_每技能QT.TryGetValue(技能Id, out var 名)) return true;
        return GetQt(名, true);
    }

    // ==================== 一次性开关（模拟按钮）====================

    private static bool _上次解除熔断;

    /// <summary>上次看到的「强制熔断」值（用来只在变化时回传）</summary>
    private static bool _上次强制熔断;

    /// <summary>
    /// 外部挂上"解除熔断"的实际动作（BlueWhale 挂 DeepSeekClient.解除熔断）。
    /// 原版不挂 → 勾了也没反应，但不报错。
    /// </summary>
    public static Action? 解除熔断请求;

    /// <summary>
    /// 外部挂上"强制熔断"的实际动作
    /// （BlueWhale 挂 `DeepSeekClient.设强制熔断`）。
    ///
    /// ⚠️ 和 `解除熔断请求` **机制不同**，别照拄：
    ///     · 解除熔断 —— 一次性按钮，芷上执行完自动弹回
    ///     · 强制熔断 —— **状态开关**，开就是开、关就是关
    ///
    ///   所以这里传的是 `Action&lt;bool&gt;`（带值），
    ///   而不是无参的 `Action` —— 因为接收方需要知道**开还是关**。
    /// </summary>
    public static Action<bool>? 强制熔断请求;

    /// <summary>
    /// **把某个开关的 QT 值写回去**（供反向同步用）。
    ///
    /// ⚠️ 为什么需要它 —— 双向同步不是"两边都能改"那么简单：
    ///     每帧同步的方向是 **QT → 实际状态**。
    ///     如果代码里把实际状态改了、却没把 QT 改过来，
    ///     下一帧就会被**旧的 QT 值反向覆盖** ——
    ///     现象就是"按了没反应"或"关不掉"。
    ///
    ///     所以只要在代码里改了状态，就必须调这个把 QT 同步过去。
    /// </summary>
    public static void 写回(string 名称, bool 值)
    {
        try { _窗口?.SetQt(名称, 值); } catch { }
    }

    /// <summary>
    /// 每帧调用：监听那种"勾上执行一次就弹回"的开关。
    ///
    /// **为什么这么做**：AEAssist 的 QT 只有 bool 开关，没有"按钮"类型。
    /// 但 QT 开关**可以绑快捷键**（在 QT 控制台里设）——
    /// 所以用"勾上 -> 触发 -> 自动弹回"来模拟按钮，
    /// 用户绑个键就等于有了快捷键。
    ///
    /// 目前用这个机制的：「解除熔断」。
    ///
    /// ── 另外还同步一个**状态型**开关：记录模式 ──
    ///   记录模式是"开就是开、关就是关"的状态开关（不弹回），
    ///   需要在 QT 值和 记录模式.开启 之间做双向同步。
    /// </summary>
    public static void 每帧更新()
    {
        try
        {
            var 现在 = GetQt("解除熔断", false);

            // 从 false 变 true 的那一帧 → 触发一次
            if (现在 && !_上次解除熔断)
            {
                try { 解除熔断请求?.Invoke(); } catch { }
            }

            // 触发后立刻弹回（不管有没有挂回调）
            if (现在)
            {
                try { _窗口?.SetQt("解除熔断", false); } catch { }
                _上次解除熔断 = false;
            }
            else
            {
                _上次解除熔断 = false;
            }
        }
        catch { }

        // ── 强制熔断：**状态型**同步（不弹回）──
        //
        //   ⚠️ 和上面"解除熔断"的区别就在**不弹回**：
        //     开关本身就是状态，用户勾上就一直熔断，直到取消勾选。
        //
        //   ⚠️ 每帧回传（而不是只在变化时）——
        //     因为读配置、重载 ACR、切职业都可能让 QT 值变而不经过这里。
        //     传入方（`设强制熔断`）自己有"没变就 return"的守卫，
        //     所以每帧调不会刷日志、也不会重复做事。
        try
        {
            var 强制 = GetQt("强制熔断", false);
            if (强制 != _上次强制熔断)
            {
                _上次强制熔断 = 强制;
                try { 强制熔断请求?.Invoke(强制); } catch { }
            }
        }
        catch { }

        // ── 记录模式：QT 开关 → 实际状态 ──
        //   ⚠️ 必须双向同步：
        //     ① QT 被勾/取消 → 改 记录模式.开启
        //     ② 代码里直接改了 记录模式.设置() → 把 QT 也改过来
        //        （否则界面显示和实际状态会不一致，用户会以为没生效）
        //
        //   ⚠️ 这里还要顺带维护"施法事件订阅"（记录模式.每帧维护）——
        //      因为打开记录模式时通常在**脱战**，
        //      而 OnBattleUpdate 只在战斗中跑，不在这里订阅就会漏掉开场。
        try
        {
            var qt值 = GetQt("记录模式", false);

            if (qt值 != 记录模式.开启)
            {
                记录模式.设置(qt值);
            }

            记录模式.每帧维护();
        }
        catch { }
    }

    // ============ 原生 API 封装（对照 原生 QT 封装）============
    // 它的 Qt 类就是把 JobViewWindow 的这几个方法包了一层：
    //   GetQt / SetQt / ReverseQt / NewDefault / SetDefaultFromNow / GetQtArray
    // 我补上之前没用到的三个。

    /// <summary>
    /// 拿全部开关名（对照 JobViewWindow.GetQtArray）。
    /// 比遍历 _已登记 更全 —— 时间轴动态加的、外部注册的也能拿到。
    /// </summary>
    public static string[] 全部开关()
    {
        try { return _窗口?.GetQtArray() ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// 反转一个开关（对照 JobViewWindow.ReverseQt）。
    /// 用途：时间轴里做"切换"而不是"设置"—— 比如"每次爆发翻转一次停手开关"。
    /// </summary>
    public static bool 反转(string 名称)
    {
        if (string.IsNullOrEmpty(名称)) return false;
        try { return _窗口?.ReverseQt(名称) ?? false; }
        catch { return false; }
    }

    /// <summary>
    /// 把**当前所有开关状态固化为默认值**（对照 JobViewWindow.SetDefaultFromNow）。
    ///
    /// 用途：用户花时间调好一套配置后，希望以后每次战斗重置都回到这个状态 ——
    /// 调完点一下，以后就按这套走。（后续会接一个 QT 面板按钮）
    /// </summary>
    public static void 固化为默认()
    {
        try { _窗口?.SetDefaultFromNow(); } catch { }
    }

    /// <summary>设置单个开关的默认值（对照 JobViewWindow.NewDefault）</summary>
    public static void 设默认值(string 名称, bool 值)
    {
        if (string.IsNullOrEmpty(名称)) return;
        try { _窗口?.NewDefault(名称, 值); } catch { }
    }
}
