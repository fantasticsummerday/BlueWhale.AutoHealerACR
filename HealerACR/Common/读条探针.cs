using System;

using AEAssist;

using AEAssist.Helper;



namespace HealerACR.Common;



/// <summary>

/// **读条取证探针** —— 读条被取消时，把「当时到底发生了什么」记成一行日志（2026-10-03 新增）。

///

/// ══════════════════════════════════════════════════════════════════════

///  [!] 为什么需要（用户实测）：

///      「毁灭读条到一半终止重读」—— 日志里只能看到

///        `技能触发Event 17869 CancelCast` + `SelfCastCancel Id: 17869`

///      而 `SelfCastCancel` 只是「客户端取消了自己的读条」，**原因分不出来**：

///        ① 玩家移动（用户明确说没有）

///        ② 被击退 / 强制位移

///        ③ 服务器回弹（延迟抖动，AEAssist 自己也这么猜）

///        ④ 目标侧变化（不可选中 / 超距）

///      上一轮我凭「移动」下结论，被用户否掉了 —— 所以改成**让现场自己说话**：

///      读条开始时记基线，读条「没成功就结束」时把下面这些一起打出来。

///

///  [!] 只读 + 只在状态变化时打一行（不是每帧写日志）——

///      轮询本身有 200ms 节流，开销可以忽略。

///

///  [!] ⚠️ 不参与任何决策，也不改施放行为；读不到就少打几个字段。

/// ══════════════════════════════════════════════════════════════════════

public static class 读条探针

{

    private static bool _上次在读条;

    private static long _上次轮询;



    // ── 读条开始时的基线 ──

    private static System.Numerics.Vector3 _开始位置;

    private static float _开始已读秒;

    private static float _开始总秒;

    private static bool _有目标;

    private static float _开始目标距离;

    private static bool _开始目标可选中;



    /// <summary>每帧调一次（内部 200ms 节流；只在「开始 / 结束」两个瞬间做事）。</summary>

    public static void 每帧()

    {

        try

        {

            var 现在 = Environment.TickCount64;

            if (现在 - _上次轮询 < 200) return;

            _上次轮询 = 现在;



            var (在读条, 已读秒, 总秒) = SpellUtil.读条进度();



            // ── 开始读条：记基线（不打日志，避免刷屏）──

            if (在读条 && !_上次在读条)

            {

                _上次在读条 = true;

                _开始已读秒 = 已读秒;

                _开始总秒 = 总秒;

                try { _开始位置 = CharacterExt.我的位置(); } catch { _开始位置 = default; }



                try

                {

                    var t = CharacterExt.我的目标();

                    _有目标 = t != null;

                    if (t != null)

                    {

                        _开始目标距离 = System.Numerics.Vector3.Distance(CharacterExt.我的位置(), t.Position);

                        _开始目标可选中 = t.IsTargetable;

                    }

                    else { _开始目标距离 = -1f; _开始目标可选中 = false; }

                }

                catch { }

                return;

            }



            // ── 结束读条：判断「成功了还是被取消」，只对取消打日志 ──

            if (!在读条 && _上次在读条)

            {

                _上次在读条 = false;



                var 成功 = false;

                try { 成功 = 最近释放.最近技能 != 0 && (TimeHelper.Now() - 最近释放.最近时刻) <= 1500; }

                catch { }



                if (成功) return;   // 正常读完 —— 不用记



                var 位移 = -1f;

                try { 位移 = System.Numerics.Vector3.Distance(_开始位置, CharacterExt.我的位置()); } catch { }



                var 在移动 = false;

                try { 在移动 = SpellUtil.在移动(); } catch { }



                var 目标现在 = "无";

                try

                {

                    var t = CharacterExt.我的目标();

                    if (t != null)

                    {

                        var 距 = System.Numerics.Vector3.Distance(CharacterExt.我的位置(), t.Position);

                        目标现在 = $"有（距离 {距:F1}｜可选中={t.IsTargetable}）";

                    }

                }

                catch { }



                LogHelper.Info("[HealerACR] ★ 读条被取消（取证）★" +

                               $"｜开始读条时 已读={_开始已读秒:F2}s 总={_开始总秒:F2}s" +

                               $"｜位移={位移:F2}（0=一步没动）" +

                               $"｜此刻在移动={在移动}" +

                               $"｜开始时有目标={_有目标}（距离={_开始目标距离:F1} 可选中={_开始目标可选中}）" +

                               $"｜此刻目标={目标现在}" +

                               $"｜本地最近释放={最近释放.描述()}｜取消前1秒内动作={最近释放.近期描述(1000)}");

            }

        }

        catch { }

    }

}

