using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>场景内复用两份 Shader 材质。收到逻辑快照中的动作起点才播放，不从按钮直接播放。</summary>
    public sealed class DuelStrikeEffects : MonoBehaviour
    {
        // 黑方命中特效层，位置取权威出招帧的黑方位置。
        public Image First;
        // 白方命中特效层，与黑方独立播放和清理。
        public Image Second;
        // 两席共同使用的扩散和扭曲着色器资产。
        public Shader StrikeShader;
        // 普攻特效的序列帧，由场景配方绑定。
        public Sprite[] Jab;
        // 技能特效的序列帧。
        public Sprite[] Skill;
        // 大招特效的序列帧。
        public Sprite[] Ultimate;
        // 每席一份可写表现材质，场景创建时分配、离开时销毁。
        private readonly Material[] _materials = new Material[2];
        // 按座位缓存上一份招式编号，用来识别新的招式开始。
        private readonly int[] _lastAction = new int[2];
        // 以开始帧区分连续使用的同一招式。即使两次渲染之间补了多帧也不会只比较年龄。
        private readonly int[] _lastStart = new int[2];
        // 当前两席正在播放的特效种类，0 表示无。
        private readonly int[] _playing = new int[2];
        // 每个特效启动时的本地不缩放秒数。
        private readonly float[] _began = new float[2];
        // Shader 的染色属性 ID，避免每帧进行字符串查找。
        private static readonly int Tint = Shader.PropertyToID("_Tint");
        // Shader 的表现进度属性 ID，值域为 0 到 1。
        private static readonly int Phase = Shader.PropertyToID("_Phase");
        // Shader 的扩散冲击强度属性 ID。
        private static readonly int Power = Shader.PropertyToID("_Power");

        // 为两席各建可复用材质并关闭默认显示。
        private void Awake()
        {
            for (int i = 0; i < _materials.Length; i++)
            {
                _materials[i] = new Material(StrikeShader);
                var layer = i == 0 ? First : Second;
                layer.material = _materials[i];
                layer.enabled = false;
            }
        }

        // 从快照识别出招边沿，按座位、朝向和位置初始化特效层。
        public void Observe(int seat, int action, int startFrame, int facing, Vector2 position)
        {
            // 事件标识为座位、动作和起始帧。连续相同招式仍会各播放一次，回滚不调用此入口。
            if (action != 0 && (action != _lastAction[seat] || startFrame != _lastStart[seat]))
            {
                _playing[seat] = action;
                _began[seat] = Time.unscaledTime;
                var layer = seat == 0 ? First : Second;
                float width = action == 3 ? 710 : action == 2 ? 485 : 300;
                layer.rectTransform.sizeDelta = Vector2.one * width;
                layer.rectTransform.anchoredPosition = position + new Vector2(facing * (action == 1 ? 100 : 150), 32);
                layer.rectTransform.localScale = new Vector3(facing < 0 ? -1 : 1, 1, 1);
                layer.enabled = true;
                _materials[seat].SetColor(Tint, action == 1 ? new Color(1,.83f,.47f) :
                    action == 2 ? new Color(.42f,.72f,1) : new Color(1,.40f,.68f));
                _materials[seat].SetFloat(Power, action == 3 ? 2.1f : action == 2 ? 1.4f : .75f);
            }
            _lastAction[seat] = action;
            _lastStart[seat] = startFrame;
        }

        // 按本地表现时间推进序列帧和 Shader，播完关闭，不写回战斗状态。
        private void Update()
        {
            for (int i = 0; i < _playing.Length; i++)
            {
                int action = _playing[i];
                if (action == 0)
                {
                    continue;
                }
                var layer = i == 0 ? First : Second;
                float progress = (Time.unscaledTime - _began[i]) / (action == 3 ? .62f : .36f);
                if (progress >= 1)
                {
                    layer.enabled = false;
                    _playing[i] = 0;
                    continue;
                }
                var frames = action == 1 ? Jab : action == 2 ? Skill : Ultimate;
                // 表现可以跳过过期的图片，命中已经由权威帧在原生执行完成。
                layer.sprite = frames[Mathf.Min(frames.Length - 1, (int)(progress * frames.Length))];
                _materials[i].SetFloat(Phase, progress);
            }
        }

        // 离开场景释放该组件创建的材质实例。
        private void OnDestroy()
        {
            foreach (Material material in _materials)
            {
                if (material != null)
                {
                    Destroy(material);
                }
            }
        }
    }
}
