using System;
using DG.Tweening;
using RevGame.Core;
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
        [SerializeField] private HelperStormVisual stormVisual;
        private Transform _source;
        private PieceView _target;
        private int _targetId;
        private bool _beam, _storm;
        private Vector3 _lastEnd;
        public bool IsBeam => _beam && line.enabled;
        public bool IsStorm => _storm;
        /// <summary>每次借出播放递增，自动化可区分同一个池对象的不同弹幕，避免把上次飞行误配给这次爆炸。</summary>
        public int PlaySerial { get; private set; }
        /// <summary>在LateUpdate完成几何刷新后记录误差，避免测试在Update中把正常的一帧先后顺序误判成脱离。</summary>
        public float OriginError { get; private set; }
        public HelperStormVisual StormVisual => stormVisual;
        private Tween _animation;
        private Action<HelperAttackView> _finished;
        public string VisualStatus => "enabled=" + line.enabled + ",points=" + line.positionCount + ",width=" + line.widthMultiplier
            + ",material=" + (line.sharedMaterial == null ? "none" : line.sharedMaterial.name) + ",start=" + line.GetPosition(0) + ",end=" + line.GetPosition(1)
            + ",visible=" + line.isVisible + ",bounds=" + line.bounds.size + ",color=" + line.startColor + ",curve=" + line.widthCurve.Evaluate(.5f);
        public bool DiamondVisible => diamond != null && diamond.enabled && diamond.isVisible;
        public string DiamondStatus => "diamond=" + DiamondVisible + ",rune=" + (rune != null && rune.enabled)
            + ",position=" + diamond.transform.position + ",material=" + (diamond.sharedMaterial == null ? "none" : diamond.sharedMaterial.name);
        public void Play(Vector3 start, Vector3 end, Material material, bool beam, int lane, Action<HelperAttackView> finished,
            Transform source = null, PieceView target = null, HelperShotInfo info = default)
        {
            Stop();
            PlaySerial++;
            _finished = finished;
            _source = source; _target = target; _targetId = info.TargetId; _beam = beam; _storm = info.Storm; _lastEnd = end;
            if (source != null) { start = source.position; }
            line.sharedMaterial = material;
            line.enabled = beam;
            diamond.enabled = rune.enabled = !beam && !_storm;
            if (_storm && !beam) { stormVisual.Begin(info.Weapon, lane); }
            impact.enabled = beam;
            diamond.sharedMaterial = material;
            Vector3 delta = end - start;
            float angle = Mathf.Atan2(delta.y,delta.x) * Mathf.Rad2Deg;
            diamond.transform.rotation = Quaternion.Euler(0,0,angle);
            rune.transform.rotation = Quaternion.identity;
            float beamWidth = material.HasProperty("_BeamWidth") ? material.GetFloat("_BeamWidth") : .46f;
            if (_storm) { beamWidth = info.Width * Consts.CellSize; }
            float pulse = material.HasProperty("_Pulse") ? material.GetFloat("_Pulse") : .12f;
            float impactSize = material.HasProperty("_ImpactScale") ? material.GetFloat("_ImpactScale") : 1.5f;
            line.widthMultiplier = beam ? beamWidth : .11f;
            Vector3 bend = Vector3.Cross((end - start).normalized, Vector3.forward) * ((lane % 3 - 1) * .16f);
            float progress = 0;
            _animation = DOTween.To(() => progress, t =>
            {
                progress = t;
                if (_target != null && _target.gameObject.activeInHierarchy && _target.PieceId == _targetId && t < .6f)
                { _lastEnd = _target.transform.position; }
                if (beam)
                {
                    line.SetPosition(0, _source == null ? start : _source.position); line.SetPosition(1, _lastEnd);
                    line.widthMultiplier = beamWidth * (1f + Mathf.Sin(t * 40) * pulse);
                    impact.transform.position = _lastEnd;
                    impact.transform.localScale = Vector3.one * impactSize * (1f + Mathf.Sin(t * 35) * .08f);
                }
                else if (_storm)
                {
                    // 视觉接触半径按目标当前缩放取值；只控制弹体何时切爆炸，不参与服务层伤害判定。
                    float radius = _target != null && _target.gameObject.activeInHierarchy && _target.PieceId == _targetId
                        ? Mathf.Max(.24f, _target.transform.lossyScale.x * .38f) : .32f;
                    radius += info.Weapon == HelperWeapon.Jellyfish ? .36f : .3f;
                    stormVisual.Present(start, _lastEnd, t, radius);
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
            }, 1f, beam ? .18f : _storm ? .95f : .34f).SetEase(Ease.Linear).SetUpdate(true).OnComplete(Finish);
        }
        /// <summary>移动Tween在Update写位置，激光在LateUpdate读取最终位置；不能继续使用发射时的坐标快照。</summary>
        private void LateUpdate()
        {
            if (!IsBeam) { return; }
            if (_source == null) { Finish(); return; }
            line.SetPosition(0, _source.position);
            // 池中的目标可能已被借给另一颗棋，必须同时核对Id与激活状态才允许追踪。
            if (_target != null && _target.gameObject.activeInHierarchy && _target.PieceId == _targetId)
            { _lastEnd = _target.transform.position; }
            line.SetPosition(1, _lastEnd); impact.transform.position = _lastEnd;
            OriginError = Vector3.Distance(line.GetPosition(0), _source.position);
        }
        private void Finish()
        {
            _animation = null; var callback = _finished; _finished = null;
            callback?.Invoke(this); RevPool.Return(this, 0);
        }
        private void Stop()
        {
            _animation?.Kill(false); _animation = null; _finished = null; line.enabled = false;
            _source = null; _target = null; _beam = _storm = false;
            OriginError = 0;
            if (stormVisual != null) { stormVisual.Clear(); }
            if (diamond != null) { diamond.enabled = false; }
            if (rune != null) { rune.enabled = false; }
            if (impact != null) { impact.enabled = false; }
        }
        public void OnPoolGet() { Stop(); }
        public void OnPoolReturn() { Stop(); }
        private void OnDisable() { Stop(); }
    }
}
