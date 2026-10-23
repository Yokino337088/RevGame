// 第一章原始教学背景专用：保留原有旋涡原画/色带混合算法和材质参数。
// 与大厅ArcaneBackdrop分成两个Shader资产，第一章不再因大厅材质而被预加载。
// _ArtUvRect继续接受图集局部UV，不能将整张图集当成一幅原画。
Shader "RevGame/Background/Chapter01_Tutorial"
{
    Properties
    {
        [PerRendererData] _MainTex ("原图（由UI或Sprite提供）", 2D) = "white" {}
        _Color ("整体颜色", Color) = (1,1,1,1)
        _GlowColor ("魔法光泽颜色（仅无图面板的色带高光）", Color) = (1,0.8,0.38,1)
        _Strength ("效果强度", Range(0,1)) = 0.22
        _Speed ("流动速度", Range(0,1)) = 0.15
        [HideInInspector] _EffectTime ("场景时钟（-1使用Unity时钟）", Float) = -1
        [HideInInspector] _PreviewTime ("固定测试时间", Float) = -1
        _ArtBlend ("原图比例（0=程序漩涡 1=原图本身流动）", Range(0,1)) = 1
        _ArtUvRect ("背景在图集内的矩形（xy起点 zw尺寸）", Vector) = (0,0,1,1)
        [Header(Art Swirl)]
        _ArtTwist ("原画中心扭转角（弧度，负数反向）", Range(-4,4)) = 1.9
        _ArtTwistRadius ("扭转范围（原画宽度的比例）", Range(0.05,0.6)) = 0.22
        _ArtFlow ("原画流动幅度", Range(0,0.4)) = 0.14
        _ArtSpin ("流动绕中心公转速度（负数为顺时针）", Range(-8,8)) = -4
        _ArtBreathe ("扭转呼吸幅度", Range(0,1)) = 0.45
        [Header(Swirl Field)]
        _SwirlScale ("漩涡密度（越大纹路越细越多）", Range(5,60)) = 30
        _SpinAmount ("卷曲程度（离中心越远转得越多）", Range(0,1)) = 0.25
        [Header(Procedural Panel Only)]
        _SwirlColorA ("主色带", Color) = (0.10,0.52,0.56,1)
        _SwirlColorB ("副色带", Color) = (0.46,0.20,0.62,1)
        _SwirlColorC ("墨底", Color) = (0.035,0.06,0.09,1)
        // 0=保持上面三色（战斗/选关统一墨绿紫）；1=按漩涡角度与半径走全色谱，形成五彩斑斓的虹彩带。
        _Rainbow ("彩虹色带（0=原配色 1=五彩斑斓）", Range(0,1)) = 0
        _SpinRotation ("色带公转速度（负数为顺时针）", Range(-5,5)) = -2
        _Contrast ("色带锐度", Range(1,8)) = 3.5
        _Lighting ("色带高光", Range(0,1)) = 0.4
        _PixelFilter ("像素化格数（屏幕对角线方向，0=关闭）", Range(0,2000)) = 745
        // 以下与UGUI默认Shader一致，供Mask/RectMask2D及图集批处理使用。
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" "PreviewType"="Plane" }
        Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // ★ 3.0：5轮三角迭代、ddx/ddy、tex2Dgrad 与按材质分支都超出 SM2.0。
            //   Android 图形 API 为 Vulkan + GLES3（ProjectSettings），PC 为 DX11，都满足 3.0。
            #pragma target 3.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 localPosition : TEXCOORD1;
                // 只有无图面板的程序漩涡用屏幕坐标（像素化要按屏幕像素算）；原画漩涡在原画自己的坐标里算。
                float4 screenPos : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color, _TextureSampleAdd, _GlowColor;
            float4 _ClipRect, _ArtUvRect;
            float _Strength, _Speed, _ArtBlend, _EffectTime, _PreviewTime;
            float _ArtTwist, _ArtTwistRadius, _ArtFlow, _ArtSpin, _ArtBreathe;
            float _SwirlScale, _SpinAmount;
            fixed4 _SwirlColorA, _SwirlColorB, _SwirlColorC;
            float _SpinRotation, _Contrast, _Lighting, _PixelFilter, _Rainbow;

            v2f vert(appdata input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.localPosition = input.vertex;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.color = input.color * _Color;
                output.uv = input.uv;
                output.screenPos = ComputeScreenPos(output.vertex);
                return output;
            }

            float2 Rotate(float2 v, float angle)
            {
                float c = cos(angle);
                float s = sin(angle);
                return float2(v.x * c - v.y * s, v.x * s + v.y * c);
            }

            // 螺旋角：离中心越远转得越多（_SpinAmount 越大，半径影响越大），并随时间整体公转。
            // 用“旋转向量”而不是 atan2 求角度，正中心那个像素不会出现 atan2(0,0) 的未定义值。
            float SpiralAngle(float radius, float time, float spin)
            {
                return time * spin * 0.2 - 20.0 * (_SpinAmount * radius + (1.0 - _SpinAmount));
            }

            // 三角函数迭代扭曲：每轮用上一轮结果再扰动坐标，螺旋被搅成液态卷曲的纹路。
            // ★ 次数固定为5、系数是经验值：再少卷不起来，再多只增加全屏片元开销；改系数图案会整体变样。
            // ★ 全程 float：坐标会被放大到几十，移动端 half 精度不够会出现色块。
            float2 SwirlIterate(float2 u, float flow)
            {
                float2 uv2 = float2(u.x + u.y, u.x + u.y);
                [unroll] for (int i = 0; i < 5; i++)
                {
                    uv2 += sin(max(u.x, u.y)) + u;
                    u += 0.5 * float2(cos(5.1123314 + 0.353 * uv2.y + flow * 0.131121), sin(uv2.x - 0.113 * flow));
                    u -= cos(u.x + u.y) - sin(u.x * 0.711 - u.y);
                }
                return u;
            }

            // ---------- 原画本身流动（战斗场景）----------
            // 只计算“这个像素该去原画的哪里取色”，颜色 100% 来自原画，不叠加任何颜色。
            // 全部在原画局部坐标里算，以原画中心为漩涡中心：GamePresentation 会把原画中心对准相机中心，
            // 所以 Game 视图里漩涡在屏幕中央，Scene 视图里看到的也是同一个漩涡（不随编辑器窗口变化）。
            fixed4 SwirlArt(float2 localUV, float artAspect, float time, float2 gradX, float2 gradY)
            {
                // 以原画宽度为 1 的“圆形”坐标：原画是竖图（宽:高 = artAspect），不乘回去漩涡会被拉成竖椭圆。
                float2 s = (localUV - 0.5) * float2(1.0, 1.0 / artAspect);
                float radius = length(s);
                // ① 扭转：越靠中心转得越多（高斯衰减），随时间轻微“呼吸”。
                //   用振荡而不是一直累加：内圈一直比外圈转得快，几十秒后画面会被拧成细丝。
                //   _Strength 乘在角度上而不是乘在位移上：缩短“旋转后的位移”会把中心往里收，变成放大镜。
                float falloff = exp(-(radius * radius) / max(_ArtTwistRadius * _ArtTwistRadius, 0.0001));
                float twist = _ArtTwist * _Strength * falloff * (1.0 + _ArtBreathe * sin(time * 0.9));
                float2 twisted = Rotate(s, twist);
                // ② 流动：把螺旋迭代场“扭曲前后的差”当位移，旋回原画方向再缩回原画尺度。
                //   位移场整体绕中心公转（_ArtSpin），原画纹理被一圈圈带着走，但不会越拧越紧。
                float phi = SpiralAngle(radius, time, _ArtSpin);
                float2 u0 = Rotate(s, phi) * _SwirlScale;
                float2 flowOffset = Rotate(SwirlIterate(u0, time * 7.0) - u0, -phi) / _SwirlScale;
                float2 delta = twisted - s + _ArtFlow * _Strength * flowOffset;
                // ③ 左右边缘渐弱：横屏时原画按宽度铺满，左右边正好贴着屏幕边，往外推会采到边缘像素被拉成条纹。
                //   0.12 = 原画宽度的 12%，与下面 5% 的内缩一起保证边缘位移不越界；纵向原画很长，不需要渐弱。
                delta *= saturate(min(localUV.x, 1.0 - localUV.x) / 0.12);
                float2 warped = 0.5 + (s + delta) * float2(1.0, artAspect);
                // 先内缩 5% 留出位移余量，再钳在半个像素内，最后映回图集矩形，防止读到图集里的邻图。
                float2 rectSize = max(_ArtUvRect.zw, float2(0.0001, 0.0001));
                float2 artUV = clamp(lerp(float2(0.05, 0.05), float2(0.95, 0.95), warped), 0.005, 0.995);
                // ★ tex2Dgrad 用未扭曲 UV 的导数：扭曲后的坐标在纹路折线处变化剧烈，隐式导数会在那里选错 Mip 出现细线；
                //   也让采样可以安全地放在按材质的分支里。
                return tex2Dgrad(_MainTex, _ArtUvRect.xy + artUV * rectSize, gradX, gradY) + _TextureSampleAdd;
            }

            // 色相 → RGB（饱和度高、明度可调）。彩虹模式用它按漩涡角度取色，比手写三色渐变更均匀。
            float3 HsvToRgb(float3 hsv)
            {
                float3 p = abs(frac(hsv.xxx + float3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
                return hsv.z * lerp(float3(1.0, 1.0, 1.0), saturate(p - 1.0), hsv.y);
            }

            // ---------- 程序漩涡（无图的大厅/选关/加载面板）----------
            float3 ProceduralSwirl(float2 pixel, float2 screen, float time)
            {
                float diagonal = length(screen);
                // 像素化：坐标吸附到方块格子中心，得到复古颗粒感；_PixelFilter = 屏幕对角线上的格子数，0 = 关闭。
                float2 swirlPixel = pixel;
                if (_PixelFilter >= 1.0)
                {
                    float cell = diagonal / _PixelFilter;
                    swirlPixel = floor(pixel / cell) * cell + cell * 0.5;
                }
                // 以屏幕中心为原点、按对角线归一化：不同分辨率、横竖屏下漩涡占屏比例一致。
                float2 uv = (swirlPixel - 0.5 * screen) / diagonal;
                uv = SwirlIterate(Rotate(uv, SpiralAngle(length(uv), time, _SpinRotation)) * _SwirlScale, time * 7.0);
                // 色带取色：扭曲后的长度落在 1 附近 → 主色带A，0 附近 → 副色带B，其余 → 墨底C。_Contrast 越大色带越窄越锐。
                float contrastMod = 0.25 * _Contrast + 0.5 * _SpinAmount + 1.2;
                float paint = min(2.0, max(0.0, length(uv) * 0.035 * contrastMod));
                float bandA = max(0.0, 1.0 - contrastMod * abs(1.0 - paint));
                float bandB = max(0.0, 1.0 - contrastMod * abs(paint));
                float bandC = 1.0 - min(1.0, bandA + bandB);
                float baseShare = 0.3 / max(_Contrast, 0.0001);
                // 彩虹模式：色相跟着“屏幕角度 + 半径 + 时间”转，主副色带错开约 0.45 圈色相，
                // 相邻两道色带永远是对比色，卷曲起来才是五彩斑斓而不是同色深浅。
                float hue = frac(atan2(uv.y, uv.x) / 6.2831853 + length(uv) * 0.22 + time * 0.04);
                float3 rainbowA = HsvToRgb(float3(hue, 0.92, 1.0));
                float3 rainbowB = HsvToRgb(float3(frac(hue + 0.45), 0.95, 1.0));
                float3 colorA = lerp(_SwirlColorA.rgb, rainbowA, _Rainbow);
                float3 colorB = lerp(_SwirlColorB.rgb, rainbowB, _Rainbow);
                float3 swirl = baseShare * colorA + (1.0 - baseShare) * (colorA * bandA + colorB * bandB + _SwirlColorC.rgb * bandC);
                // 高光：只有色带最中心那一窄条才亮（*5-4 把 0.8~1 拉成 0~1）。
                float light = (_Lighting - 0.2) * max(bandA * 5.0 - 4.0, 0.0) + _Lighting * max(bandB * 5.0 - 4.0, 0.0);
                // 彩虹模式高光用白色，避免暖金色把紫/蓝带染脏。
                float3 glow = lerp(_GlowColor.rgb, float3(1.0, 1.0, 1.0), _Rainbow);
                return swirl + glow * light * _Strength;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                float clock = _EffectTime >= 0.0 ? _EffectTime : _Time.y;
                float time = (_PreviewTime >= 0.0 ? _PreviewTime : clock) * _Speed;
                // 导数必须在任何分支之外求，分支里求导在部分 GPU 上结果未定义。
                float2 gradX = ddx(input.uv);
                float2 gradY = ddy(input.uv);
                float2 rectSize = max(_ArtUvRect.zw, float2(0.0001, 0.0001));
                float2 localUV = (input.uv - _ArtUvRect.xy) / rectSize;
                // 原画在屏幕上的宽高比 = 局部V每像素变化量 / 局部U每像素变化量。
                // 用导数求而不是写死 0.5：换一张不同比例的背景图不用改 Shader，也不依赖 _MainTex_TexelSize 是否被设置。
                float2 localDx = gradX / rectSize;
                float2 localDy = gradY / rectSize;
                float artAspect = length(float2(localDx.y, localDy.y)) / max(length(float2(localDx.x, localDy.x)), 1e-8);
                artAspect = clamp(artAspect, 0.05, 20.0);

                // 两条路径按材质分支：场景材质（=1）不付程序色带的开销，面板材质（=0）不采样贴图。
                // 两个分支条件都来自材质常量，同一次绘制里所有像素走同一边，不会产生分支发散。
                float3 procedural = float3(0.0, 0.0, 0.0);
                float3 art = float3(0.0, 0.0, 0.0);
                UNITY_BRANCH if (_ArtBlend < 0.999)
                {
                    float2 pixel = input.screenPos.xy / max(input.screenPos.w, 0.0001) * _ScreenParams.xy;
                    procedural = ProceduralSwirl(pixel, _ScreenParams.xy, time);
                }
                UNITY_BRANCH if (_ArtBlend > 0.001)
                {
                    art = SwirlArt(localUV, artAspect, time, gradX, gradY).rgb;
                }
                // 全屏背景强制不透明：原图即使有透明边，扭曲也不会把相机底色带进屏幕。
                fixed4 color = fixed4(lerp(procedural, art, _ArtBlend), 1.0);
                color *= input.color;
                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(input.localPosition.xy, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
                #endif
                return color;
            }
            ENDCG
        }
    }
    Fallback "UI/Default"
}
