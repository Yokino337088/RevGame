// ============================================================
// GameAudio.cs —— 业务事件到声音的映射。
// 不直接创建 AudioSource，播放、同帧去重和加载交给 RevSound；退出场景停止并卸载本游戏声音。
// ============================================================
using RevGame.Core;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed class GameAudio : MonoBehaviour
    {
        private void Start()
        {
            RevEvent.AddEventListener<int>(Events.PiecePlaced, OnPlaced, owner: this);
            RevEvent.AddEventListener<int>(Events.DamageResolved, OnDamage, owner: this);
            RevEvent.AddEventListener<int>(Events.PieceMerged, OnMerged, owner: this);
            RevEvent.AddEventListener<GameSide>(Events.PhaseEnterSettle, OnSettle, owner: this);
            RevEvent.AddEventListener<GameScreen>(Events.ScreenChanged, OnScreen, owner: this);
            RevSound.Failed += OnFailed;
        }

        private void OnPlaced(int id)
        {
            RevSound.Play("落子", volume: 0.35f);
        }

        private void OnDamage(int id)
        {
            RevSound.Play("受击", volume: 0.18f);
        }

        private void OnMerged(int id)
        {
            RevSound.Play("升星", volume: 0.4f);
        }

        private void OnSettle(GameSide winner)
        {
            RevSound.Play(winner == GameSide.Player ? "胜利" : "失败", volume: 0.4f);
        }

        private void OnScreen(GameScreen screen)
        {
            // 从按钮触发流程后才播音乐，也适配浏览器必须先有用户手势才能出声的限制。
            if (screen == GameScreen.Battle)
            {
                RevSound.PlayBgm("普通朋友", fadeSeconds: 0.6f, volume: 0.06f);
            }
            else
            {
                RevSound.StopBgm(0.3f);
            }
        }

        private void OnFailed(string name, RevSoundErrorReason reason)
        {
            RevLog.Warn("声音播放失败：" + name + "，原因：" + reason, "RevGame");
        }

        private void OnDestroy()
        {
            RevEvent.RemoveAllByOwner(this);
            RevSound.Failed -= OnFailed;
            RevSound.StopAll();
            RevSound.UnloadAll();
        }
    }
}
