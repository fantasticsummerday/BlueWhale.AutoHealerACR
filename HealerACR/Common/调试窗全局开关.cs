using System;
using System.IO;
using System.Text.Json;

namespace HealerACR.Common;

/// <summary>
/// **调试窗的全局开关** —— 不随职业变。
///
/// ══════════════════════════════════════════════════════════════════
///  [!] 为什么不能放在 `HealSettings` 里（这是一个真实的设计错误）
///
///      `HealSettings` 是**每职业一份**（`奶妈设置_学者.json` /
///      `奶妈设置_白魔.json` / …），切职业不会互相覆盖 —— 那对**治疗阈值**
///      是对的，但对**调试窗**是错的。
///
///  [!] 实测证据（用户一直开学者）：
///        `D:\FF14\Settings\Plugins\小鲸鱼统治世界\奶妈设置_Scholar.json`
///        共 40 个字段，**没有 `启用调试窗`** ⇒ 取默认值 `false`
///        ⇒ `画(启用=False)` 立刻 return ⇒ **窗口在学者上从来没被画过**。
///        而 5 个职业文件里只有 `奶妈设置_Conjurer.json` 是 `true`
///        （用户某次在幻术师上勾过）。
///
///      ==> 症状：换职业后窗口"不见了 / 在闪" ——
///          因为一个**不再被提交的持久 ImGui 窗口**内容会冻住、偶尔重绘。
///      ==> 「排查工具」要逐职业去勾，本身就是错的。
///
///  [!] 所以：存到设置目录下一个**独立文件** `调试窗开关.json`，
///      与职业无关。路径从任一职业设置文件的目录反推
///      （`HealSettings.当前文件路径` 的目录 —— 和
///        `HealSettings.写时间轴目录()` 同一个套路）。
/// ══════════════════════════════════════════════════════════════════
public static class 调试窗全局开关
{
    private static string? _路径;
    private static bool? _缓存;

    private static string 取路径()
    {
        if (_路径 != null) return _路径;

        // 从职业设置文件的目录反推（和 `写时间轴目录()` 同款做法）
        var 目录 = "";
        try
        {
            var 当前 = HealSettings.当前文件路径;
            if (!string.IsNullOrEmpty(当前))
                目录 = Path.GetDirectoryName(当前) ?? "";
        }
        catch { }

        if (string.IsNullOrEmpty(目录))
        {
            // 兜底：Documents 下（总能写）
            try { 目录 = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
            catch { 目录 = "."; }
        }

        _路径 = Path.Combine(目录, "调试窗开关.json");
        return _路径;
    }

    /// <summary>调试窗是否启用（**全局**，不随职业变）。读不到返回 false。</summary>
    public static bool 启用
    {
        get
        {
            if (_缓存.HasValue) return _缓存.Value;
            try
            {
                var p = 取路径();
                if (File.Exists(p))
                {
                    var t = File.ReadAllText(p);
                    var d = JsonSerializer.Deserialize<数据>(t);
                    _缓存 = d?.启用 ?? false;
                }
                else
                {
                    _缓存 = false;
                }
            }
            catch { _缓存 = false; }
            return _缓存.Value;
        }
        set
        {
            _缓存 = value;
            try
            {
                var p = 取路径();
                var 目录 = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(目录)) Directory.CreateDirectory(目录);
                File.WriteAllText(p,
                    JsonSerializer.Serialize(new 数据 { 启用 = value },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>落盘格式（只有这一个字段，简单点）</summary>
    private sealed class 数据
    {
        public bool 启用 { get; set; }
    }
}
