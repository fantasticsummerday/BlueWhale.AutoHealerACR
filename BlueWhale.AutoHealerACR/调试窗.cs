using System.Numerics;
using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;
using Dalamud.Bindings.ImGui;
using HealerACR.Common;
using HealerACR.Timeline;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// **实时调试窗** —— 把"每帧实际采集到什么"摊开在屏幕上看。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么需要它 ★
///
///    在此之前，要验证"AI 到底看到了什么"只能翻日志 ——
///    而日志是**事后**的，而且看完还得回游戏里对时间。
///    这个窗口把同一份数据**实时**显示出来，边打边看。
///
///  ★ 它显示的是"原始采集"，不是"结论" ★
///
///    刻意不放"建议放什么"这类结论 —— 那是 QT 面板的事。
///    这里放的是**喂给决策的输入**：
///      副本 / 读条 / 预测 / 技能可用性 / AI 各层状态
///    这样才能回答"AI 为什么这么选"（输入错了还是判断错了）。
///
///  [!] 只读 —— 这个窗口**不改任何状态**，可以放心一直开着。
///      唯一的写操作是它自己的开关（那个存在设置里）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 调试窗
{
    /// <summary>窗口开着吗（持久化在设置里）</summary>
    public static bool 开
    {
        get
        {
            try { return HealSettings.Instance?.启用调试窗 ?? false; }
            catch { return false; }
        }
    }

    private static bool _首次定位 = true;

    /// <summary>用户拖动后的目标位置（null = 还没拖过，用默认贴右缘）。</summary>
    private static Vector2? _目标位置;

    /// <summary>用户调整后的目标尺寸（null = 用默认 440x600）。</summary>
    private static Vector2? _目标尺寸;

    /// <summary>
    /// **把窗口拉回屏幕内**（设置页的补救按钮用）。
    ///
    /// [!] 为什么要这个按钮：万一还是遇到"窗口不见了"，
    ///     点一下就能复位，不用去猜/去删 ImGui 配置。
    /// </summary>
    public static void 复位位置()
    {
        _首次定位 = true;
        _目标位置 = null;
        _目标尺寸 = null;
    }

    /// <summary>当前窗口坐标（设置页显示用，方便报问题时贴出来）。</summary>
    public static string 位置描述
    {
        get
        {
            try
            {
                if (_目标位置 == null) return "（默认：贴屏幕右缘）";
                return $"({_目标位置.Value.X:F0}, {_目标位置.Value.Y:F0})";
            }
            catch { return "?"; }
        }
    }

    /// <summary>
    /// **每帧画**（在 ACR 设置页里调 —— 那一页每帧都画）。
    ///
    /// [!] 为什么放在设置页的绘制里：
    ///     那是**确定每帧都会执行**的地方。调试窗是独立 ImGui 窗口，
    ///     画一次之后会自己留在屏幕上（ImGui 的窗口是持久的），
    ///     所以"每帧画"实际效果就是"窗口一直开着、数据一直刷新"。
    /// </summary>
    public static void 画()
    {
        if (!开) return;

        try
        {
            // ══════════════════════════════════════════════════════════════
            //  [!] 初始位置要**贴屏幕右缘**，不能按比例放（用户实测踩的坑）
            //
            //      原来写的是 `屏.X * 0.62f` + 宽度 460 ——
            //      屏幕不够宽时窗口会**压在 ACR 设置面板上**，
            //      用户点开设置看到的是调试窗，"AI 设置不见了"。
            //
            //      ACR 设置面板在左边，右边那片是空的 => 贴右缘最不容易挡。
            //      仍然完全可拖动（ImGui 默认就能拖标题栏）。
            // ══════════════════════════════════════════════════════════════
            // ══════════════════════════════════════════════════════════════
            //  ★ 窗口位置/尺寸：**每帧钳进可视区** ★
            //
            //  [!] 修的是一个"窗口打不开、而且不报错"的真问题
            //
            //      原来只在 `_首次定位` 时用 `ImGuiCond.FirstUseEver` 设一次，
            //      之后 ImGui 用自己 ini 里存的位置。
            //      一旦那个位置落在**可视区外**（换分辨率 / 多显示器 /
            //      上次拖到边外），`Begin` 就恒返回 **false**
            //      ⇒ 走下面 `return` ⇒ **窗口永不出现，且不产生任何日志**
            //        （用户实测：设置里开关是 true，但窗口不出现，
            //          Dalamud 日志里连一条异常都没有 —— 就是这个原因）
            //
            //  [!] `NoCollapse` 只禁了"折叠"，**没禁"在可视区外"** ——
            //      所以这条路径是真实可达的，不能靠"位置算得好"回避。
            //
            //  [!] 修法：每帧都把位置夹进屏幕（`ImGuiCond.Always`）。
            //      **仍然可以自由拖动** —— 只要拖到的位置在屏幕内就尊重它，
            //      只有拖出可视区时才被拉回来。
            // ══════════════════════════════════════════════════════════════
            var 屏 = ImGui.GetIO().DisplaySize;
            const float 默认宽 = 440f;
            const float 默认高 = 600f;

            if (_首次定位)
            {
                _首次定位 = false;
                _目标位置 = null;   // 第一次由下面的逻辑算
            }

            // 位置：第一次贴右缘；之后沿用 ImGui 记的位置（但会被夹进屏幕）
            var 要用位置 = _目标位置;
            if (要用位置 == null)
            {
                要用位置 = new Vector2(
                    屏.X > 默认宽 + 80f ? 屏.X - 默认宽 - 16f : 16f,
                    屏.Y > 默认高 + 80f ? 屏.Y * 0.12f : 16f);
            }

            // 夹进可视区：留 8 像素边距，并保证**标题栏一定在屏幕内**
            //（否则拖不回来 —— ImGui 的拖动要靠标题栏）
            const float 边距 = 8f;
            var 宽 = Math.Clamp(_目标尺寸?.X ?? 默认宽, 320f, MathF.Max(320f, 屏.X - 边距 * 2));
            var 高 = Math.Clamp(_目标尺寸?.Y ?? 默认高, 240f, MathF.Max(240f, 屏.Y - 边距 * 2));
            var x = Math.Clamp(要用位置.Value.X, 边距, MathF.Max(边距, 屏.X - 宽 - 边距));
            var y = Math.Clamp(要用位置.Value.Y, 边距, MathF.Max(边距, 屏.Y - 高 - 边距));

            ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(宽, 高), ImGuiCond.Always);

            var 显示 = true;

            // ══════════════════════════════════════════════════════════════
            //  [!] `Begin` 返回 **false** 时**绝对不能调 `End`** —— 这是闪退根因
            //
            //      ImGui 的契约：`Begin` 返回 false 表示"这个窗口这一帧没被画"
            //      （被折叠 / 被裁剪 / 完全在屏幕外），**栈上没有它**，
            //      所以**不需要也不允许**配对的 `End`。
            //
            //      我原来写成了：
            //          if (!ImGui.Begin(...)) { ImGui.End(); return; }
            //          try { ... } finally { ImGui.End(); }
            //      => 那条路径上 `End` 被调了**两次** =>
            //         窗口栈不平衡 => **ImGui 断言失败 => 进程崩溃**。
            //
            //  [!] 为什么之前没炸：只有 `Begin` 返回 false 才走到 ——
            //      窗口被折叠 / 屏幕太小 / 窗口完全在可视区外才会发生。
            //      用户这次的分辨率下窗口超出边界，正好触发。
            //
            //  [!] 现在贴右缘的定位（见上）已经降低了"超出边界"的概率，
            //      但**判据本身必须是对的** —— 不能靠"窗口位置算得好"来回避。
            // ══════════════════════════════════════════════════════════════
            if (!ImGui.Begin("小鲸鱼 · 实时数据（只读）", ref 显示,
                             ImGuiWindowFlags.NoCollapse))
            {
                return;      // [!] 不调 End —— Begin=false 时没有配对的 End
            }

            // ── 记住用户拖动后的位置（下一帧的钳制基于它）──
            try
            {
                var 实际 = ImGui.GetWindowPos();
                if (实际.X > 0f || 实际.Y > 0f) _目标位置 = 实际;
                var 实际尺寸 = ImGui.GetWindowSize();
                if (实际尺寸.X >= 320f && 实际尺寸.Y >= 240f) _目标尺寸 = 实际尺寸;
            }
            catch { }

            // [!] 说明窗**可以拖** —— 用户实测遇到"AI 设置不见了"，
            //     实际是这个窗口压在了设置面板上。写一句省得再困惑。
            ImGui.TextDisabled("  这个窗可以拖标题栏移动；挡住设置面板时拖开即可");
            ImGui.Separator();

            try
            {
                if (!显示)
                {
                    // 用户点了右上角关闭 -> 同步回设置（下次不再自动开）
                    try
                    {
                        if (HealSettings.Instance != null)
                        {
                            HealSettings.Instance.启用调试窗 = false;
                            HealSettings.Instance.Save();
                        }
                    }
                    catch { }
                }

                画基础();
                画读条();
                画预测();
                画技能();
                画AI();
            }
            finally
            {
                ImGui.End();
            }
        }
        catch { }
    }

    // ==================== 基础 ====================

    private static void 画基础()
    {
        try
        {
            if (!ImGui.CollapsingHeader("基础局面", ImGuiTreeNodeFlags.DefaultOpen)) return;

            var 地图 = TimelineManager.实时副本Id();
            var 是副本 = 地图 != 0 && 地名.是副本(地图);
            var 名 = 地图 == 0 ? "未知" : 地名.解析(地图, 是副本: 是副本);

            ImGui.Text($"{(是副本 ? "副本" : "所在地")}：{名}");
            ImGui.TextDisabled($"  地图 id {地图}｜在副本里={进本识别.在副本里()}");

            try
            {
                var 我 = Core.Me;
                if (我 != null)
                {
                    ImGui.Text($"职业：{我.ClassJob.Value.Name}（{Data.PlayerCurrentLevel} 级）");
                    ImGui.TextDisabled($"  血量 {我.CurrentHp * 100.0 / Math.Max(1, 我.MaxHp):F0}%" +
                                       $"｜蓝 {我.CurrentMp}");
                }
            }
            catch { }

            try
            {
                // [!] 空中单独显示（用户反馈"原地跳跃应该也是移动状态"）：
                //     框架的 `IsMoving` 只看 `AgentMap.IsPlayerMoving`，
                //     **跳跃不改变它** —— 所以跳跃时这里"移动=否"是对的，
                //     但"空中=是"必须能看见，否则没法验证那个修复。
                var 空中 = 空中检测.在空中;
                var 文本 = $"战斗：{(Core.Me.InCombat() ? "战斗中" : "未战斗")}" +
                           $"｜移动：{(SpellUtil.在移动() ? "是" : "否")}" +
                           $"｜空中：{(空中 ? "是（读条放不出）" : "否")}";

                if (空中)
                    ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), "  " + 文本);
                else
                    ImGui.Text("  " + 文本);

                ImGui.TextDisabled($"    移动判定：{SpellUtil.移动状态描述()}");
            }
            catch { }

            try
            {
                ImGui.TextDisabled($"时间轴：{TimelineManager.状态摘要()}");
                ImGui.TextDisabled($"  在跑={TimelineManager.时间轴在跑()}" +
                                   $"｜条目={TimelineManager.条目数}" +
                                   $"｜机制={TimelineManager.机制总数()}");
            }
            catch { }

            ImGui.Separator();
        }
        catch { }
    }

    // ==================== 读条（本轮新增的机制身份）====================

    private static void 画读条()
    {
        try
        {
            var 全 = 机制读条.当前();

            if (!ImGui.CollapsingHeader($"正在读条（{全.Count}）", ImGuiTreeNodeFlags.DefaultOpen))
                return;

            if (全.Count == 0)
            {
                ImGui.TextDisabled("  当前没有敌人在读条");
                // [!] 说清"没有读条" ≠ "没有机制" ——
                //     瞬发机制看不到读条。这句话能避免用户误判。
                ImGui.TextDisabled("  （瞬发机制没有读条，这里看不到，仍靠时间轴）");
                ImGui.Separator();
                return;
            }

            foreach (var r in 全)
            {
                var 名 = string.IsNullOrEmpty(r.名) ? ("未知技能" + r.技能Id) : r.名;

                // 快落地的标红 —— 和"还有时间"区分开
                if (r.剩余 <= 2.5f)
                    ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f),
                        $"  {r.施法者名} → {名}  {r.剩余:F1}s");
                else
                    ImGui.Text($"  {r.施法者名} → {名}  {r.剩余:F1}s");

                ImGui.TextDisabled($"      id {r.技能Id}｜总长 {r.总长:F1}s｜已读 {r.已读:F1}s" +
                                   $"｜射程 {(int)r.射程}m");
                ImGui.TextDisabled($"      官表={(r.有权威数据 ? "有" : "无")}" +
                                   $"｜时间轴={(r.在时间轴 ? $"有(第 {r.时间轴次数} 次)" : "无")}" +
                                   $"｜可能打我={(r.可能打我 ? "是" : "否")}");
            }

            ImGui.TextDisabled($"  机制数值表：{机制数值表.条数} 条");
            ImGui.Separator();
        }
        catch { }
    }

    // ==================== 预测 ====================

    private static void 画预测()
    {
        try
        {
            if (!ImGui.CollapsingHeader("伤害预测 / 坦克压力")) return;

            ImGui.TextWrapped($"预测：{伤害预测.状态描述()}");
            // [!] 这几个都要传"未来几秒"。统一用 4 秒 ——
            //     和项目里其它调用点一致（`要预铺` 的默认值就是 4）。
            const float 展望秒 = 4f;
            ImGui.TextDisabled($"  未来 {展望秒:F0} 秒：" +
                               $"预计掉血人数={伤害预测.预计掉血人数(展望秒)}" +
                               $"｜要预铺={伤害预测.要预铺(展望秒)}" +
                               $"｜预计机制伤害={伤害预测.预计机制伤害(展望秒):P0}");

            try
            {
                var 危 = 伤害预测.最危险(4f);
                if (危 != null)
                    ImGui.TextDisabled($"  最危险：{危.Name}（{危.有效血量比例():P0}）");
            }
            catch { }

            // [!] 诊断串是"为什么是 0"的答案 ——
            //     预测返回 0 时要能看出是"真没伤害"还是"没有数据"。
            try
            {
                var 诊 = 伤害预测.最近诊断;
                if (!string.IsNullOrWhiteSpace(诊))
                    ImGui.TextDisabled($"  诊断：{诊}");
            }
            catch { }

            ImGui.Separator();

            // [!] `状态描述()` **自带**"坦克压力："前缀 —— 再加一个会重复
            ImGui.TextWrapped(坦克压力.状态描述());

            // [!] `波动很大` / `波动中等` 是**方法不是属性** ——
            //     漏括号会打印出 `System.Func`1[System.Boolean]`（用户实测截到）。
            ImGui.TextDisabled($"  波动={坦克压力.波动幅度:P0}" +
                               $"（很大={坦克压力.波动很大()} 中等={坦克压力.波动中等()}）" +
                               $"｜最高={坦克压力.最高血量:P0} 最低={坦克压力.最低血量:P0}");
            ImGui.TextDisabled($"  无敌中={坦克压力.无敌中}｜有减伤={坦克压力.有减伤}" +
                               $"｜命中的减伤={坦克压力.命中的减伤}");

            ImGui.Separator();
        }
        catch { }
    }

    // ==================== 技能读取 ====================

    private static void 画技能()
    {
        try
        {
            if (!ImGui.CollapsingHeader("技能读取 / 职业表")) return;

            var 表 = HealRotationEventHandler.取当前职业技能表();
            if (表 == null)
            {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f),
                    "  拿不到当前职业技能表 —— 技能相关功能全部失效");
                ImGui.Separator();
                return;
            }

            ImGui.TextDisabled($"  移动：{SpellUtil.移动状态描述()}");

            // 关键栏位逐个查"解锁了吗 / 可用吗"
            void 一行(string 标签, uint id)
            {
                if (id == 0)
                {
                    ImGui.TextDisabled($"  {标签}：未配置");
                    return;
                }
                能放(标签, id);
            }

            一行("基础输出", 表.基础输出);
            一行("群体输出", 表.群体输出);
            一行("DoT", 表.Dot技能);
            一行("复活", 表.复活);
            一行("驱散", 表.驱散);

            // ── 治疗候选表：列"现在能用几个" + **为什么不能用的** ──
            //
            //  [!] 为什么不只给个数（用户实测）：
            //      截图显示"解锁 5 个，可用 0 个"，但**看不出原因** ——
            //      `SpellUtil.可用()` 只返回 bool，不返回理由。
            //      而"可用 0 个"直接导致治疗候选为空、AI 退回技能 ID 模式，
            //      是这一整类问题的源头 => 必须能一眼看出卡在哪。
            //
            //  [!] 每个属性都要单独 try —— 框架在非战斗时读某些属性会抛。
            try
            {
                var 候选 = 表.治疗候选.已解锁();
                var 能用 = 0;
                var 不能用明细 = new System.Text.StringBuilder();

                foreach (var 技 in 候选)
                {
                    var 可 = false;
                    try { 可 = SpellUtil.可用(技.Id); } catch { }
                    if (可) { 能用++; continue; }

                    // ── 不能用 -> 查原因 ──
                    var 原因 = new System.Text.StringBuilder();
                    try
                    {
                        var sp = SpellUtil.Get(技.Id);
                        if (sp == null) 原因.Append("读不到");

                        if (sp != null)
                        {
                            // 未解锁
                            try { if (!sp.IsUnlock()) 原因.Append("未解锁 "); } catch { }
                            // 冷却
                            try
                            {
                                var cd = sp.Cooldown;
                                if (cd > TimeSpan.Zero) 原因.Append($"CD{cd.TotalSeconds:F1}s ");
                            }
                            catch { }
                            // 移动（读条技）
                            try
                            {
                                if (技.咏唱 > 0f && !SpellUtil.移动中能放(技.Id))
                                    原因.Append("移动中 ");
                            }
                            catch { }
                            // 正在读条
                            try
                            {
                                if (Core.Me.IsCasting) 原因.Append("在读条 ");
                            }
                            catch { }
                            // 资源
                            try
                            {
                                if (!sp.IsReadyWithCanCast() && 原因.Length == 0)
                                    原因.Append("CanCast失败（资源/条件）");
                            }
                            catch { 原因.Append("CanCast抛异常 "); }
                        }
                    }
                    catch { 原因.Append("查询异常"); }

                    if (原因.Length == 0) 原因.Append("未知");
                    if (不能用明细.Length < 150)
                        不能用明细.Append(技.名).Append('[').Append(原因.ToString().Trim()).Append("] ");
                }

                if (能用 > 0 || 候选.Count == 0)
                    ImGui.TextDisabled($"  治疗候选：解锁 {候选.Count} 个，其中现在可用 {能用} 个");
                else
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f),
                        $"  治疗候选：解锁 {候选.Count} 个，可用 0 个 —— {不能用明细.ToString().Trim()}");
            }
            catch { }

            // ★ 本地治疗选择（诊断）★
            //   [!] 用户实测的疑点：日志里 AI 说"用能力技单奶自己"，
            //      但实际一直看到医术 —— 分不清是 AI 选了、还是本地选了。
            //      这一行直接给出答案：`选[医术]=1.23｜候选：医术=1.23 鼓舞激励之策=1.10`
            try
            {
                var 选 = HealerACR.Common.治疗决策.最近选择;
                if (!string.IsNullOrWhiteSpace(选))
                    ImGui.TextDisabled($"  本地治疗选择：{选}");
            }
            catch { }

            // ── Dot 补判状态 ──
            //   [!] 用**当前选中目标**来问"该补吗" —— 那是 DoT 真正会打的人。
            try
            {
                var 目标 = HealTargetHelper.当前目标();
                var 该 = false;
                try
                {
                    // [!] 第二个参数是 `uint[]` —— 用 `表.所有DotBuff`
                    //     （`DotBuff` 是单个 uint，形状不对）
                    该 = 目标 != null && Dot补判.该补(目标, 表.所有DotBuff);
                }
                catch { }

                ImGui.TextDisabled($"  DoT 补判：距上次 {Dot补判.距上次毫秒}ms" +
                                   $"｜目标={目标?.Name.ToString() ?? "无"}" +
                                   $"｜该补={该}");
            }
            catch { }

            // ── 效果确认（命中确认挂钩是否正常）──
            //   [!] `是否确认命中` 要传技能 Id —— 它问的是"这个技能刚确认命中了没"。
            //     这里用基础输出当探针（它是最常放的技能）。
            try
            {
                var 探针 = 表.基础输出;
                ImGui.TextDisabled($"  效果确认：已挂载={效果确认.已挂载}" +
                                   $"｜挂载失败过={效果确认.挂载失败过}" +
                                   $"｜基础输出已确认={效果确认.是否确认命中(探针)}");
            }
            catch { }

            ImGui.Separator();
        }
        catch { }
    }

    /// <summary>画一行技能状态（名字 / 解锁 / 可用 / 距离）</summary>
    private static void 能放(string 标签, uint id)
    {
        try
        {
            var 名 = SpellIds.反查(id);
            var 解锁 = false; var 可用 = false;
            try { 解锁 = SpellUtil.已解锁(id); } catch { }
            try { 可用 = SpellUtil.可用(id); } catch { }

            // 颜色区分"能不能放" —— 一眼看出问题在哪
            var 色 = !解锁 ? new Vector4(0.6f, 0.6f, 0.6f, 1f)      // 灰 = 没解锁
                   : !可用 ? new Vector4(1f, 0.7f, 0.3f, 1f)        // 橙 = 解锁但不可用（CD/资源）
                   : new Vector4(0.5f, 1f, 0.5f, 1f);               // 绿 = 可用

            ImGui.TextColored(色, $"  {标签}：{id} {名}" +
                                  $"｜解锁={解锁} 可用={可用}");
        }
        catch { }
    }

    // ==================== AI 各层状态 ====================

    private static void 画AI()
    {
        try
        {
            if (!ImGui.CollapsingHeader("AI 各层状态")) return;

            var s = AiSettings.Instance;

            ImGui.TextDisabled($"  配置：Key={(!string.IsNullOrEmpty(s.ApiKey) ? "有" : "无")}" +
                               $"｜模型={s.Model}｜策略层={s.启用策略层}｜决策层={s.启用决策层}");
            ImGui.TextDisabled($"  通道：{DeepSeekClient.通道摘要()}");
            ImGui.TextDisabled($"  缓存：{DeepSeekClient.缓存描述()}");

            ImGui.Separator();

            // ── 策略层 ──
            ImGui.Text($"  策略层：倾向={AiStrategyLayer.当前倾向}" +
                       $"｜刷新中={AiStrategyLayer.刷新中}" +
                       $"｜成功={AiStrategyLayer.成功次数} 次");
            if (!string.IsNullOrWhiteSpace(AiStrategyLayer.说明))
                ImGui.TextDisabled($"      说明：{AiStrategyLayer.说明}");

            // ── 决策层 ──
            ImGui.Text($"  决策层：队列={AiDecisionLayer.队列长度}" +
                       $"｜预取中={AiDecisionLayer.预取中}" +
                       $"｜命中={AiDecisionLayer.命中次数} 过期={AiDecisionLayer.过期次数}");
            ImGui.TextDisabled($"      预取 成功={AiDecisionLayer.预取成功次数}" +
                               $" 失败={AiDecisionLayer.预取失败次数}" +
                               $"｜解析失败={AiDecisionLayer.解析失败次数}" +
                               $"｜幻觉丢弃={AiDecisionLayer.丢弃幻觉次数}");
            // ★ AI 幻觉校验 —— 它提到过当前等级用不了的技能吗（用户要求）★
            //   [!] 这是把"AI 又编了"变成**可量化数字**的唯一手段 ——
            //      提示词约束无法在执行层验证（AI 的输出是自由文本）。
            ImGui.TextDisabled($"      幻觉校验：{幻觉校验.状态描述()}");

            ImGui.TextDisabled($"      局面剧变清空={AiDecisionLayer.清空次数} 次" +
                               $"（丢 {AiDecisionLayer.清空丢弃条数} 条）" +
                               $"｜格式噪声={AiDecisionLayer.格式噪声次数}");

            try
            {
                var 拦 = AiDecisionLayer.拦截摘要();
                if (!string.IsNullOrWhiteSpace(拦))
                    ImGui.TextDisabled($"      拦截：{拦}");
            }
            catch { }

            // ── 局面监控 ──
            ImGui.TextDisabled($"  局面监控：{局面监控.状态描述()}");

            ImGui.Separator();

            // ── 候选集（当前快照长什么样）──
            //   [!] 这是本轮最重要的可见项：候选**实际**有没有输出类、
            //      有没有复活 —— 分桶改造的效果一眼可见。
            try
            {
                var 快 = 候选集.生成();
                ImGui.Text($"  候选快照 #{快.快照Id}（{快.候选表.Count} 个）");

                if (快.候选表.Count == 0)
                {
                    ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f),
                        "      候选为空 —— AI 会退回技能 ID 模式");

                    // ★ 说清**是哪一步归零的** ★
                    //   [!] 只显示"0 个"等于没线索 —— 四个生成器各有多个提前
                    //      return（拿不到技能表 / 没队友受伤 / 没敌人 / 减伤没配置…），
                    //      必须能当场看出卡在哪。
                    ImGui.TextDisabled("      诊断：" + 快.生成诊断);
                }
                else
                {
                    // 有候选时也把诊断挂一行（看各生成器的贡献）
                    ImGui.TextDisabled("      " + 快.生成诊断);
                }

                foreach (var c in 快.候选表)
                {
                    var 类色 = c.类 switch
                    {
                        候选集.类别.紧急 => new Vector4(1f, 0.45f, 0.35f, 1f),
                        候选集.类别.功能 => new Vector4(1f, 0.85f, 0.4f, 1f),
                        候选集.类别.减伤 => new Vector4(0.6f, 0.8f, 1f, 1f),
                        候选集.类别.治疗 => new Vector4(0.5f, 1f, 0.6f, 1f),
                        _ => new Vector4(0.75f, 0.75f, 0.75f, 1f),
                    };

                    // ★ 显示"技能往哪放"和"为什么选它" ★
                    //   [!] 这两件事原来挤在一个字段里，导致群疗被以队友为中心放。
                    //      分开展示之后，群疗应该长这样：
                    //          C1 [治疗] 医治 施法→自己（关注 坦克甲 缺口58%）
                    var 施法目标文本 = c.施法目标模式 switch
                    {
                        候选集.目标模式.自己 => "自己",
                        候选集.目标模式.地面 => "地面",
                        _ => c.目标Id != 0 ? c.目标名 : "?",
                    };

                    var 关注 = string.IsNullOrEmpty(c.关注目标名)
                        ? ""
                        : $"（关注 {c.关注目标名}）";

                    ImGui.TextColored(类色,
                        $"    {c.编号} [{c.类}] {c.技能名}" +
                        $" 施法→{施法目标文本}{关注}" +
                        (c.能力技 ? " 能力" : " GCD"));

                    var 紧急 = c.紧急等级 > 0 ? $"｜紧急={c.紧急等级}" : "";
                    ImGui.TextDisabled($"        量={c.量} 即时={c.即时治疗}" +
                                       $" 3s={c.预测3秒} 6s={c.预测6秒}" +
                                       $"｜缺口={c.缺口:P0} 过量={c.过量:P0}" +
                                       $"｜分={c.本地分:F2}{紧急}");
                }
            }
            catch { }

            ImGui.Separator();
        }
        catch { }
    }
}
