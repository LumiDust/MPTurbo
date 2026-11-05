using System.Diagnostics;
using UnityEngine;

namespace MpTurbo
{
    /// <summary>
    /// 单调时钟。不用 UnityEngine.Time：1.7（Waylith）把 Unity 换成了 UnityStubs，
    /// 这层的成员名在版本间会漂（realtimeSinceStartup / RealtimeSinceStartup 两种写法都出现过）。
    /// Stopwatch 是 BCL 的，跨版本稳定。
    /// </summary>
    public static class Clock
    {
        static readonly Stopwatch sw = Stopwatch.StartNew();

        public static float Now => (float)sw.Elapsed.TotalSeconds;
    }
}