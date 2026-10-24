using DG.Tweening;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>
    /// 一颗积分飞星：单个UI四边形由共享Shader画星核/碎光/短尾，不加载散图、不复制材质。
    /// 使用RevPool复用，DOTween走弧线，归池时停止动画；飞行完成只通知面板回收，不增加积分。
    /// </summary>
    [RevPoolPrefab(RevResPath.Effects_Combat, "ScoreSpark", Group = RevResGroup.Battle)]
    public sealed class ScoreSparkView : MonoBehaviour, IRevPoolable
    {
        [SerializeField] private Image glow;
        private RectTransform _rect;
        private Tween _flight;
        private System.Action<ScoreSparkView> _finished;
        private float _progress;
        public bool IsFlying => _flight != null;
        private void Awake() { _rect = (RectTransform)transform; }

        /// <summary>
        /// start/end为HUD叠加画布的局部像素坐标，lane只错开弧线与时长，不影响分数。
        /// 完成回调由所属HUD移除活动登记；无论正常结束或中途归池，动画都只能结束一次。
        /// </summary>
        public void Fly(Vector2 start, Vector2 end, bool critical, int lane, System.Action<ScoreSparkView> finished)
        {
            Stop(); _finished = finished; _progress = 0;
            _rect.anchorMin = _rect.anchorMax = new Vector2(.5f, .5f);
            _rect.anchoredPosition = start;
            Vector2 bend = Vector2.Lerp(start, end, .45f) + new Vector2((lane % 3 - 1) * 55f, 110f + lane % 4 * 24f);
            Color tint = critical ? new Color(1f, .55f, .13f) : new Color(.48f, .95f, 1f);
            _flight = DOTween.To(() => _progress, t =>
            {
                _progress = t;
                float p = t * t, q = 1f - p;
                _rect.anchoredPosition = q * q * start + 2f * q * p * bend + p * p * end;
                Vector2 tangent = 2f * q * (bend - start) + 2f * p * (end - bend);
                _rect.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(tangent.y, tangent.x) * Mathf.Rad2Deg);
                _rect.localScale = new Vector3(1f + t * .75f, 1f - t * .25f, 1f);
                tint.a = Mathf.Min(Mathf.Clamp01(t / .08f), Mathf.Clamp01((1f - t) / .14f));
                glow.color = tint;
            }, 1f, .66f + lane % 3 * .035f).SetEase(Ease.Linear).SetUpdate(true).OnComplete(Finish);
        }

        private void Finish()
        {
            _flight = null;
            System.Action<ScoreSparkView> callback = _finished; _finished = null;
            callback?.Invoke(this);
            RevPool.Return(this, 0);
        }
        private void Stop() { _flight?.Kill(false); _flight = null; _finished = null; }
        public void OnPoolGet() { Stop(); }
        public void OnPoolReturn() { Stop(); }
        private void OnDisable() { Stop(); }
        private void OnDestroy() { Stop(); }
    }
}
