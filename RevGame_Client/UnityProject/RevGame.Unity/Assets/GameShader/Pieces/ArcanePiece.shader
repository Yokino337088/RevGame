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
            #pragma target 2.0
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

            fixed4 FragArcane(v2f input) : SV_Target
            {
                // 正常仅一次原图采样；安卓ETC1分离透明度时由Unity内置函数补读alpha。
                fixed4 source = SampleSpriteTexture(input.texcoord) * input.color;
                half lightness = dot(source.rgb, half3(0.299, 0.587, 0.114));
                // 斜向窄流光缓慢掠过，正弦只做颜色调制，不移动棋子轮廓或改变命中区域。
                half wave = sin((input.texcoord.x + input.texcoord.y * 0.65) * 12.0 - _Time.y * _Speed * 4.0);
                half glint = smoothstep(0.76, 1.0, wave);
                half pulse = 0.5 + 0.5 * sin(_Time.y * _Speed * 2.0 + input.texcoord.y * 7.0);
                half3 glow = _GlowColor.rgb * glint * (0.25 + lightness * 0.65);
                glow += _ArcaneColor.rgb * pulse * lightness * 0.18;
                source.rgb = saturate(source.rgb + glow * _Strength);
                // ★ 绝不改变alpha：透明区域仍透明，攻击/死亡/序列帧边缘不会出现矩形残影。
                source.rgb *= source.a;
                return source;
            }
            ENDCG
        }
    }
    Fallback "Sprites/Default"
}
