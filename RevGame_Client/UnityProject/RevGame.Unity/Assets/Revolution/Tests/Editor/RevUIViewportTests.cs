// ============================================================
// RevUIViewportTests.cs —— 在编辑态验证画幅数学和实际 RectTransform 落点。
// 测试临时创建 Canvas 与控件，不修改用户预制体或场景。
// ============================================================
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Revolution.Editor.Tests
{
    // 数值检查与真实 RectTransform 写入使用同一套生产 API，测试对象均在本轮销毁。
    public sealed class RevUIViewportTests
    {
        // 测试用横屏设计尺寸，单位为画布单位。
        private static readonly Vector2 Landscape = new Vector2(1920f, 1080f);
        // 允许归一化浮点计算的微小舍入差。
        private const float Epsilon = .0001f;

        // 基准横屏、超宽和窄屏均保持同一个设计宽高比。
        [TestCase(1920f, 1080f, 1f, 1f)]
        [TestCase(2560f, 1080f, .75f, 1f)]
        [TestCase(1024f, 768f, 1f, .75f)]
        public void DesignFrameFitsLandscapeWindows(float width, float height, float frameWidth, float frameHeight)
        {
            Rect frame = RevUIViewport.FitDesignFrame(new Vector2(width, height), RevUIViewport.Full, Landscape);

            Assert.That(frame.width, Is.EqualTo(frameWidth).Within(Epsilon));
            Assert.That(frame.height, Is.EqualTo(frameHeight).Within(Epsilon));
            Assert.That(frame.center, Is.EqualTo(new Vector2(.5f, .5f)));
            Assert.That(frame.width * width / (frame.height * height), Is.EqualTo(16f / 9f).Within(Epsilon));
        }

        // 竖屏安全区上下留边时，适配内容保持居中。
        [Test]
        public void PortraitDesignUsesNotchedScreenArea()
        {
            var pixels = new Vector2(1080f, 2400f);
            Rect safe = RevUIViewport.NormalizeSafeArea(new Rect(0f, 100f, 1080f, 2100f), new Rect(Vector2.zero, pixels));
            Rect frame = RevUIViewport.FitDesignFrame(pixels, safe, new Vector2(1080f, 1920f));

            Assert.That(frame.width, Is.EqualTo(1f).Within(Epsilon));
            Assert.That(frame.height, Is.EqualTo(.8f).Within(Epsilon));
            Assert.That(frame.center.y, Is.EqualTo(safe.center.y).Within(Epsilon));
            Assert.That(frame.yMin, Is.GreaterThan(safe.yMin));
            Assert.That(frame.yMax, Is.LessThan(safe.yMax));
        }

        // 像素视口偏移时需要相交后归一化，覆盖分屏和不对称安全区。
        [Test]
        public void SafeAreaAccountsForViewportPositionAndIntersection()
        {
            Rect safe = RevUIViewport.NormalizeSafeArea(new Rect(100f, 40f, 900f, 960f), new Rect(0f, 20f, 500f, 960f));

            Assert.That(safe.xMin, Is.EqualTo(.2f).Within(Epsilon));
            Assert.That(safe.xMax, Is.EqualTo(1f).Within(Epsilon));
            Assert.That(safe.yMin, Is.EqualTo(20f / 960f).Within(Epsilon));
            Assert.That(safe.yMax, Is.EqualTo(1f).Within(Epsilon));
        }

        // 屏幕初始化和非法尺寸用有效的回退矩形。
        [Test]
        public void MissingOrInvalidViewportDoesNotGenerateDegenerateLayout()
        {
            Assert.That(RevUIViewport.NormalizeSafeArea(Rect.zero, RevUIViewport.Full), Is.EqualTo(RevUIViewport.Full));
            Assert.That(RevUIViewport.NormalizeSafeArea(new Rect(3f, 3f, 1f, 1f), RevUIViewport.Full), Is.EqualTo(RevUIViewport.Full));
            Assert.That(RevUIViewport.FitDesignFrame(new Vector2(1920f, float.NaN), RevUIViewport.Full, Landscape),
                Is.EqualTo(RevUIViewport.Full));
            var safe = new Rect(.1f, .1f, .8f, .8f);
            Assert.That(RevUIViewport.FitDesignFrame(Landscape, safe, Vector2.zero).width, Is.EqualTo(safe.width).Within(Epsilon));
        }

        // 界面适配只写内容容器，子控件位置和原枢轴均保留。
        [Test]
        public void ApplyingDesignFramePreservesAuthoredPivotAndChildCoordinates()
        {
            var root = new GameObject("Viewport", typeof(RectTransform));
            try
            {
                var parent = (RectTransform)root.transform;
                parent.sizeDelta = new Vector2(2560f, 1440f);
                RectTransform content = NewRect("Content", parent);
                content.pivot = new Vector2(.2f, .8f);
                content.localScale = new Vector3(1f, 1f, 2f);
                RectTransform button = NewRect("Button", content);
                button.anchoredPosition = new Vector2(130f, -64f);
                Rect frame = RevUIViewport.FitDesignFrame(parent.rect.size, RevUIViewport.Full, Landscape);

                float scale = RevUIViewport.ApplyDesignFrame(content, frame, Landscape);

                Assert.That(content.rect.size, Is.EqualTo(Landscape));
                Assert.That(content.pivot, Is.EqualTo(new Vector2(.2f, .8f)));
                Assert.That(content.anchorMin, Is.EqualTo(content.pivot));
                Assert.That(content.anchorMax, Is.EqualTo(content.pivot));
                Assert.That(content.localScale.x, Is.EqualTo(4f / 3f).Within(Epsilon));
                Assert.That(content.localScale.y, Is.EqualTo(scale));
                Assert.That(content.localScale.z, Is.EqualTo(2f));
                Assert.That(button.anchoredPosition, Is.EqualTo(new Vector2(130f, -64f)));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // 同一个内容自适应组件验证参考尺寸继承、重排通知和两种策略切换。
        [Test]
        public void ComponentUsesCanvasReferenceAndReflowsOnViewportResize()
        {
            var root = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            try
            {
                root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var reference = new Vector2(1080f, 1920f);
                root.GetComponent<CanvasScaler>().referenceResolution = reference;
                RectTransform panel = NewRect("Panel", root.transform);
                panel.sizeDelta = new Vector2(1000f, 2000f);
                RectTransform content = NewRect("Content", panel);
                var layout = content.gameObject.AddComponent<RevUIAdaptiveLayout>();
                int changes = 0;
                layout.ViewportChanged += _ => changes++;

                layout.Configure(RevUIAdaptiveLayoutMode.FitDesignFrame);
                Assert.That(content.sizeDelta, Is.EqualTo(reference));
                Rect first = layout.Viewport;
                layout.Configure(RevUIAdaptiveLayoutMode.FitDesignFrame);
                Assert.That(changes, Is.EqualTo(1));

                panel.sizeDelta = new Vector2(1600f, 1200f);
                layout.Configure(RevUIAdaptiveLayoutMode.FitDesignFrame);

                Assert.That(layout.Viewport, Is.Not.EqualTo(first));
                Assert.That(changes, Is.EqualTo(2));

                layout.Configure(RevUIAdaptiveLayoutMode.SafeArea);
                Assert.That(content.localScale, Is.EqualTo(Vector3.one));
                Assert.That(content.offsetMin, Is.EqualTo(Vector2.zero));
                Assert.That(content.offsetMax, Is.EqualTo(Vector2.zero));
                Assert.That(layout.Viewport, Is.EqualTo(RevUIViewport.GetScreenSafeArea()));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // 创建本轮临时的真实 UI 节点，归测试根对象统一销毁。
        private static RectTransform NewRect(string name, Transform parent)
        {
            var node = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            node.SetParent(parent, false);
            return node;
        }
    }
}
