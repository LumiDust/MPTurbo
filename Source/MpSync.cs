using System;
using System.Reflection;
using UnityEngine;
using Verse;

namespace MpTurbo
{
    /// <summary>
    /// 同步注册。只调用 Multiplayer 的公开 API：MP.RegisterSyncMethod(Type, string, SyncType[])。
    /// 注册成功后，MP 的 transpiler 会把 <see cref="SetMultiplier"/> 的方法体改写成"本地调用 → 广播命令"，
    /// 所以 UI 里任何一个人拖动滑块，全服都会执行同一个方法体，倍率保持一致。
    /// </summary>
    public static class MpSync
    {
        public static bool Registered { get; private set; }

        public static void Register()
        {
            if (!MpBridge.MpPresent || MpBridge.ApiType == null) return;

            try
            {
                MethodInfo register = null;
                foreach (var m in MpBridge.ApiType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "RegisterSyncMethod") continue;
                    if (m.GetParameters().Length == 3) { register = m; break; }
                }

                if (register == null)
                {
                    Log.Warning("[MP Turbo] 找不到 MP.RegisterSyncMethod(Type, string, SyncType[])，倍率将只在本地生效。");
                    return;
                }

                var target = typeof(MpSync).GetMethod(nameof(SetMultiplier), BindingFlags.Public | BindingFlags.Static);
                if (target == null)
                {
                    Log.Warning("[MP Turbo] 找不到 SetMultiplier 方法。");
                    return;
                }

                register.Invoke(null, new object[] { typeof(MpSync), nameof(SetMultiplier), null });
                Registered = true;
                Log.Message("[MP Turbo] 已注册同步方法 SetMultiplier。");
            }
            catch (Exception e)
            {
                Log.Warning("[MP Turbo] 注册同步方法失败（倍率只在本地生效）: " + e.Message);
            }
        }

        /// <summary>
        /// 设置倍率。注册成功后这是"同步方法"：本地调用 → 发命令 → 所有人（含自己）执行。
        /// 未装 MP / 注册失败时就是普通方法，只改本地。
        /// 注意：方法体必须保持确定性，不要在这里读 IsHosting 之类的"因人而异"的状态。
        /// </summary>
        public static void SetMultiplier(float value)
        {
            float max = MpTurboMod.Settings?.maxMultiplier ?? 20f;
            TurboState.Multiplier = Mathf.Clamp(value, 1f, Mathf.Max(1f, max));
        }

        /// <summary>UI 用：非房主且设置了"仅房主可改"时禁用。</summary>
        public static bool CanEdit
        {
            get
            {
                var s = MpTurboMod.Settings;
                if (s == null || !s.hostOnly) return true;
                if (!MpBridge.IsInMultiplayer) return true;   // 单机随便改
                return MpBridge.IsHosting;
            }
        }

        public static void Step(float delta)
        {
            if (!CanEdit) return;
            SetMultiplier(TurboState.Multiplier + delta);
        }

        static float lastSentAt = -999f;
        static float lastSentValue = -1f;

        /// <summary>
        /// 给设置页滑块用：拖动时每帧都会触发，直接调 <see cref="SetMultiplier"/> 会刷屏发包。
        /// 联机时做 0.25s / 0.05 步长的节流；单机时直接生效，保证手感。
        /// </summary>
        public static void SetMultiplierFromSlider(float value)
        {
            if (!MpBridge.IsInMultiplayer)
            {
                SetMultiplier(value);
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (Mathf.Abs(value - lastSentValue) < 0.05f) return;
            if (now - lastSentAt < 0.25f) return;

            lastSentAt = now;
            lastSentValue = value;
            SetMultiplier(value);
        }
    }
}
