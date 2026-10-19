// ArcanePiece —— 史莱姆身体的古铜魔光与青绿流光。
// 为什么用单Pass：棋子数量多，描边额外Pass/屏幕抓取会放大手机透明过绘成本。
// 待机保持克制光泽；入场为炽烈火舌，升级为蓝色分叉雷电，死亡真正侵蚀原图alpha。
// 三种效果都在原棋子上完成，避免额外特效对象；死亡多采样一次原图，只用来表现液体拖拽。
Shader "RevGame/Pieces/ArcanePiece"
{
    Properties
    {
        [PerRendererData] _MainTex ("棋子当前动画帧", 2D) = "white" {}
        _Color ("整体颜色", Color) = (1,1,1,1)
        _GlowColor ("古铜高光", Color) = (1,0.72,0.29,1)
        _ArcaneColor ("青绿魔力", Color) = (0.22,0.79,0.72,1)
        _Strength ("魔光强度（建议0.15至0.35）", Range(0,1)) = 0.28
        _Speed ("光泽流速", Range(0,1)) = 0.24
        // 仅死亡中的原棋子通过 MaterialPropertyBlock 逐实例写入；存活棋子保持 0，不改共享材质。
        [PerRendererData] _Dissolve ("死亡溶解进度", Range(0,1)) = 0
        [PerRendererData] _Birth ("落子身体燃烧进度（首尾均无火）", Range(0,1)) = 0
        [PerRendererData] _Storm ("升级雷暴进度", Range(0,1)) = 0
        [PerRendererData] _DissolveUV ("当前帧图集UV矩形", Vector) = (0,0,1,1)
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }
        Cull Off
        Lighting Off
        ZWrite Off
        // Unity Sprite使用预乘透明度：输出RGB最后乘alpha，避免边缘出现黑边/发光方框。
        Blend One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            // 稀疏动态分支仅在落地/升星/死亡期间生效；正常棋子仍单次采样。项目 Android 使用 GLES3/Vulkan。
            #pragma target 3.0
            #pragma vertex SpriteVert
            #pragma fragment FragArcane
            #pragma multi_compile_instancing
            #pragma multi_compile _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"
            fixed4 _GlowColor;
            fixed4 _ArcaneColor;
            half _Strength;
            half _Speed;
            half _Dissolve;
            half _Birth;
            half _Storm;
            float4 _DissolveUV;
            float4 _MainTex_TexelSize;

            // 用原图局部坐标而非图集绝对UV：不管棋子被打到Atlas V2的哪一角，每张帧都从自身轮廓溶解。
            // 固定频率的双层 value noise 不依赖贴图，也不引入第二个 Pass / RenderTexture。
            half DissolveHash(float2 cell)
            {
                return frac(sin(dot(cell, float2(127.1, 311.7))) * 43758.5453);
            }

            half DissolveNoise(float2 uv)
            {
                float2 cell = floor(uv);
                float2 blend = frac(uv);
                blend = blend * blend * (3.0 - 2.0 * blend);
                half bottom = lerp(DissolveHash(cell), DissolveHash(cell + float2(1, 0)), blend.x);
                half top = lerp(DissolveHash(cell + float2(0, 1)), DissolveHash(cell + float2(1, 1)), blend.x);
                return lerp(bottom, top, blend.y);
            }

            fixed4 FragArcane(v2f input) : SV_Target
            {
                // 正常仅一次原图采样；安卓ETC1分离透明度时由Unity内置函数补读alpha。
                float2 localUV = (input.texcoord - _DissolveUV.xy) / max(_DissolveUV.zw, float2(0.0001, 0.0001));
                half erosion = 0.0;
                half fineErosion = 0.0;
                // 原始alpha是“这颗棋子真实占有的像素”。死亡拖拽只扭曲RGB，不能拿扭曲后的alpha
                // 当轮廓，否则上一刻已被腐蚀的孔洞可能在下一刻又被旁边的图案填回来。
                fixed4 source = SampleSpriteTexture(input.texcoord) * input.color;
                if (_Dissolve > 0.0)
                {
                    // ★ 侵蚀场固定在原图上：粗噪声制造大块破口，细噪声制造虫蚀般的锯齿和细丝。
                    // 它不能随_Time滚动；只有阈值单调增加，才能保证“被毒液吃掉的像素永不长回来”。
                    half coarse = DissolveNoise(localUV * float2(7.5, 5.0) + float2(2.1, 5.7));
                    fineErosion = DissolveNoise(localUV * float2(23.0, 15.0));
                    half field = coarse * 0.64 + fineErosion * 0.28 + (1.0 - localUV.y) * 0.08;
                    erosion = saturate((field - 0.14) / 0.72);
                    // 残存图案向下拖曳，纵向噪声让各列下淌速度不同，形成黏液拉丝。
                    // 限制在当前Atlas帧的半像素内边界，双线性采样也不会把邻居图片拖进来。
                    float pull = _Dissolve * _Dissolve;
                    float wobble = sin(localUV.y * 19.0 - _Dissolve * 13.0 + coarse * 7.0) * 0.035 * pull;
                    float drip = (0.06 + coarse * 0.20) * pull;
                    float2 inset = min(0.49, _MainTex_TexelSize.xy * 0.5 / max(_DissolveUV.zw, 0.0001));
                    float2 liquidUV = clamp(localUV + float2(wobble, drip), inset, 1.0 - inset);
                    float2 texUV = _DissolveUV.xy + liquidUV * _DissolveUV.zw;
                    fixed4 dragged = SampleSpriteTexture(texUV) * input.color;
                    source.rgb = lerp(source.rgb, dragged.rgb, dragged.a * _Dissolve * 0.85);
                }
                half lightness = dot(source.rgb, half3(0.299, 0.587, 0.114));
                // 斜向窄流光缓慢掠过，正弦只做颜色调制，不移动棋子轮廓或改变命中区域。
                half wave = sin((input.texcoord.x + input.texcoord.y * 0.65) * 12.0 - _Time.y * _Speed * 4.0);
                half glint = smoothstep(0.76, 1.0, wave);
                half pulse = 0.5 + 0.5 * sin(_Time.y * _Speed * 2.0 + input.texcoord.y * 7.0);
                half3 glow = _GlowColor.rgb * glint * (0.25 + lightness * 0.65);
                glow += _ArcaneColor.rgb * pulse * lightness * 0.18;
                source.rgb = saturate(source.rgb + glow * _Strength);
                if (_Dissolve > 0.0)
                {
                    // 从多处破口侵蚀，而不是整张图逐渐透明。distance<0的像素已被吃掉，
                    // distance略大于0是正在腐蚀的边缘，更远处是尚未消失的身体。
                    half threshold = lerp(-0.04, 1.02, _Dissolve);
                    half distance = erosion - threshold;
                    half coverage = smoothstep(0.0, 0.035, distance);
                    half infection = (1.0 - smoothstep(0.03, 0.27, distance)) * smoothstep(0.0, 0.14, _Dissolve);
                    half rim = (1.0 - smoothstep(0.02, 0.075, distance)) * coverage;
                    half bubbles = smoothstep(0.67, 0.88, fineErosion) * infection;
                    // 黑绿色黏液先浸透图片，再以酸绿细边吃出孔洞。原图亮度仍决定明暗，保留图案细节。
                    half3 soaked = source.rgb * half3(0.11, 0.32, 0.12);
                    source.rgb = lerp(source.rgb, soaked, infection * 0.90);
                    half3 venom = lerp(half3(0.045, 0.42, 0.015), half3(0.28, 1.0, 0.045), fineErosion);
                    source.rgb = lerp(source.rgb, venom, saturate(rim * 0.96 + bubbles * 0.45));
                    source.a *= coverage;
                }
                else if (_Birth > 0.0)
                {
                    // ★ 火焰附着在当前序列帧上，而不是用一条火线把身体擦出来。
                    // 下方的噪声往上滚动，粗细两层合成断续的火舌；只改 RGB，绝不扩大原图 alpha，
                    // 所以透明区不会长出矩形色块，眼睛、表情和身体轮廓也仍来自棋子本身。
                    float fireTime = _Birth * 5.5;
                    float2 fireUV = float2(localUV.x * 7.0, localUV.y * 3.4 - fireTime);
                    // 横向轻微摆动使火舌卷曲，避免看起来像一层匀速上移的灰色噪点。
                    fireUV.x += sin(localUV.y * 8.0 - fireTime * 3.0) * 0.24;
                    half coarseFire = DissolveNoise(fireUV);
                    half fineFire = DissolveNoise(fireUV * 2.1 + float2(3.7, -fireTime * 0.65));
                    half tongues = smoothstep(0.26, 0.76, coarseFire * 0.72 + fineFire * 0.28);
                    half heat = tongues * lerp(1.0, 0.78, saturate(localUV.y));
                    // 首 12% 渐燃，末 30% 渐熄；进度=1 与完全关闭分支的结果严格相同。
                    // 不能在最后一帧再额外提亮/变白，否则玩家会看到“燃烧结束突然闪一下”。
                    half envelope = smoothstep(0.0, 0.12, _Birth) * (1.0 - smoothstep(0.70, 1.0, _Birth));
                    // 用有上限的混色代替高亮加法：火色保留红/橙色差，RGB 不会一起冲到白色。
                    // 亮度来自原图，暗部与眼睛保留层次；暖色火舌覆盖身体的不同区域，而非整颗同色闪烁。
                    // 窄亮脉络随火舌一同翻卷，深红焦边与橙黄火芯拉开对比，小棋子上也能认出火焰。
                    // 蓝通道始终低于0.04，即使火芯变亮也不会整颗变成白色。
                    half vein = 1.0 - smoothstep(0.025, 0.11, abs(coarseFire * 0.72 + fineFire * 0.28 - 0.54));
                    half3 ember = half3(0.65, 0.012, 0.002);
                    half3 flame = lerp(ember, half3(1.0, 0.62, 0.025), saturate(heat + vein * 0.40));
                    flame *= 0.20 + lightness * 0.80;
                    source.rgb = lerp(source.rgb, flame, envelope * (0.74 + tongues * 0.22));
                }
                else if (_Storm > 0.0)
                {
                    // 两条主电弧和一条斜向分叉反复跳动；外层蓝色电晕更宽，内层青色电芯更亮。
                    // 颜色混合有上限，保持蓝/青色识别点，绝不靠整张图片闪白制造冲击力。
                    float strike = floor(_Storm * 9.0);
                    float row = floor(localUV.y * 13.0);
                    float zig = (DissolveHash(float2(row, strike)) - 0.5) * 0.19;
                    float arcA = abs(localUV.x - (0.38 + zig + sin(localUV.y * 11.0 + _Storm * 14.0) * 0.08));
                    float arcB = abs(localUV.x - (0.69 - zig + sin(localUV.y * 16.0 - _Storm * 10.0) * 0.07));
                    float branch = abs(localUV.x - (0.18 + localUV.y * 0.70 - zig * 0.65));
                    float arc = min(min(arcA, arcB), branch);
                    half corona = 1.0 - smoothstep(0.018, 0.095, arc);
                    half core = 1.0 - smoothstep(0.008, 0.026, arc);
                    half thunder = 0.58 + 0.42 * pow(saturate(sin(_Storm * 36.0)), 2.0);
                    half envelope = smoothstep(0.0, 0.08, _Storm) * (1.0 - smoothstep(0.76, 1.0, _Storm));
                    half shock = 1.0 - smoothstep(0.018, 0.065, abs(length((localUV - 0.5) * 1.25) - frac(_Storm * 2.0) * 0.90));
                    half3 charged = source.rgb * half3(0.50, 0.72, 1.15);
                    source.rgb = lerp(source.rgb, charged, envelope * 0.35);
                    half3 electricity = lerp(half3(0.035, 0.20, 1.0), half3(0.12, 0.82, 1.0), core);
                    half spark = saturate(corona * 0.94 + shock * 0.68) * envelope * thunder;
                    source.rgb = lerp(source.rgb, electricity, spark);
                }
                // 三种动画仍沿用 Unity Sprite 的预乘透明度，清理参数块后存活棋子不执行任何特效分支。
                source.rgb *= source.a;
                return source;
            }
            ENDCG
        }
    }
    Fallback "Sprites/Default"
}
