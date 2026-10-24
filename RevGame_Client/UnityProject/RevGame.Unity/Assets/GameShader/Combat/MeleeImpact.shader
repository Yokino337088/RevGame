// Kenney透明斩弧/冲击环保留原轮廓；流光和盾面六边纹由共享Shader叠加，不生成每次命中的材质。
Shader "RevGame/Combat/MeleeImpact"
{
    Properties
    {
        [PerRendererData] _MainTex ("原画图集", 2D) = "white" {}
        [PerRendererData] _SpriteUV ("图集UV", Vector) = (0,0,1,1)
        [PerRendererData] _Progress ("本次动画进度", Float) = 0
        _Color ("染色", Color) = (1,1,1,1)
        _Glow ("职业光色", Color) = (0.3,0.8,1,1)
        _Shield ("盾击", Float) = 0
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("External Alpha", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" }
        Cull Off ZWrite Off Lighting Off Blend One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment Frag
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"
            float4 _SpriteUV; float _Progress, _Shield; fixed4 _Glow;
            fixed4 Frag(v2f i) : SV_Target
            {
                float2 uv = (i.texcoord-_SpriteUV.xy)/max(_SpriteUV.zw-_SpriteUV.xy,.00001);
                float2 p = uv*2-1;
                float age = _Progress * .34;
                fixed4 art = SampleSpriteTexture(i.texcoord);
                float sweep = pow(saturate(sin(uv.y*15-age*35)),6);
                float ring = pow(saturate(1-abs(length(p)-frac(age*2.4))/.09),2);
                float flash = saturate(1-age*8);
                float alpha = art.a;
                if (_Shield > .5)
                {
                    float hex = max(abs(p.y),abs(p.x)*.866+abs(p.y)*.5);
                    float edge = 1-smoothstep(.025,.055,abs(hex-.65));
                    alpha = max(alpha, (edge*.9 + ring*.7 + flash*.45) * (1-smoothstep(.8,1,length(p))));
                }
                fixed4 c = fixed4(lerp(_Glow.rgb,half3(1,1,1),sweep*.7+flash*.4),alpha)*i.color;
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
