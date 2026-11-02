using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessArena
    {
        /// <summary>天元禁放标记：真实下载的20帧红色独眼，Prefab只引用图集Sprite，不加载散图。</summary>
        [SerializeField] private SpriteRenderer heartEye;
        [SerializeField] private Sprite[] heartFrames;
        /// <summary>棋心就地血条和底条，材质与图片都由Prefab共享；不是每帧新建UI。</summary>
        [SerializeField] private SpriteRenderer heartBar, heartTrack;
        /// <summary>本次租约已显示帧号；出池置-1以强制第一次落屏，范围0~19。</summary>
        private int _heartFrame = -1;
        /// <summary>上一次显示的生命比例0~1，只作显示缓存，不负责真实扣血。</summary>
        private float _heartRatio = -1;

        /// <summary>只在帧号/血量改变时写入，避免重复脏写；动画使用无缩放时间，暂停时仍显示禁放眼。</summary>
        private void UpdateHeart(EndlessService service)
        {
            if (heartEye == null || heartFrames == null || heartFrames.Length == 0)
            {
                Error = "棋心独眼序列帧尚未绑定";
                return;
            }
            int frame = (int)(Time.unscaledTime * 8) % heartFrames.Length;
            if (frame != _heartFrame)
            {
                _heartFrame = frame;
                heartEye.sprite = heartFrames[frame];
            }
            float ratio = Mathf.Clamp01(service.Run.heart / EndlessConfig.Value("HeartHp"));
            if (ratio != _heartRatio)
            {
                _heartRatio = ratio;
                heartBar.transform.localScale = new Vector3(.82f * ratio, .08f, 1);
                heartBar.transform.localPosition = new Vector3(4 - .41f * (1 - ratio), 4.55f, -.8f);
                heartBar.color = ratio < .3f ? new Color(1, .2f, .15f) : new Color(.7f, .1f, .14f);
            }
        }
        /// <summary>真实可画状态，供过场屏障和开发态断言检查。</summary>
        public bool HeartReady
        {
            get
            {
                return heartEye != null && heartEye.sprite != null && heartEye.sprite.texture != null && heartBar != null && heartTrack != null;
            }
        }
    }
}
