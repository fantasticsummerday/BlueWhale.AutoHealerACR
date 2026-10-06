using System;

using AEAssist;

using AEAssist.Helper;



namespace HealerACR.Common;



/// <summary>

/// **本地最近实际释放出去的技能** —— 给 AI 终审判"与本地一致"用（2026-10-03 新增）。

///

/// ══════════════════════════════════════════════════════════════════════

///  [!] 为什么必须有（用户实测原话）：

///      「AI 应该是预铺技能建议 —— 比如本地判定打毁灭、AI 也建议毁灭，

///        但这时候因为延迟，AI 建议落地时本地**已经把毁灭打出去了**，

///        这种就不应该算作没有命中。」

///

///  [!] 现状（错在哪）：终审只问"此刻能不能放"（`SpellUtil.可用`）——

///      刚放出去的技能当然在 CD 里 ⇒ 判定"不可用" ⇒ **整条建议被丢弃**，

///      而且不计入命中 ⇒ 命中率被算低、AI 看起来像没在工作 ✗

///

///  [!] 做法：订阅 AEAssist 的施法成功事件（和 `记录模式` 用的是同一个 hook），

///      记下"最近放出的技能 + 时刻"；终审发现"AI 建议的正是这个" ⇒

///      判为**与本地一致**：计入命中、不丢弃、日志写清楚。

///

///  [!] ⚠️ 只读观察，不影响任何施放行为；订阅失败也不影响其他功能

///      （失败方向 = 记录不到 ⇒ 回到原来的判法）。

/// ══════════════════════════════════════════════════════════════════════

public static class 最近释放

{

    /// <summary>"刚放过"的时间窗 —— 2.5 秒 ≈ 一个 GCD + 一点余量。</summary>

    public const int 默认窗口毫秒 = 2500;



    private static uint _技能;

    private static long _时刻;



    private static AEAssist.MemoryApi.MemApiSpellCastSuccess? _api;

    /// <summary>
    /// **最近若干次施放**（2026-10-03 新增）—— 只给"读条被取消"取证用。
    /// 原来的 描述() 只能给"最近一次"，而定位极早取消需要看"取消前 1 秒内发过哪些动作"✗
    /// 只在游戏线程读写（施法成功事件 + 探针都在游戏线程）✓ 不做跨线程共享 ✓
    /// </summary>
    private static readonly System.Collections.Generic.List<(uint 技能, long 时刻)> _近几次 = new();

    /// <summary>最近 N 次施放的文字（新的在前，只在时间窗内），给读条取证用。</summary>
    public static string 近期描述(int 毫秒窗口 = 1000, int 最多 = 6)
    {
        try
        {
            var 现在 = TimeHelper.Now();
            var 出 = new System.Text.StringBuilder();
            var 数 = 0;
            for (var i = _近几次.Count - 1; i >= 0 && 数 < 最多; i--)
            {
                var 技能 = _近几次[i].技能;
                var 差 = 现在 - _近几次[i].时刻;
                if (差 > 毫秒窗口) break;
                出.Append(技能).Append('(').Append(差).Append("ms前) ");
                数++;
            }
            return 数 == 0 ? "（窗口内没有别的动作）" : 出.ToString().TrimEnd();
        }
        catch { return "（读不到）"; }
    }

    private static bool _已订阅;

    private static bool _订阅试过;



    /// <summary>最近放出的技能 id（0 = 没有记录）。</summary>

    public static uint 最近技能 => _技能;



    /// <summary>最近放出的时刻（TickCount64 毫秒；0 = 没有记录）。</summary>

    public static long 最近时刻 => _时刻;



    /// <summary>确保订阅了施法成功事件（幂等、只试一次；失败不影响其他功能）。</summary>

    public static void 确保订阅()

    {

        if (_已订阅 || _订阅试过) return;

        _订阅试过 = true;



        try

        {

            var api = Core.Resolve<AEAssist.MemoryApi.MemApiSpellCastSuccess>();

            if (api == null) return;



            _api = api;   // ★ 留着读 LastTarget（给 治疗间隔 用）
            api.OnCastSucces += 收到施法成功;

            _已订阅 = true;

            LogHelper.Info("[HealerACR] 最近释放：已订阅施法成功事件（用于判定 AI 建议与本地一致）");

        }

        catch (Exception e)

        {

            // 订阅失败只是退化成"没有记录"，不影响施放

            LogHelper.Info("[HealerACR] 最近释放：订阅施法事件失败（不影响功能，只是判不出与本地一致）："

                           + e.GetType().Name);

        }

    }



    /// <summary>事件回调 —— 只记 id 与时刻。</summary>

    private static void 收到施法成功(AEAssist.CombatRoutine.SpellType 类型, uint 技能)

    {

        try

        {

            if (技能 == 0) return;

            _技能 = 技能;

            _时刻 = TimeHelper.Now();

            // ★ 2026-10-03：同时进小环形缓冲（给"读条被取消"取证）✓
            try
            {
                _近几次.Add((技能, _时刻));
                while (_近几次.Count > 12) _近几次.RemoveAt(0);
            }
            catch { }

            // ★ 转发给「治疗间隔」：同一目标短时间内被照顾两次 ⇒ 记一行（2026-10-03）
            try
            {
                var 目标 = _api?.LastTarget;
                // ★ 2026-10-15 修（崩溃防护）：**只判 null 不够** —— `LastTarget` 在目标刚消失时
                //   拿到的是**哨兵地址**（`0x12345679` ≠ null），而 `GameObjectId` / `Name`
                //   都是原生热读 ⇒ 访问违例（**穿透 catch、无转储、进程直接没**）。
                //   本回调在**每次施放成功**时由游戏线程触发，目标恰好被释放是常态。
                //   （项目守则"不假设调用方判过"；同族写法见 `占星卡目标.cs:119-121` 先判 `对象有效()`。）
                if (目标 != null && 目标.对象有效())
                    治疗间隔.记一次(技能, 目标.GameObjectId, 目标.Name?.TextValue ?? "?");
            }
            catch { }

        }

        catch { }

    }



    /// <summary>**这个技能是不是"本地刚刚放出去的那个"** —— 终审用它判"与本地一致"。</summary>

    public static bool 刚刚放过(uint 技能, int 窗口毫秒 = 默认窗口毫秒)

    {

        try

        {

            if (技能 == 0 || _技能 == 0) return false;

            if (技能 != _技能) return false;

            return TimeHelper.Now() - _时刻 <= 窗口毫秒;

        }

        catch { return false; }

    }



    /// <summary>给日志/窗口用的一句话描述。</summary>

    public static string 描述()

    {

        try

        {

            if (_技能 == 0) return "（还没记录到）";

            var 秒 = (TimeHelper.Now() - _时刻) / 1000.0;

            return $"{_技能}（{秒:F1} 秒前）";

        }

        catch { return "（读不到）"; }

    }

}

