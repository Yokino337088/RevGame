// 远程职业弹道：真实原画只采样一次，程序层补足流动熔岩、寒流、圣光和拉丝拖尾。
// 每次发射传入图集矩形；效果使用0~1局部UV，不扭曲原图采样，不会串到邻图。
Shader "RevGame/Combat/RangedProjectile"
{
    Properties
    {
        [PerRendererData] _MainTex ("原画图集", 2D) = "white" {}
        _Color ("颜色", Color) = (1,1,1,1)
        [PerRendererData] _SpriteUV ("图集最小最大UV", Vector) = (0,0,1,1)
        _Style ("职业：箭0 弩1 水2 火3 圣光4", Float) = 0
        _Trail ("拖尾层", Float) = 0
        _Glow ("魔力颜色", Color) = (1,0.8,0.25,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "CanUseSpriteAtlas"="True" }
        Cull Off Lighting Off ZWrite Off
        Blend One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment Frag
            #pragma target 2.0
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"
            float4 _SpriteUV;
            half _Style, _Trail;
            fixed4 _Glow;

            fixed4 Frag(v2f i) : SV_Target
            {
                float2 uv = saturate((i.texcoord - _SpriteUV.xy) / max(_SpriteUV.zw - _SpriteUV.xy, 0.00001));
                float t = _Time.y;
                float2 p = uv * 2 - 1;
                fixed4 c = SampleSpriteTexture(i.texcoord);
                if (_Trail > 0.5)
                {
                    // 左端散去、右端汇入弹头；三条不同频率的细丝表现空气、寒流或火舌。
                    float bend = sin(uv.x * 14 - t * 12) * 0.18 * (1 - uv.x);
                    float width = lerp(0.025, 0.36, uv.x);
                    float core = saturate(1 - abs(p.y - bend) / width);
                    float filament = pow(saturate(1 - abs(p.y + bend * 1.8 - 0.18 * sin(uv.x * 24 - t * 15)) / 0.09), 2);
                    float fade = smoothstep(0, 0.22, uv.x) * (1 - smoothstep(0.85, 1, uv.x));
                    float streak = core * core + filament * 0.3;
                    c.a = saturate(streak * fade);
                    c.rgb = lerp(_Glow.rgb, half3(1,1,0.95), core * 0.65);
                }
                else if (_Style > 2.5 && _Style < 3.5)
                {
                    // 两层弯曲熔岩裂纹在火球内部流动；暗红岩壳与黄白高温裂口同时存在。
                    float veins = sin(p.x * 10 - t * 4 + sin(p.y * 7 + t * 1.7) * 1.6);
                    veins += sin(p.y * 9 + t * 3 + cos(p.x * 8 - t * 2)) * 0.6;
                    float crack = pow(saturate(1 - abs(veins) * 5), 2);
                    float radius = length((uv - float2(0.68,0.5)) * float2(3,2.5));
                    float rock = 1 - smoothstep(0.55, 1, radius);
                    float hot = saturate(1 - radius);
                    c.rgb = lerp(c.rgb, half3(0.3,0.035,0.012) * (0.6 + hot), rock * 0.85);
                    c.rgb += half3(1,0.58,0.06) * crack * (0.2 + rock * 1.1);

                }
                else if (_Style > 1.5 && _Style < 2.5)
                {
                    float wave = pow(saturate(sin(length(p) * 22 - t * 9 + p.x * 5)), 6);
                    c.rgb = c.rgb * half3(0.55,0.95,1) + half3(0.18,0.65,0.8) * wave * 0.5;
                }
                else if (_Style > 3.5)
                {
                    float ring = pow(saturate(sin(length(p) * 22 - t * 5)), 10);
                    float crossLight = pow(saturate(1 - min(abs(p.x),abs(p.y)) * 7), 3);
                    c.rgb = c.rgb * half3(1,0.87,0.48) + _Glow.rgb * (ring * 0.35 + crossLight * 0.45);
                }
                else
                {
                    float glint = pow(saturate(sin(uv.x * 14 - t * 11)), 14);
                    c.rgb += _Glow.rgb * glint * 0.25;
                }
                c *= i.color;
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
