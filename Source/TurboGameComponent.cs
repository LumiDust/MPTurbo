using UnityEngine;
using Verse;

namespace MpTurbo
{
    /// <summary>
    /// 存档内组件：保存倍率、每帧更新追赶状态、画 HUD。
    /// RimWorld 会自动为每个非抽象 GameComponent 子类创建实例，不需要注册 Def。
    /// </summary>
    public class TurboGameComponent : GameComponent
    {
        /// <summary>组件是否已被创建。用于让 <see cref="UiRootUpdateFallback"/> 判断要不要兜底。</summary>
        public static bool Alive;

        // 1.6 的 GameComponent 是无参构造，由 Game.FillComponents() 自动实例化
        public TurboGameComponent()
        {
            Alive = true;
        }

        public override void ExposeData()
        {
            // 倍率跟着存档走：读档/加入时所有人拿到同一个值
            Scribe_Values.Look(ref TurboState.Multiplier, "mpTurboMultiplier", 2f);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                TurboState.ClampToSettings();
        }

        public override void GameComponentUpdate()
        {
            MpBridge.Refresh();
            TurboState.Update();
        }

        public override void GameComponentOnGUI()
        {
            TurboHud.Draw();
        }
    }
}
