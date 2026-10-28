using System;
using DG.Tweening;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>共享材质的积分弹道/激光。每对象只写自己的几何，不克隆材质；归池取消Tween和旧完成回调。</summary>
    [RevPoolPrefab("Effects/Helper", "HelperAttack", Group = RevResGroup.Battle)]
    public sealed class HelperAttackView : MonoBehaviour, IRevPoolable
    {
        [SerializeField] private LineRenderer line;
        [Tooltip("独立菱形几何Quad与CC0魔法符文叠层，共用资产Mesh和Material，不逐发新建。")] 
        [SerializeField] private MeshRenderer diamond, rune, impact;
        private Tween _animation;
        private Action<HelperAttackView> _finished;
        public string VisualStatus => "enabled=" + line.enabled + ",points=" + line.positionCount + ",width=" + line.widthMultiplier
            + ",material=" + (line.sharedMaterial == null ? "none" : line.sharedMaterial.name) + ",start=" + line.GetPosition(0) + ",end=" + line.GetPosition(1)
            + ",visible=" + line.isVisible + ",bounds=" + line.bounds.size + ",color=" + line.startColor + ",curve=" + line.widthCurve.Evaluate(.5f);
        public bool DiamondVisible => diamond != null && diamond.enabled && diamond.isVisible;
        public string DiamondStatus => "diamond=" + DiamondVisible + ",rune=" + (rune != null && rune.enabled)
            + ",position=" + diamond.transform.position + ",material=" + (diamond.sharedMaterial == null ? "none" : diamond.sharedMaterial.name);
        public void Play(Vector3 start, Vector3 end, Material material, bool beam, int lane, Action<HelperAttackView> finished)
        {
            Stop();
            _finished = finished;
            line.sharedMaterial = material;
            line.enabled = beam;
            diamond.enabled = rune.enabled = !beam;
            impact.enabled = beam;
            diamond.sharedMaterial = material;
            Vector3 delta = end - start;
            float angle = Mathf.Atan2(delta.y,delta.x) * Mathf.Rad2Deg;
            diamond.transform.rotation = Quaternion.Euler(0,0,angle);
            rune.transform.rotation = Quaternion.identity;
            float beamWidth = material.HasProperty("_BeamWidth") ? material.GetFloat("_BeamWidth") : .46f;
            float pulse = material.HasProperty("_Pulse") ? material.GetFloat("_Pulse") : .12f;
            float impactSize = material.HasProperty("_ImpactScale") ? material.GetFloat("_ImpactScale") : 1.5f;
            line.widthMultiplier = beam ? beamWidth : .11f;
            Vector3 bend = Vector3.Cross((end - start).normalized, Vector3.forward) * ((lane % 3 - 1) * .16f);
            float progress = 0;
            _animation = DOTween.To(() => progress, t =>
            {
                progress = t;
                if (beam)
                {
                    line.SetPosition(0, start); line.SetPosition(1, end);
                    line.widthMultiplier = beamWidth * (1f + Mathf.Sin(t * 40) * pulse);
                    impact.transform.position = end;
                    impact.transform.localScale = Vector3.one * impactSize * (1f + Mathf.Sin(t * 35) * .08f);
                }
                else
                {
                    Vector3 head = Vector3.Lerp(start, end, t) + bend * Mathf.Sin(t * Mathf.PI);
                    diamond.transform.position = rune.transform.position = head;
                    // 方向切面保持清楚，符文缓慢旋转；菱形长轴不是上一版线段，所以不会与普通箭矢混淆。
                    diamond.transform.localScale = new Vector3(.95f,.65f,1) * (.9f+.1f*Mathf.Sin(t*12));
                    rune.transform.rotation = Quaternion.Euler(0,0,t*90+lane*30);
                    rune.transform.localScale = Vector3.one * .9f;
                }
            }, 1f, beam ? .37f : .34f).SetEase(Ease.Linear).SetUpdate(true).OnComplete(Finish);
        }
        private void Finish()
        {
            _animation = null; var callback = _finished; _finished = null;
            callback?.Invoke(this); RevPool.Return(this, 0);
        }
        private void Stop()
        {
            _animation?.Kill(false); _animation = null; _finished = null; line.enabled = false;
            if (diamond != null) { diamond.enabled = false; }
            if (rune != null) { rune.enabled = false; }
            if (impact != null) { impact.enabled = false; }
        }
        public void OnPoolGet() { Stop(); }
        public void OnPoolReturn() { Stop(); }
        private void OnDisable() { Stop(); }
    }
}
