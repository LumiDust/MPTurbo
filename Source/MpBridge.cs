using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Verse;

namespace MpTurbo
{
    /// <summary>
    /// 与 Multiplayer 的唯一接触面。全部走反射，**不引用 Multiplayer 的任何程序集**，
    /// 这样：① 没装 MP 也能加载；② MP 升级改了签名只会让某一项功能降级，不会让 mod 崩。
    /// 只读公开静态字段/属性，不调用 MP 的内部方法（除了注册同步方法这一个公开 API）。
    /// </summary>
    public static class MpBridge
    {
        public static bool MpPresent { get; private set; }
        public static bool IsInMultiplayer { get; private set; }
        public static bool IsHosting { get; private set; }
        public static bool Desynced { get; private set; }
        public static bool Frozen { get; private set; }
        public static int Lag { get; private set; }
        public static int PlayerCount { get; private set; }
        public static readonly List<PlayerLag> Players = new List<PlayerLag>();

        public static Type ApiType { get; private set; }

        public struct PlayerLag
        {
            public string name;
            public int ticksBehind;
            public int latency;
        }

        static Type tickPatchType;
        static Type sessionType;
        static Type playerInfoType;

        static PropertyInfo pIsInMp;
        static PropertyInfo pIsHosting;
        static FieldInfo fSession;
        static FieldInfo fTimer;
        static FieldInfo fTickUntil;
        static FieldInfo fServerFrozen;
        static FieldInfo fDesynced;
        static FieldInfo fPlayers;
        static FieldInfo fUsername;
        static FieldInfo fTicksBehind;
        static FieldInfo fLatency;

        static float lastRefresh = -999f;
        static bool initTried;

        public static void Init()
        {
            if (initTried) return;
            initTried = true;

            var mpType = GenTypes.GetTypeInAnyAssembly("Multiplayer.Client.Multiplayer", null);
            if (mpType == null)
            {
                MpPresent = false;
                Log.Message("[MP Turbo] 未检测到 Multiplayer，进入单机模式。");
                return;
            }

            try
            {
                ApiType = GenTypes.GetTypeInAnyAssembly("Multiplayer.API.MP", null);
                tickPatchType = GenTypes.GetTypeInAnyAssembly("Multiplayer.Client.TickPatch", null);
                sessionType = GenTypes.GetTypeInAnyAssembly("Multiplayer.Client.MultiplayerSession", null);
                playerInfoType = GenTypes.GetTypeInAnyAssembly("Multiplayer.Client.PlayerInfo", null);

                const BindingFlags SF = BindingFlags.Public | BindingFlags.Static;
                const BindingFlags IF = BindingFlags.Public | BindingFlags.Instance;

                if (ApiType != null)
                {
                    pIsInMp = ApiType.GetProperty("IsInMultiplayer", SF);
                    pIsHosting = ApiType.GetProperty("IsHosting", SF);
                }

                fSession = mpType.GetField("session", SF);
                if (tickPatchType != null)
                {
                    fTimer = tickPatchType.GetField("Timer", SF);
                    fTickUntil = tickPatchType.GetField("tickUntil", SF);
                    fServerFrozen = tickPatchType.GetField("serverFrozen", SF);
                }
                if (sessionType != null)
                {
                    fPlayers = sessionType.GetField("players", IF);
                    fDesynced = sessionType.GetField("desynced", IF);
                }
                if (playerInfoType != null)
                {
                    fUsername = playerInfoType.GetField("username", IF);
                    fTicksBehind = playerInfoType.GetField("ticksBehind", IF);
                    fLatency = playerInfoType.GetField("latency", IF);
                }

                MpPresent = pIsInMp != null || fSession != null;
                Log.Message("[MP Turbo] 检测到 Multiplayer，同步通道" + (ApiType != null ? "可用。" : "不可用（API 类型缺失）。"));
            }
            catch (Exception e)
            {
                MpPresent = false;
                Log.Warning("[MP Turbo] 读取 Multiplayer 接口失败，降级为单机模式: " + e.Message);
            }
        }

        public static void Refresh()
        {
            if (!MpPresent) return;
            float now = Clock.Now;
            if (now - lastRefresh < 0.25f) return;
            lastRefresh = now;

            try
            {
                if (pIsInMp != null) IsInMultiplayer = (bool)pIsInMp.GetValue(null, null);
                if (pIsHosting != null) IsHosting = (bool)pIsHosting.GetValue(null, null);

                object session = fSession?.GetValue(null);
                if (session == null)
                {
                    PlayerCount = 0;
                    Players.Clear();
                    Lag = 0;
                    return;
                }

                if (fDesynced != null) Desynced = (bool)fDesynced.GetValue(session);

                if (fPlayers != null && fPlayers.GetValue(session) is IList list)
                {
                    Players.Clear();
                    foreach (object p in list)
                    {
                        if (p == null) continue;
                        Players.Add(new PlayerLag
                        {
                            name = fUsername?.GetValue(p) as string ?? "?",
                            ticksBehind = fTicksBehind != null ? (int)fTicksBehind.GetValue(p) : 0,
                            latency = fLatency != null ? (int)fLatency.GetValue(p) : 0
                        });
                    }
                    PlayerCount = Players.Count;
                }

                if (fTimer != null && fTickUntil != null)
                    Lag = (int)fTickUntil.GetValue(null) - (int)fTimer.GetValue(null);
                if (fServerFrozen != null) Frozen = (bool)fServerFrozen.GetValue(null);
            }
            catch
            {
                // 读失败就保持上一次的值，绝不因为诊断功能把游戏搞崩
            }
        }

        public static string StatusLine
        {
            get
            {
                if (!MpPresent) return "MPTurbo_StatusSolo".Translate().ToString();
                if (!IsInMultiplayer) return "MPTurbo_StatusNoSession".Translate().ToString();
                return "MPTurbo_StatusMp".Translate(PlayerCount, IsHosting ? "MPTurbo_Host".Translate().ToString() : "MPTurbo_Client".Translate().ToString()).ToString();
            }
        }

        /// <summary>按"谁最拖后腿"排序，取前 n 个。</summary>
        public static void WorstBehind(int n, List<string> outList)
        {
            outList.Clear();
            if (Players.Count == 0) return;
            Players.Sort((a, b) => b.ticksBehind.CompareTo(a.ticksBehind));
            for (int i = 0; i < Players.Count && i < n; i++)
            {
                var p = Players[i];
                if (p.ticksBehind <= 0) break;
                outList.Add(p.name + " (" + p.ticksBehind + ")");
            }
            Players.Sort((a, b) => b.ticksBehind.CompareTo(a.ticksBehind));
        }
    }
}

