namespace HealerACR.Common;

/// <summary>
/// 列表对象池 —— 对照 Shiyuvi 的 `BattleCharaListPool`。
///
/// **为什么需要**：
///   选目标、数敌人这类操作**每帧都在跑**，每次都 `new List&lt;T&gt;()`
///   会持续制造垃圾，最终表现为 GC 停顿 —— 在战斗里就是"偶尔卡一下"。
///
///   对象池的做法：用完了不丢，清空后放回池子，下次直接取。
///   稳态下几乎零分配。
///
/// **用法必须配对**：
/// <code>
/// var 列表 = 列表池&lt;IBattleChara&gt;.取();
/// try
/// {
///     // ... 用列表 ...
/// }
/// finally
/// {
///     列表池&lt;IBattleChara&gt;.还(列表);   // ← 一定要还，否则池子白建
/// }
/// </code>
///
/// **注意**：还回去之前会 `Clear()`，所以**不要把列表本身存起来留着以后用** ——
/// 那等于把池子里的对象借出去不还。
/// </summary>
public static class 列表池<T>
{
    /// <summary>池子上限 —— 防止异常情况下无限增长</summary>
    private const int 上限 = 32;

    private static readonly Stack<List<T>> _池 = new();
    private static readonly object _锁 = new();

    /// <summary>取一个空列表</summary>
    public static List<T> 取()
    {
        lock (_锁)
        {
            if (_池.Count > 0)
            {
                var l = _池.Pop();
                l.Clear();
                return l;
            }
        }

        return new List<T>(16);   // 常见规模就十几个，预分配省一次扩容
    }

    /// <summary>还回去（会自动清空）</summary>
    public static void 还(List<T>? l)
    {
        if (l == null) return;

        lock (_锁)
        {
            if (_池.Count >= 上限) return;
            l.Clear();
            _池.Push(l);
        }
    }

    /// <summary>池子现状（调试用）</summary>
    public static int 空闲数
    {
        get { lock (_锁) { return _池.Count; } }
    }
}
