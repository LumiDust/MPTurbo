using UnityEngine;

namespace MpTurbo
{
    /// <summary>
    /// 倍率状态机。核心只有一件事：把 Multiplayer / 原版算出来的 TickRateMultiplier 再乘一个系数。
    ///
    /// 为什么补在"倍率"层而不是改 tick 循环：原版的 3x/6x/15x 本来就是这么实现的
    /// （TickManager.TickRateMultiplier / MP 的 AsyncTimeComp.TickRateMultiplier），
    /// 在这层乘系数与 MP 自己的时间系统同构，不会和它的命令调度打架。
    ///
    /// 重要：倍率是**全局同步值**。所有客户端必须乘同一个系数，否则每 tick 推进的游戏 tick 数不同 → desync。
    /// 所以这里没有任何"本地降速"的旁路；自动降速走的是同步方法（见 Update）。
    /// </summary>
    public static class TurboState
    {
        /// <summary>同步值（1 = 关闭）。所有客户端必须一致。</summary>
        public static float Multiplier = 2f;

        /// <summary>自动降速是否已生效（由本客户端触发，全服已降到 1x）。</summary>
        public static bool AutoActive;

        static float autoAt = -999f;
        static float savedMultiplier = 2f;

        public static float EffectiveMultiplier
        {
            get
            {
                var s = MpTurboMod.Settings;
                if (s == null || !s.enabled) return 1f;
                return Mathf.Clamp(Multiplier, 1f, Mathf.Max(1f, s.maxMultiplier));
            }
        }

        /// <summary>被 Harmony 补丁调用的唯一入口。</summary>
        public static float Apply(float rate)
        {
            if (rate <= 0f) return rate;          // 暂停 / 强制暂停 / 无速率，原样返回

            var s = MpTurboMod.Settings;
            if (s == null || !s.enabled) return rate;

            float m = Mathf.Clamp(Multiplier, 1f, Mathf.Max(1f, s.maxMultiplier));
            if (m <= 1.0001f) return rate;

            // 上限保护：rate * m 不超过 maxRateCap，避免把 CPU 打爆
            return Mathf.Min(rate * m, Mathf.Max(1f, s.maxRateCap));
        }

        public static void ClampToSettings()
        {
            var s = MpTurboMod.Settings;
            if (s == null) return;
            Multiplier = Mathf.Clamp(Multiplier, 1f, Mathf.Max(1f, s.maxMultiplier));
        }

        /// <summary>
        /// 自动降速。注意它**不是本地行为**：落后时把倍率同步降回 1x，让落后的客户端追上来，
        /// 追上后再同步恢复。因为倍率必须全员一致，任何"只降自己"的做法都会直接 desync。
        /// 触发条件带 30 秒冷却与 3 倍迟滞，避免来回抖。
        /// </summary>
        public static void Update()
        {
            var s = MpTurboMod.Settings;
            if (s == null || !s.enabled || !s.autoDownshift) return;
            if (!MpBridge.IsInMultiplayer) return;          // 单机不需要
            if (!MpSync.Registered) return;                 // 没注册同步就不能改，否则会分叉

            float now = Time.realtimeSinceStartup;
            int lag = MpBridge.Lag;

            if (!AutoActive)
            {
                if (lag > s.downshiftThreshold && now - autoAt > 30f)
                {
                    savedMultiplier = Multiplier > 1.01f ? Multiplier : savedMultiplier;
                    AutoActive = true;
                    autoAt = now;
                    MpSync.SetMultiplier(1f);               // 同步命令：全服降到 1x
                }
            }
            else
            {
                if (lag < s.downshiftThreshold / 3 && now - autoAt > 10f)
                {
                    AutoActive = false;
                    autoAt = now;
                    MpSync.SetMultiplier(savedMultiplier);  // 同步命令：恢复
                }
            }
        }
    }
}
