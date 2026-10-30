using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>
    /// 预制体中的数字滚筒零件。每列只有上下两个字，RectMask2D裁掉窗口外的部分。
    /// 单个Tween推进所有列，数字字符串使用十个缓存；不在每帧生成完整积分字符串或创建文本对象。
    /// 连续得分只覆盖待显示目标，当前滚动结束后追上最新总分，不堆几十个动画序列。
    /// </summary>
    public sealed class ScoreReelView : MonoBehaviour
    {
        /// <summary>预制体从左到右绑定的上下两排数字；仅初始化一次，每帧复用，不生成新文字对象。</summary>
        [SerializeField] private Text[] lower, upper;
        /// <summary>数值超过滚筒位数时显示完整文本，避免永久积分丢掉高位。此时暂停滚动。</summary>
        [SerializeField] private Text overflow;
        /// <summary>一格滚动的画布像素高度，必须与预制体裁剪窗口一致。</summary>
        [SerializeField] private float rowHeight = 46f;
        /// <summary>积分为0，倍率为2；倍率传入百分位整数，使动画避免浮点小数位抖动。</summary>
        [SerializeField] private int fractionalDigits;
        [SerializeField] private Text decimalPoint;
        private static readonly string[] Digits = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };
        /// <summary>当前一轮滚动的起点/终点，仅由本组件持有；待显示目标变化不会重建这些数组。</summary>
        private float[] _from, _to;
        private int[] _shown;
        // 高位不变时不反复写RectTransform，避免把静止数字也加入每帧Canvas重建。
        private float[] _paintedPosition;
        private Tween _roll;
        private long _target = -1;
        private float _progress;
        public bool IsRolling => _roll != null;
        public long TargetValue => _target;

        private void Awake()
        {
            _from = new float[lower.Length];
            _to = new float[lower.Length];
            _shown = new int[lower.Length];
            _paintedPosition = new float[lower.Length];
            for (int i = 0; i < _shown.Length; i++)
            {
                _shown[i] = -1;
                _paintedPosition[i] = float.NaN;
            }
        }
        /// <summary>snap用于换回合/首次打开，必须直接落到真实分数，避免播放上一回合的旧分。</summary>
        public void Display(long value, bool snap)
        {
            value = System.Math.Max(0, value);

            if (_target == value && !snap)
            {
                return;
            }
            _target = value;
            long limit = 1;
            for (int i = 0; i < lower.Length; i++)
            {
                limit *= 10;
            }
            bool tooLarge = value >= limit;
            overflow.gameObject.SetActive(tooLarge);

            if (decimalPoint != null)
            {
                decimalPoint.gameObject.SetActive(!tooLarge);
            }
            if (tooLarge)
            {
                overflow.text = fractionalDigits == 2 ? (value / 100d).ToString("0.00") : value.ToString("N0");
            }
            foreach (Text digit in lower)
            {
                digit.transform.parent.gameObject.SetActive(!tooLarge);
            }
            if (snap || tooLarge)
            {
                StopRoll();
                for (int i = lower.Length - 1; i >= 0; i--)
                {
                    Paint(i, value % 10);
                    value /= 10;
                }
                return;
            }
            if (_roll == null)
            {
                BeginRoll();
            }
        }

        /// <summary>捕获本轮终值；播放中再得分只更新_target，本轮结束后最多追播一轮，避免无限堆动画。</summary>
        private void BeginRoll()
        {
            long landing = _target, digits = landing;
            for (int i = lower.Length - 1; i >= 0; i--)
            {
                int next = (int)(digits % 10);
                digits /= 10;
                _from[i] = Mathf.Max(0, _shown[i]);
                // 未变化的高位不转。变化位多转一周，有“刷屏”感，但不会每击新增一套Tween。
                _to[i] = next == (int)_from[i] ? _from[i] : _from[i] + 10 + (next - _from[i] + 10) % 10;
            }
            _progress = 0;
            _roll = DOTween.To(() => _progress, t =>
            {
                _progress = t;
                for (int i = 0; i < lower.Length; i++)
                {
                    float stagger = (lower.Length - 1 - i) * .012f;
                    float p = Mathf.Clamp01((t - stagger) / (1f - stagger));
                    Paint(i, Mathf.Lerp(_from[i], _to[i], 1f - Mathf.Pow(1f - p, 3f)));
                }
            }, 1f, .62f).SetUpdate(true).SetEase(Ease.Linear).OnComplete(() =>
            {
                _roll = null;

                if (_target != landing)
                {
                    BeginRoll();
                }
            });
        }
        /// <summary>整数部分决定字形，小数部分决定上下偏移；只有数字真的变化时才重建该Text的字形网格。</summary>
        private void Paint(int column, float position)
        {
            if (_paintedPosition[column] == position)
            {
                return;
            }
            _paintedPosition[column] = position;
            int whole = Mathf.FloorToInt(position), digit = whole % 10;

            if (_shown[column] != digit)
            {
                _shown[column] = digit;
                lower[column].text = Digits[digit];
                upper[column].text = Digits[(digit + 1) % 10];
            }
            float y = -(position - whole) * rowHeight;
            lower[column].rectTransform.anchoredPosition = new Vector2(0, y);
            upper[column].rectTransform.anchoredPosition = new Vector2(0, y + rowHeight);
        }
        /// <summary>关闭/隐藏时终止回调，不强行触发OnComplete，防止失活后又续播下一轮。</summary>
        public void StopRoll()
        {
            _roll?.Kill(false);
            _roll = null;
        }
        private void OnDisable()
        {
            StopRoll();
        }
        private void OnDestroy()
        {
            StopRoll();
        }
    }
}
