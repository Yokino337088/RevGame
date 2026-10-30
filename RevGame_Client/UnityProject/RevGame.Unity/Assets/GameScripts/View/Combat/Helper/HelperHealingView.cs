using RevGame.Core;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>
    /// 当前治疗目标的持续治疗连线。BoardView独占一个池实例，服务的保护目标Id是唯一显示依据。
    /// 这里只画绿色能量与脚下空心光环；恢复生命、攻击/攻速加成均由BattleService处理，不随帧率重复结算。
    /// </summary>
    [RevPoolPrefab("Effects/Helper", "HelperHealing", Group = RevResGroup.Battle)]
    public sealed class HelperHealingView : MonoBehaviour, IRevPoolable
    {
        [Tooltip("共享治疗Shader的连线；白绿光芯和深绿色描边同时适配明暗章节。")]
        [SerializeField] private LineRenderer beam;
        [Tooltip("目标脚下的绿色加号波纹；共享Quad/Material，不新建材质。")]
        [SerializeField] private MeshRenderer impact;
        private Transform _source;
        private PieceView _target;
        private int _targetId;
        private MaterialPropertyBlock _properties;
        private static readonly int BeamLength = Shader.PropertyToID("_BeamLength");

        public int TargetId => _targetId;
        public bool IsConnected => beam != null && beam.enabled && _target != null && _target.PieceId == _targetId;
        public float OriginError
        {
            get; private set;
        }
        public float TargetError
        {
            get; private set;
        }
        /// <summary>在模型刷新时重新绑定当前身份；目标被池借给另一颗棋时Id检查会立即隐藏旧保护。</summary>
        public void Bind(Transform source, PieceView target, int targetId)
        {
            // 视觉层结构：格面至多6，棋子从9开始，治疗线固定7，保持在棋子身体下面。
            beam.sortingOrder = 7;
            impact.sortingOrder = 7;
            _source = source;
            _target = target;
            _targetId = targetId;
            RefreshGeometry();
        }
        private void LateUpdate()
        {
            RefreshGeometry();
        }
        /// <summary>棋子和帮手的Tween在Update移动，因此LateUpdate读取最终坐标，不留下落后一帧的射线。</summary>
        private void RefreshGeometry()
        {
            bool valid = _source != null && _source.gameObject.activeInHierarchy && _target != null
                && _target.gameObject.activeInHierarchy && _target.PieceId == _targetId && _targetId != 0;
            beam.enabled = valid;
            impact.enabled = valid;

            if (!valid)
            {
                return;
            }
            Vector3 start = _source.position, end = _target.transform.position;
            Material material = beam.sharedMaterial;
            float width = material != null && material.HasProperty("_BeamWidth") ? material.GetFloat("_BeamWidth") : .62f;
            float pulse = 1f + Mathf.Sin(Time.time * 9f) * .08f;
            beam.widthMultiplier = width * pulse;
            beam.SetPosition(0, start);
            beam.SetPosition(1, end);

            if (_properties == null)
            {
                _properties = new MaterialPropertyBlock();
            }
            _properties.SetFloat(BeamLength, Mathf.Max(.01f, Vector3.Distance(start, end)) / Mathf.Max(.1f, width));
            beam.SetPropertyBlock(_properties);
            impact.transform.position = end;
            // 约一格半的提示圈能看清加号，同时给棋子身体留出空心中心。
            impact.transform.localScale = Vector3.one * Consts.CellSize * 1.65f * pulse;
            OriginError = Vector3.Distance(beam.GetPosition(0), start);
            TargetError = Vector3.Distance(beam.GetPosition(1), end);
        }
        private void Clear()
        {
            _source = null;
            _target = null;
            _targetId = 0;
            OriginError = TargetError = 0;

            if (beam != null)
            {
                beam.enabled = false;
                beam.SetPropertyBlock(null);
            }
            if (impact != null)
            {
                impact.enabled = false;
            }
        }
        public void OnPoolGet()
        {
            Clear();
        }
        public void OnPoolReturn()
        {
            Clear();
        }
        private void OnDisable()
        {
            Clear();
        }
    }
}
