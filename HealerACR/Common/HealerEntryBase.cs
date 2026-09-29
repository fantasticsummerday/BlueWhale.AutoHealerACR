using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.CombatRoutine.View.JobView;
using HealerACR.Opener;
using HealerACR.Timeline;
using Dalamud.Bindings.ImGui;

namespace HealerACR.Common;

/// <summary>
/// 四个职业入口类的公共部分。
///
/// AEAssist 3.0 的 IRotationEntry 只有 4 个成员：
///   string AuthorName { get; set; }
///   Rotation Build(string settingFolder)
///   IRotationUI GetRotationUI()
///   void OnDrawSetting()
/// 再加上 IDisposable。等级 / 职业 / 描述是通过 Build 里构造的 Rotation 设置的。
/// </summary>
public abstract class HealerEntryBase : IRotationEntry
{
    // ==================== 身份信息 ====================

    public string AuthorName { get; set; } = "小鲸鱼";

    /// <summary>日随从 1 级开始，一路到 100 级</summary>
    public int MinLevel { get; set; } = 1;
    public int MaxLevel { get; set; } = 100;

    public abstract Jobs TargetJob { get; }

    /// <summary>QT 面板标题</summary>
    public abstract string OverlayTitle { get; }

    public abstract string Description { get; }

    /// <summary>职业技能表：本职业所有技能 ID 的唯一来源</summary>
    public abstract JobSpellTable Spells { get; }

    /// <summary>
    /// ACR 类型 —— 决定在 AEAssist 的 ACR 列表里怎么归类。
    ///
    /// **本项目定位是日随**（四人本为主、随机副本、省心优先），
    /// 所以默认 `Normal`（只显示"日常"），不是 `Both`。
    ///
    /// 改成 `Both` 会让它在"高难"分类里也出现 ——
    /// 但我们没有针对高难做优化（没有精细的减伤轴编排、
    /// 没有 Trigger 条件体系），挂个"高难"标签容易误导。
    ///
    /// 子类可以覆盖（比如以后真做了高难特化版）。
    /// </summary>
    public AcrType AcrType { get; set; } = AcrType.Normal;

    // ==================== UI ====================

    /// <summary>QT 窗口。所有 resolver 通过 HealerACR.Common.HealQt 读开关。</summary>
    public JobViewWindow? 视图窗口 { get; protected set; }

    // ==================== 核心 ====================

    /// <summary>子类只需要给出决策队列，剩下的都在这</summary>
    protected abstract List<SlotResolverData> 构建决策队列();

    public virtual Rotation Build(string settingFolder)
    {
        // 1. 读设置（每个职业一份 json）
        HealSettings.Build(settingFolder, TargetJob.ToString());

        // 2. 扫一遍 cactbot 时间轴目录
        TimelineManager.初始化();

        // 3. 构建 QT 面板
        构建QT();

        // 4. 组装 Rotation
        var rotation = new Rotation(构建决策队列())
        {
            TargetJob = TargetJob,
            AcrType = AcrType,
            MinLevel = MinLevel,
            MaxLevel = MaxLevel,
            Description = Description,
        };

        var rot = rotation
            .SetRotationEventHandler(new HealRotationEventHandler())
            // 日随用的"起手"其实只会做开怪倒计时预铺，序列是空的
            .AddOpener(level => new 预铺起手(Spells))
            // ── Trigger Action（参考同类 ACR 用的 AddTriggerAction）──
            //   让"铺减伤 / 攒资源"能从 AEAssist 的时间轴编辑器里直接触发。
            //   两者并存：轮询还在跑，Trigger 是额外的触发源。
            .AddTriggerAction(new AEAssist.CombatRoutine.Trigger.ITriggerAction[]
            {
                new 减伤触发器(),
                new 攒资源触发器(),
            })
            // ── Trigger Condition（参考同类 ACR 用的 AddTriggerCondition）──
            //   时间轴编辑器里可选的"条件"，配合 Action 用。
            .AddTriggerCondition(new AEAssist.CombatRoutine.Trigger.ITriggerCond[]
            {
                new 血量低于条件(),
                new 死亡人数条件(),
                new 资源条件(),
            });

        // ── 爆发轴（参考同类 ACR 用的 AddSlotSequences）──
        //   爆发期按固定套路走，不被优先级逻辑打断。
        //   **默认关闭** —— 爆发轴会占 GCD，日随里不一定划算。
        try
        {
            var 轴 = 构建爆发轴();
            if (轴 != null && 轴.Length > 0) rot.AddSlotSequences(轴);
        }
        catch { }

        return rot;
    }

    /// <summary>
    /// 子类返回自己的爆发轴（返回空数组 = 不用）。
    ///
    /// 爆发轴的意义：爆发期有固定套路，不该被优先级逻辑打断 ——
    /// 比如连环计刚放完，优先级队列可能因为"有人掉血"就转头去治疗。
    /// </summary>
    protected virtual AEAssist.CombatRoutine.Module.ISlotSequence[] 构建爆发轴()
        => Array.Empty<AEAssist.CombatRoutine.Module.ISlotSequence>();

    /// <summary>把开关暴露到 QT 面板。子类可以 override 之后往里面加职业专属开关。</summary>
    protected virtual void 构建QT()
    {
        // 把技能表注入给事件处理类 —— 它是独立类，拿不到入口类的 Spells 属性。
        // AfterSpell 里做单插控制要用到（判断"复活"用掉了即刻）。
        HealRotationEventHandler.当前技能表 = Spells;
        // ★ 同时按职业登记 ★ —— 否则 5 个入口会互相覆盖，
        //   最后加载的那个（幻术师）赢，导致 AI 看到错误的技能清单。
        HealRotationEventHandler.登记技能表((uint)TargetJob, Spells);

        // 尝试挂 ActionEffect hook（默认关闭；挂不上会降级，不影响其他功能）
        效果确认.尝试挂载();
        var s = HealSettings.Instance;

        // ══════════════════════════════════════════════════════════════
        //  ★ QT 精简：把"不需要经常动"的开关默认隐藏起来 ★
        //
        //    原来面板上有 24 个开关，日随根本用不上这么多 ——
        //    开关太多反而让人不知道该看哪个。
        //
        //    做法用 AEAssist 自带的机制：`JobViewSave.QtUnVisibleList`。
        //    隐藏的开关**依然生效**（保持默认值），只是不出现在主面板上；
        //    真想调的时候，在 QT 控制台里能看到全部。
        //
        //    保留可见的 9 个 = 日随里最可能想动的：
        //      奶人 / 输出 / 减伤 / 时间轴     ← 大方向
        //      复活 / 驱散 / 群盾              ← 关键功能的开关
        //      一键爆发                        ← 新功能，用户会想试
        //      解除熔断                        ← 要绑快捷键，必须可见
        //
        //    其余全部隐藏（默认值都是合理的，不需要用户操心）。
        // ══════════════════════════════════════════════════════════════
        应用QT精简(s);

        视图窗口 = new JobViewWindow(s.职业视图保存, s.保存回调, OverlayTitle);

        // ── 加一个「优先级」页签（参考同类 ACR 的 JobPriorityUI）──
        //   决定血线接近时先救谁。用 JobViewWindow.AddTab 挂进去。
        try
        {
            视图窗口.AddTab("优先级", _ => 职业优先级.画());
        }
        catch { }

        void 加开关(string 名称, bool 默认)
        {
            视图窗口.AddQt(名称, 默认);
            HealQt.注册默认值(名称, 默认);
        }

        加开关("奶人", s.奶人);
        加开关("单奶", s.单体治疗);
        加开关("群奶", s.群体治疗);
        加开关("复活", s.复活);
        加开关("驱散", s.驱散);
        加开关("醒梦", s.醒梦);
        加开关("输出", s.输出);
        加开关("AOE", s.AOE);
        加开关("DOT", s.挂Dot);
        加开关("减伤", true);
        加开关("群盾", false);
        加开关("时间轴", true);
        加开关("能量吸收", true);    // 参考实现：允许卸豆换输出
        加开关("小怪卸豆", true);    // 参考实现：非 Boss 目标放手卸
        加开关("强制以太", false);   // 参考实现：手动强制补，默认关
        // ---- 小仙女系统（参考实现的开关命名）----
        // ⚠️ 「自动召唤」这个开关删掉了 —— 它注册了但**没有任何地方读它**，
        //    学者实际用的是职业专属开关「小仙女」（在 ScholarACR 里注册）。
        //    留着一个不起作用的开关只会让用户困惑："我勾了怎么没反应？"
        加开关("自动转化", false);   // 牺牲小仙女换 3 颗以太（默认关，有代价）
        加开关("脚下放罩", false);   // 地面减伤放自己脚下（不勾则放目标处）

        // ══════════════════════════════════════════════════════════════
        //  ⚠️ 下面这些是"被 resolver 读取了、但一直没注册"的开关。
        //
        //    没注册的后果：GetQt 查不到 → 落到**调用方的兜底参数** →
        //      ① 用户在面板上看不到它，改不了
        //      ② 如果兜底是 false，**功能直接静默失效**
        //
        //    最严重的是「应急」—— 它读取时写的兜底是 false，
        //    意味着**紧急治疗从来没触发过**。
        //
        //    教训：新增任何 GetQt("XXX") 调用，都必须在这里补一行注册。
        //          （这个坑我是靠"交叉检查读取 vs 注册"扫出来的，
        //           肉眼绝对看不出来 —— 因为代码不报错，只是什么也不做。）
        // ══════════════════════════════════════════════════════════════
        加开关("HoT", true);         // 再生 / 吉星相位 这类单体 HoT
        加开关("应急", true);        // ★ 紧急治疗 —— 之前默认 false 导致从未生效
        加开关("脱战准备", true);    // 脱战期提前攒资源
        加开关("极限技", true);      // 治疗极限技（LB3）
        加开关("自动疾跑", true);    // 脱战自动疾跑
        加开关("自动以太", true);    // 学者：以太不够时自动补
        // ★ 「一键爆发」★ —— 合并了原来的「爆发轴」和「木桩爆发」
        //   勾上 = 战斗或木桩，只要条件满足就走爆发轴
        //   默认关（爆发轴会占 GCD，日随里不一定划算）
        加开关("一键爆发", false);
        // ★ 解除熔断 ★ —— HealQt 只提供 bool 开关，
        //   但 AEAssist 的 QT 控制台支持给开关绑快捷键 ——
        //   所以在 QT 面板里给这个开关设个键，就等于有了"解除熔断"快捷键。
        //   勾上即解除，下一帧自动弹回（见 HealQt.每帧更新）。
        加开关("解除熔断", false);

        // ★ 爆发药 ★ —— 默认关。
        //   药水 ID 不在这里填 —— 用 AEAssist 自带的「爆发药设置」，
        //   所以只判断"有没有配 + 够不够 + CD 好没好"。
        //   实际消耗点见 爆发轴.cs 的 使用爆发药()（挂在爆发轴第一步）。
        加开关("爆发药", false);

        // 每个职业自己的页（参考同类 ACR 的 XXXOverlay / XXXSettingView 做法）
        视图窗口.AddTab("职业", w => 职业面板.画(TargetJob, w));
        // 通用页：阈值 + 时间轴
        视图窗口.AddTab("阈值", 画阈值设置);

        HealQt.绑定(视图窗口);
    }

    /// <summary>子类加职业专属开关时用这个，顺带把默认值登记上</summary>
    protected void 加职业开关(string 名称, bool 默认)
    {
        视图窗口?.AddQt(名称, 默认);
        HealQt.注册默认值(名称, 默认);
    }

    /// <summary>
    /// 保留在 QT 面板上的开关（其余默认隐藏）。
    ///
    /// **挑选标准**：日随里用户可能真的想改的。
    /// 那些"设一次就不用管"的（比如 HoT、脱战准备）全部藏起来 ——
    /// 它们的默认值就是合理值，摆在那里只会让人困惑。
    /// </summary>
    private static readonly string[] 可见开关 =
    {
        "奶人",         // 大方向：治不治
        "输出",         // 大方向：打不打
        "减伤",         // 大方向：铺不铺
        "时间轴",       // 大方向：跟不跟时间轴
        "复活",         // 关键功能
        "驱散",         // 关键功能
        "群盾",         // 默认关，用户会想开
        "一键爆发",     // 默认关，用户会想试
        "解除熔断",     // 要绑快捷键，必须可见
    };

    /// <summary>
    /// 把不在白名单里的开关加进隐藏列表。
    ///
    /// 用 `JobViewSave.QtUnVisibleList` —— AEAssist 自带的机制。
    /// **隐藏 ≠ 关闭**：它们保持默认值继续工作，
    /// 只是在主面板上不显示；QT 控制台里依然能看到全部。
    /// </summary>
    private static void 应用QT精简(HealSettings s)
    {
        try
        {
            var 列表 = s.职业视图保存.QtUnVisibleList;
            if (列表 == null) return;

            // ⚠️ **只在用户从没设过的时候应用一次。**
            //
            //    如果每次都 Clear + 重填，用户在 QT 控制台里
            //    手动显示/隐藏的开关会被我覆盖掉 —— 那是很讨厌的行为。
            //
            //    判定方式：列表非空 = 用户动过（或者上次已经初始化过）→ 不碰。
            if (列表.Count > 0) return;

            // 全部开关名（和 构建QT() 里的注册保持一致）
            var 全部 = new[]
            {
                "奶人", "单奶", "群奶", "复活", "驱散", "醒梦",
                "输出", "AOE", "DOT", "减伤", "群盾", "时间轴",
                "能量吸收", "小怪卸豆", "强制以太",
                "自动转化", "脚下放罩",
                "HoT", "应急", "脱战准备", "极限技",
                "自动疾跑", "自动以太", "一键爆发", "解除熔断", "爆发药",
                // 职业专属（各职业注册各自的，这里一并列上，没注册的无害）
                "以太超流", "链式策略", "小仙女", "炽天使",
                "神速魔", "光速", "占卜", "抽卡", "地星",
                "自动心关", "根素", "箭毒", "发炎",
            };

            列表.Clear();

            foreach (var 名 in 全部)
            {
                if (Array.IndexOf(可见开关, 名) >= 0) continue;   // 保留可见
                列表.Add(名);
            }

            LogHelper.Info($"[HealerACR] QT 精简：可见 {可见开关.Length} 个，" +
                           $"隐藏 {列表.Count} 个（隐藏的依然生效，可在 QT 控制台调）");
        }
        catch (Exception e)
        {
            LogHelper.Info("[HealerACR] QT 精简失败（不影响功能）：" + e.Message);
        }
    }


    /// <summary>
    /// 阈值面板。JobViewWindow 自带的只有 bool 开关，滑条得自己用 ImGui 画。
    /// 改完直接生效；点"保存"或者关游戏前记得落盘。
    /// </summary>
    protected virtual void 画阈值设置(JobViewWindow window)
    {
        var s = HealSettings.Instance;

        ImGui.Text("治疗阈值（0~1，调低更省蓝，调高更稳）");
        ImGui.Separator();

        ImGui.SetNextItemWidth(240);
        ImGui.SliderFloat("紧急单奶阈值", ref s.紧急单奶阈值, 0.05f, 0.90f, "%.2f");
        ImGui.SetNextItemWidth(240);
        ImGui.SliderFloat("单体治疗阈值", ref s.单体治疗阈值_基础, 0.10f, 1.00f, "%.2f");
        ImGui.SetNextItemWidth(240);
        ImGui.SliderFloat("群体治疗阈值", ref s.群体治疗阈值_基础, 0.10f, 1.00f, "%.2f");
        ImGui.SetNextItemWidth(240);
        ImGui.SliderInt("群奶最少人数", ref s.群奶最少人数, 1, 4);

        ImGui.Separator();
        ImGui.SetNextItemWidth(240);
        ImGui.SliderInt("醒梦蓝量阈值", ref s.醒梦蓝量阈值, 1000, 10000);
        ImGui.SetNextItemWidth(240);
        ImGui.SliderFloat("不挂 DoT 血线", ref s.不挂Dot血线, 0f, 0.20f, "%.2f");

        ImGui.Separator();
        ImGui.Checkbox("自动减伤（boss 读条时自动铺）", ref s.自动减伤);
        ImGui.Checkbox("允许硬读复活（不推荐）", ref s.允许硬读复活);
        ImGui.Checkbox("拉人喊话", ref s.复活喊话开关);

        // ---------------- 时间轴 ----------------
        ImGui.Separator();
        ImGui.Text("cactbot 时间轴");
        ImGui.TextDisabled("状态：" + TimelineManager.状态);
        ImGui.Checkbox("启用时间轴", ref s.启用时间轴);
        ImGui.SetNextItemWidth(240);
        ImGui.SliderFloat("提前秒数", ref s.时间轴提前秒, 0f, 5f, "%.1f");
        ImGui.TextDisabled("额外技能Id / 技能Id覆盖 / BuffId覆盖 都改 json 文件（这里只读）");

        ImGui.Separator();
        if (ImGui.Button("保存到 json"))
        {
            s.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("（改完记得点一下）");
    }

    public virtual IRotationUI GetRotationUI() => 视图窗口!;

    public virtual void OnDrawSetting()
    {
        // AEAssist 主界面「ACR 设置」标签的内容。
        //
        // 之前这里是空的 —— 用户点进来看到一片空白（虽然设置其实都在
        // QT 面板的「职业」页里，但那个入口不好找）。
        //
        // 这里把全部设置按用途分组画出来，和 QT 面板互补：
        //   · 这里 = 完整设置（含阈值、保留数等数值项）
        //   · QT 面板职业页 = 开关 + 实时资源显示
        var s = HealSettings.Instance;
        if (s == null)
        {
            ImGui.TextDisabled("设置还没加载完成，稍后再打开");
            return;
        }

        ImGui.TextDisabled("提示：按职业区分的开关在 QT 面板的「职业」页里，这里放通用设置。");
        ImGui.Separator();

        if (ImGui.CollapsingHeader("治疗", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Checkbox("奶人", ref s.奶人);
            ImGui.SliderFloat("紧急单奶阈值", ref s.紧急单奶阈值, 0.1f, 1f, "%.2f");
            ImGui.SliderFloat("单体治疗阈值", ref s.单体治疗阈值_基础, 0.1f, 1f, "%.2f");
            ImGui.SliderFloat("群体治疗阈值", ref s.群体治疗阈值_基础, 0.1f, 1f, "%.2f");
            ImGui.SliderInt("群奶最少人数", ref s.群奶最少人数, 1, 8);
            ImGui.Checkbox("单体治疗", ref s.单体治疗);
            ImGui.Checkbox("群体治疗", ref s.群体治疗);
            ImGui.Checkbox("复活", ref s.复活);
            ImGui.Checkbox("允许硬读复活", ref s.允许硬读复活);
            ImGui.TextDisabled("  没即刻时也硬读复活（会占用很长时间，慎开）");
            ImGui.Checkbox("驱散", ref s.驱散);
        }

        if (ImGui.CollapsingHeader("输出"))
        {
            ImGui.Checkbox("输出", ref s.输出);
            ImGui.Checkbox("AOE", ref s.AOE);
            ImGui.Checkbox("挂 Dot", ref s.挂Dot);
            ImGui.SliderFloat("不挂 Dot 血线", ref s.不挂Dot血线, 0f, 1f, "%.2f");
            ImGui.TextDisabled("  目标血量低于这个值就不浪费 GCD 挂 Dot");
            ImGui.SliderFloat("Dot 持续时间", ref s.Dot持续时间, 3f, 30f, "%.0f 秒");
            ImGui.Checkbox("木桩优先输出", ref s.木桩优先输出);
            ImGui.TextDisabled("  木桩环境下走该职业最优输出策略");
        }

        if (ImGui.CollapsingHeader("减伤 / 时间轴"))
        {
            ImGui.Checkbox("自动减伤", ref s.自动减伤);
            ImGui.Checkbox("启用时间轴", ref s.启用时间轴);
            ImGui.SliderFloat("时间轴提前秒", ref s.时间轴提前秒, 0f, 15f, "%.1f 秒");
            ImGui.Checkbox("时间轴攒资源", ref s.时间轴攒资源);
            ImGui.TextDisabled("  未来几秒有减伤需求时，先攒资源不拿去输出");
            ImGui.Separator();
            ImGui.Checkbox("启用效果确认", ref s.启用效果确认);
            ImGui.TextDisabled("  ⚠️ hook 游戏的 ActionEffect 回调（高风险，默认关）");
            ImGui.TextDisabled("  挂不上会自动降级，不影响其他功能");
        }

        if (ImGui.CollapsingHeader("职业资源"))
        {
            ImGui.Checkbox("醒梦", ref s.醒梦);
            ImGui.SliderInt("醒梦蓝量阈值", ref s.醒梦蓝量阈值, 0, 10000);
            ImGui.Separator();
            ImGui.TextDisabled("白魔");
            ImGui.SliderFloat("百合使用血线", ref s.百合使用血线, 0.3f, 1f, "%.2f");
            ImGui.Checkbox("用苦难之心", ref s.用苦难之心);
            ImGui.Separator();
            ImGui.TextDisabled("学者 / 贤者");
            ImGui.SliderFloat("妖精契约血线", ref s.妖精契约血线, 0.3f, 1f, "%.2f");
            ImGui.SliderInt("以太保留数", ref s.以太保留数, 0, 3);
            ImGui.SliderInt("蛇胆保留数", ref s.蛇胆保留数, 0, 3);
            ImGui.SliderInt("箭毒泄刺阈值", ref s.箭毒泄刺阈值, 1, 3);
            ImGui.Separator();
            ImGui.TextDisabled("占星");
            ImGui.Checkbox("出卡优先近战", ref s.出卡优先近战);
            ImGui.SliderFloat("地星提前秒", ref s.地星提前秒, 0f, 10f, "%.1f 秒");
        }

        if (ImGui.CollapsingHeader("其他"))
        {
            ImGui.Checkbox("复活喊话", ref s.复活喊话开关);
            if (s.复活喊话开关)
            {
                ImGui.Indent();

                var 频道 = s.复活喊话频道 ?? "/p ";
                ImGui.SetNextItemWidth(80);
                if (ImGui.InputText("频道", ref 频道, 16)) s.复活喊话频道 = 频道;
                ImGui.SameLine();
                ImGui.TextDisabled("/p 队伍   /s 说话   /a 喊话");

                if (s.复活喊话 == null) s.复活喊话 = new List<string>();
                ImGui.TextDisabled("内容：<t> 会替换成被复活者名字；多条则随机挑一条");

                for (var i = 0; i < s.复活喊话.Count; i++)
                {
                    // InputText 需要 ref string，不能直接 ref 列表元素，先取局部变量再写回
                    var 文本 = s.复活喊话[i] ?? string.Empty;
                    ImGui.SetNextItemWidth(280);
                    ImGui.PushID("喊话" + i);
                    if (ImGui.InputText("##内容", ref 文本, 128)) s.复活喊话[i] = 文本;
                    ImGui.PopID();

                    ImGui.SameLine();
                    ImGui.PushID("删" + i);
                    if (ImGui.Button("删除")) { s.复活喊话.RemoveAt(i); break; }
                    ImGui.PopID();
                }

                if (ImGui.Button("加一条")) s.复活喊话.Add("制作\"<t>\"成功！");
                ImGui.SameLine();
                if (ImGui.Button("恢复默认"))
                {
                    s.复活喊话.Clear();
                    s.复活喊话.Add("制作\"<t>\"成功！");
                }

                var 预览 = s.复活喊话.Count > 0 ? s.复活喊话[0].Replace("<t>", "队友名字") : "";
                ImGui.TextDisabled("预览：" + (s.复活喊话频道 ?? "/p ") + 预览);

                ImGui.Unindent();
            }
        }
    }

    public virtual void Dispose()
    {
    }
}

/// <summary>
/// 战斗事件回调。日随用得到的主要是"战斗结束后把 QT 恢复默认"，
/// 否则上一把临时关掉的开关会带到下一把。
/// 另外这里挂着时间轴的推进。
/// </summary>
public class HealRotationEventHandler : IRotationEventHandler
{
    /// <summary>
    /// 当前职业的技能表，由入口类在 Build 时注入。
    /// 这个事件处理类是独立类，拿不到入口类的属性，所以用静态字段传。
    /// </summary>
    public static JobSpellTable? 当前技能表;

    /// <summary>
    /// **按职业存的技能表** —— 解决"最后加载的职业覆盖前面的"问题。
    ///
    /// ⚠️ 这是个真 bug（用户实测发现）：
    ///    BlueWhale 有 5 个入口（白魔/学者/占星/贤者/幻术师），
    ///    AEAssist 加载时会**依次调用每个的 Build**，
    ///    而每个 Build 都写同一个静态字段 `当前技能表` ——
    ///    **最后一个（幻术师）赢了**。
    ///
    ///    后果：玩学者时，AI 收到的"可选技能清单"是**幻术师的技能**，
    ///    于是它建议 `132 烈风` / `127 坚石` —— 学者根本没有这两个技能。
    ///
    ///    修法：**按职业 ID 存表**，取的时候用当前职业去查。
    /// </summary>
    private static readonly Dictionary<uint, JobSpellTable> _各职业技能表 = new();

    /// <summary>入口类 Build 时调：登记本职业的技能表</summary>
    public static void 登记技能表(uint 职业Id, JobSpellTable 表)
    {
        try
        {
            _各职业技能表[职业Id] = 表;
        }
        catch { }
    }

    /// <summary>
    /// 取**当前实际职业**的技能表。
    ///
    /// 优先按 `Core.Me.ClassJob` 查；查不到再退回旧的 `当前技能表`。
    /// </summary>
    public static JobSpellTable? 取当前职业技能表()
    {
        try
        {
            var 职业 = Core.Me.ClassJob.RowId;
            if (_各职业技能表.TryGetValue(职业, out var 表) && 表 != null) return 表;
        }
        catch { }

        return 当前技能表;   // 兜底
    }

    public Task OnPreCombat()
    {
        // 说明：本来想在这里挂"开怪倒计时"（预铺盾 + 吃爆发药），参考同类 ACR 的
        // Opener.InitCountDown，但撞了两个墙：
        //   1. AEAssist 的 CountDownHandler 是**实例**方法，找不到公开的实例来源
        //      （同类 ACR 里的 CountDownHandler 可能是它自己的同名类型）
        //   2. 这个事件处理类里拿不到 JobSpellTable（Spells 是入口类的属性）
        //
        // 而且吃药这件事 **AEAssist 自己就有**（PotionSetting / NotAutoPotion3），
        // 没必要重复造。预铺逻辑也在 Opener/预铺起手.cs 里单独实现了。
        return Task.CompletedTask;
    }

    public Task OnNoTarget() => Task.CompletedTask;

    /// <summary>
    /// 战斗结束 / 重置。
    ///
    /// ⚠️ **这里必须把每一个"有状态"的模块都清一遍。**
    ///
    ///    之前只清了 HealQt 和 TimelineManager，结果 6 个静态状态类
    ///    跨战斗带着上一场的数据 —— 这类 bug 很隐蔽，因为：
    ///      · 不会报错，只是"行为有点怪"
    ///      · 而且只在"连打两场"时才出现，单独测一场发现不了
    ///
    ///    具体后果：
    ///      以太管理     → 以为"刚用过豆子" → 下一场开场不卸豆
    ///      本地施放记录 → 以为"刚放过盾"   → 开场不放盾
    ///      死亡追踪     → 带着过期尸体记录 → 复活判断错
    ///      敌人移动检测 → 旧副本的位置数据 → 地面技能选位错
    ///      Dot黑名单    → 带上一场的怪种类 → 误拦正常目标
    ///    </summary>
    public void OnResetBattle()
    {
        HealQt.Reset();
        TimelineManager.战斗重置();   // 内部会清 减伤信号

        // ── 下面这些是后加的模块，每个都得在这里清 ──
        //    新增带状态的模块时，**别忘了往这里加一行**。
        try { 以太管理.重置(); } catch { }
        try { 本地施放记录.重置(); } catch { }
        try { 死亡追踪.重置(); } catch { }
        try { 敌人移动检测.重置(); } catch { }
        try { Dot黑名单.重置自适应(); } catch { }
        try { 技能熔断.重置(); } catch { }
        // ⚠️ 效果确认也有静态状态（_待确认技能 / _确认记录），
        //    之前漏了这里 —— 正是开发约定 F① 那类"有状态但没清"。
        try { 效果确认.重置(); } catch { }
        try { 技能诊断.重置(); } catch { }   // 诊断节流记录
    }

    public void OnSpellCastSuccess(Slot slot, Spell spell)
    {
    }

    public void AfterSpell(Slot slot, Spell spell)
    {
        // ── 本地施放记录（对付服务器状态滞后）──
        //   官方文档 L129：buff 是技能成功的 0.x 秒后才在内存里出现。
        //   先记本地一笔，判断时优先问它，能盖住那段空窗期，
        //   避免"刚放完盾，因为还没看到 buff 又放一次"。
        try
        {
            本地施放记录.记(spell?.Id ?? 0);
        }
        catch { }

        // ★ 记忆采集（通过钩子转出去，本类不知道采集器是谁）★
        //   原版 HealerACR 没挂这个钩子 → 什么也不发生。
        try
        {
            记忆钩子.通知决策(spell?.Id ?? 0, SpellIds.反查(spell?.Id ?? 0));
        }
        catch { }

        // 单插控制（官方文档）：
        //   每次 GCD 技能成功后会重置 BattleData.CurrGcdAbilityCount，
        //   紧接着触发这个回调 —— 在这里改它，就能控制"本 GCD 内还能插几个能力技"。
        //
        // 场景：复活用掉即刻之后，这个 GCD 不该再塞能力技（窗口留给随后的治疗）。
        try
        {
            var 表 = 当前技能表;
            var 数据 = AI.Instance?.BattleData;
            if (表 == null || 数据 == null) return;

            // ⚠️ 官方错题集警告：在 AfterSpell 里把 CurrGcdAbilityCount 设成 0/1，
            //    会让这个 GCD 内的**能力技全部作废**（"能力技返回 0 但就是不打出去"）。
            //    所以这里必须卡死条件：
            //      - 技能表里确实配了复活（否则 表.复活 是 0，spell.Id == 0 会误命中）
            //      - 而且就是刚放出去的那个技能
            if (表.复活 != 0 && spell != null && spell.Id == 表.复活)
            {
                数据.CurrGcdAbilityCount = 0;   // 复活后本 GCD 不再插能力技
            }
        }
        catch
        {
            // 拿不到战斗数据就算了，不影响正常循环
        }    }

    public void OnBattleUpdate(int currTimeInMs)
    {
        TimelineManager.更新(currTimeInMs);

        // 死亡追踪：记录"谁躺下了、躺了多久"（复活时判断该不该等）
        死亡追踪.每帧更新();

        // 本地施放记录：清理过期条目
        本地施放记录.每帧更新();
    }

    public void OnEnterRotation()
    {
    }

    public void OnExitRotation()
    {
    }

    public void OnTerritoryChanged()
    {
        // ★ 进本先重新判定队伍规模（四人本 / 八人本）★
        //   复活策略依赖它，所以换本时必须刷新。
        try
        {
            HealTargetHelper.刷新队伍规模();
            LogHelper.Info($"[HealerACR] 进入新地图 → 队伍规模：{(HealTargetHelper.是八人本() ? "八人本（双奶）" : "四人本（单奶）")}");
        }
        catch { }

        // 换副本了，让时间轴重新匹配
        TimelineManager.战斗重置();

        // ── 换本时把所有带状态的模块也清一遍 ──
        //   理由和 OnResetBattle 一样：旧地图的状态在新地图里全是错的。
        //   尤其是：
        //     · 敌人移动检测 —— 位置数据是上一个副本的敌人
        //     · DoT 黑名单     —— 上个别本的怪种类
        //     · 死亡追踪       —— 上场的尸体记录
        try { 以太管理.重置(); } catch { }
        try { 本地施放记录.重置(); } catch { }
        try { 死亡追踪.重置(); } catch { }
        try { 敌人移动检测.重置(); } catch { }
        try { Dot黑名单.重置自适应(); } catch { }
        try { 技能熔断.重置(); } catch { }
        try { 效果确认.重置(); } catch { }   // 同上：换本也要清
        try { 技能诊断.重置(); } catch { }

        // ★ 通知 AI 层：局面完全变了 ★
        //   不通知的话，AI 的"倾向"和阈值偏移会从上个副本带过来 ——
        //   比如上个本一直在打小怪（激进），进 Boss 本还保持激进。
        try { 状态重置钩子.通知(); } catch { }
    }
}
