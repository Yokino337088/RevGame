using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>场景内复用两份 Shader 材质。收到逻辑快照中的动作起点才播放，不从按钮直接播放。</summary>
    public sealed class DuelStrikeEffects : MonoBehaviour
    {
        public Image First, Second;
        public Shader StrikeShader;
        public Sprite[] Jab, Skill, Ultimate;
        private readonly Material[] _materials = new Material[2];
        private readonly int[] _lastAction = new int[2];
        private readonly int[] _lastAge = new int[2];
        private readonly int[] _playing = new int[2];
        private readonly float[] _began = new float[2];
        private static readonly int Tint = Shader.PropertyToID("_Tint");
        private static readonly int Phase = Shader.PropertyToID("_Phase");
        private static readonly int Power = Shader.PropertyToID("_Power");

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

        public void Observe(int seat, int action, int age, int facing, Vector2 position)
        {
            if (action != 0 && (action != _lastAction[seat] || age < _lastAge[seat]))
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
            _lastAge[seat] = age;
        }

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
                layer.sprite = frames[Mathf.Min(frames.Length - 1, (int)(progress * frames.Length))];
                _materials[i].SetFloat(Phase, progress);
            }
        }

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
