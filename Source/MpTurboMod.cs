using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace MpTurbo
{
    /// <summary>
    /// Mod 入口。刻意保持"零编译期依赖 Multiplayer"：所有与 MP 的交互都走 <see cref="MpBridge"/> 的反射，
    /// 这样 MP 换版本/没装都不会让本 mod 加载失败。
    /// </summary>
    public class MpTurboMod : Mod
    {
        public static MpTurboSettings Settings;
        public static Harmony Harmony;

        public MpTurboMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<MpTurboSettings>();
            LongEventHandler.ExecuteWhenFinished(Bootstrap.Init);
        }

        public override string SettingsCategory() => "MP Turbo";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings.Draw(inRect);
            WriteSettings();
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            TurboState.ClampToSettings();
        }
    }

    /// <summary>
    /// 在所有 mod 程序集加载完之后再初始化：此时 Multiplayer 一定已经在内存里（如果装了的话）。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static bool done;

        static Bootstrap()
        {
            Init();
        }

        public static void Init()
        {
            if (done) return;
            done = true;

            try
            {
                MpTurboMod.Harmony = new Harmony("yg.mpturbo");
                MpBridge.Init();
                TickRatePatches.Install(MpTurboMod.Harmony);
                // 兜底补丁（GameComponent 没被创建时才生效）
                MpTurboMod.Harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());
                MpSync.Register();
            }
            catch (Exception e)
            {
                Log.Error("[MP Turbo] 初始化失败（mod 会以单机模式继续运行）: " + e);
            }
        }
    }
}
