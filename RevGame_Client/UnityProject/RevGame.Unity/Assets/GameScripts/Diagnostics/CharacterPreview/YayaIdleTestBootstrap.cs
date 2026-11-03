// 仅TestScene使用：为直接摆入场景的Sprite动画提供正式AB图集绑定。
using Revolution;
using RevGame.View;
using UnityEngine;
namespace RevGame.Diagnostics
{
    [DefaultExecutionOrder(-10000)]
    public sealed class YayaIdleTestBootstrap : MonoBehaviour
    {
        private GameSpriteAtlasService _atlas;
        private void Awake()
        {
#if UNITY_EDITOR
            RevResBootstrap.UseABInEditor = true;
#endif
            RevInitialize.Initialize();
            _atlas = new GameSpriteAtlasService();
        }
        // Unity生命周期薄适配，异步加载仍使用RevTask；加载失败记录明确原因。
        private async void Start()
        {
            try
            {
                await _atlas.PreloadAsync(new[] { "YayaIdle24" });
                GameObject preview = GameObject.Find("YayaIdle24Preview");
                if (preview != null)
                {
                    SpriteRenderer renderer = preview.GetComponent<SpriteRenderer>();
                    Sprite sprite = renderer.sprite;
                    renderer.sprite = null;
                    renderer.sprite = sprite;
                    preview.GetComponent<Animator>().Play(0, 0, 0);
                }
            }
            catch (System.Exception error)
            {
                RevLog.Error("鸦鸦TestScene图集加载失败：" + error, "RevGame");
            }
        }
        private void OnDestroy()
        {
            if (_atlas != null)
            {
                _atlas.Dispose();
                _atlas = null;
            }
        }
    }
}
