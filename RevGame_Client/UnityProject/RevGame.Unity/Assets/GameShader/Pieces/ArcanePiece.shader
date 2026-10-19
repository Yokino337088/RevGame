// ArcanePiece —— 史莱姆身体的古铜魔光与青绿流光。
// 为什么用单Pass：棋子数量多，描边额外Pass/屏幕抓取会放大手机透明过绘成本。
// 保留原图、阵营颜色、透明边缘和逐帧Sprite；仅在原图可见部分叠一层克制的金属光泽。
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
        [PerRendererData] _Birth ("落地浴火进度", Range(0,1)) = 0
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
                float2 texUV = input.texcoord;
                half erosion = 0.0;
                if (_Dissolve > 0.0)
                {
                    // ★ 液体消融不仅改透明度：采样位置随波纹向下拉伸，让原棋子自身像融化的颜料往下淌。
                    // 图集V2只能在当前帧的小矩形内挪采样，钳住局部UV，不能碰到邻居棋子的图。
                    half coarse = DissolveNoise(localUV * 10.0 + float2(0.0, _Time.y * 0.4));
                    half fine = DissolveNoise(localUV * 23.0);
                    erosion = localUV.y * 0.70 + (coarse * 0.68 + fine * 0.32) * 0.30;
                    float wobble = sin(localUV.y * 16.0 - _Time.y * 3.0 + coarse * 6.0) * 0.015;
                    float drip = (0.04 + fine * 0.06) * _Dissolve;
                    float2 liquidUV = saturate(localUV + float2(wobble * _Dissolve, drip));
                    texUV = _DissolveUV.xy + liquidUV * _DissolveUV.zw;
                }
                // 正常与消融都只采样一次当前Sprite；安卓ETC1分离透明度由Unity内置函数补读alpha。
                fixed4 source = SampleSpriteTexture(texUV) * input.color;
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
                    // 侵蚀从下往上推进，但噪声让边界呈不规则液滴；值越小的像素越早消失。
                    // 边界用"焦化暗边+一点余烬橙"标识：暗边是压暗不是提亮，原图永远没被盖住。
                    half threshold = lerp(-0.08, 1.08, _Dissolve);
                    half edge = saturate((erosion - threshold) / 0.08);
                    half burn = saturate((1.0 - edge) * edge * 4.0);
                    // 绿色溶解：整体随进度染绿（乘法保留原图细节），边界为深绿暗边+亮绿荧光。
                    source.rgb = lerp(source.rgb, source.rgb * half3(0.55, 1.0, 0.55), _Dissolve);
                    source.rgb = lerp(source.rgb, half3(0.03, 0.22, 0.08), burn * 0.55);
                    source.rgb += half3(0.15, 0.95, 0.30) * burn * 0.35;
                    source.a *= smoothstep(0.0, 0.055, erosion - threshold);
                }
                else if (_Birth > 0.0)
                {
                    // 浴火入场：只让棋子真实像素从下往上逐层显形；火光集中在上升火线的窄带里，
                    // 且只画在已有像素上（乘 source.a）——不再整片染橙/染白，原图从头到尾都看得到。
                    // front 在 _Birth=1 时恰好越过头顶（1.12），配合 Linear 缓动，全程匀速不拖尾。
                    float flameWave = sin(localUV.x * 23.0 - _Time.y * 12.0) * 0.045
                        + sin(localUV.x * 49.0 + _Time.y * 17.0) * 0.022;
                    float front = _Birth * 1.24 - 0.12 + flameWave;
                    half reveal = smoothstep(localUV.y - 0.05, localUV.y + 0.03, front);
                    half band = saturate(1.0 - abs(localUV.y - front) / 0.10) * source.a;
                    half flicker = 0.55 + 0.45 * sin(_Time.y * 21.0 + localUV.x * 31.0);
                    // 红色火焰：火线下方的像素先被烧红（乘法染色保留原图），火线本身用纯红叠加，不再有白/黄高光。
                    half heat = saturate(1.0 - (front - localUV.y) / 0.35) * reveal;
                    source.rgb = lerp(source.rgb, source.rgb * half3(1.5, 0.35, 0.28), heat * 0.75);
                    source.rgb += half3(1.0, 0.10, 0.03) * band * flicker * 0.60;
                    source.a *= reveal;
                }
                else if (_Storm > 0.0)
                {
                    // 升级雷暴：识别点=细电弧+扩散冲击环，全部走加法勾边且乘 source.a，
                    // 不再把身体 lerp 成白/蓝大色块；棋子原图全程可见，只被电光瞬时勾亮。
                    // envelope 0.9~1 才收尾，不再留"画面已空但动画还在跑"的长尾巴。
                    float strike = floor(_Storm * 12.0);
                    float row = floor(localUV.y * 13.0);
                    float zig = (DissolveHash(float2(row, strike)) - 0.5) * 0.19;
                    float arcA = abs(localUV.x - (0.38 + zig + sin(localUV.y * 11.0 + _Storm * 14.0) * 0.08));
                    float arcB = abs(localUV.x - (0.69 - zig + sin(localUV.y * 16.0 - _Storm * 10.0) * 0.07));
                    half bolt = max(1.0 - smoothstep(0.006, 0.045, arcA), 1.0 - smoothstep(0.006, 0.045, arcB));
                    half thunder = pow(saturate(sin(_Storm * 42.0) + 0.32), 3.0);
                    half envelope = smoothstep(0.0, 0.05, _Storm) * (1.0 - smoothstep(0.90, 1.0, _Storm));
                    half shock = 1.0 - smoothstep(0.012, 0.05, abs(length((localUV - 0.5) * 1.4) - _Storm * 0.85));
                    float2 rim = smoothstep(0.0, 0.065, localUV) * smoothstep(0.0, 0.065, 1.0 - localUV);
                    half spark = (bolt + shock * 0.7) * envelope * rim.x * rim.y * source.a;
                    // 蓝色雷电：电弧用饱和蓝；闪烁时用乘法把原图染蓝，保留棋子细节，不向白色靠拢。
                    source.rgb += half3(0.10, 0.40, 1.0) * spark * (0.55 + thunder * 0.35);
                    source.rgb = lerp(source.rgb, source.rgb * half3(0.45, 0.70, 1.6), thunder * envelope * 0.55 * source.a);
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
