// 参考用户视频：交叉纹亮起→深紫地面→外轮廓能量边→符文上浮→逐格退场。
// 每格共用一个材质，通过MPB传入起点/真实命中延迟/外边界，动画不改变伤害时序。
Shader "RevGame/Combat/RegionRift"
{
    Properties
    {
        [PerRendererData] _MainTex ("图集", 2D) = "white" {}
        [PerRendererData] _SpriteUV ("图集矩形", Vector) = (0,0,1,1)
        [PerRendererData] _Timing ("开始/命中延迟/结束/种子", Vector) = (0,1.5,3,0)
        [PerRendererData] _Age ("本次动画经过秒数", Float) = 0
        [PerRendererData] _Edges ("左/右/下/上外边界", Vector) = (1,1,1,1)
        _Color ("颜色", Color) = (1,1,1,1)
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
            #include "UnitySprites.cginc"
            float4 _SpriteUV,_Timing,_Edges; float _Age;
            fixed4 Frag(v2f i) : SV_Target
            {
                float2 uv = saturate((i.texcoord-_SpriteUV.xy)/max(_SpriteUV.zw-_SpriteUV.xy,.00001));
                float t = _Age;
                float border = max(max((1-smoothstep(0,.035,uv.x))*_Edges.x,(1-smoothstep(0,.035,1-uv.x))*_Edges.y),
                    max((1-smoothstep(0,.035,uv.y))*_Edges.z,(1-smoothstep(0,.035,1-uv.y))*_Edges.w));
                float cross = 1-smoothstep(.025,.065,min(abs(uv.x-uv.y),abs(uv.x+uv.y-1)));
                float diamond = sin((uv.x+uv.y)*12.56)*sin((uv.x-uv.y)*12.56);
                float warning = 1-smoothstep(.12,.55,t);
                float impact = saturate(1-abs(t-_Timing.y)*5);
                float cloud = sin(uv.x*8+uv.y*6-t*3+_Timing.w)+sin(uv.x*13-uv.y*9+t*2);
                float2 spark = float2(frac(_Timing.w*.37)+sin(t*2+_Timing.w)*.08,frac(t*.6+_Timing.w*.13));
                float rune = pow(saturate(1-length((uv-spark)*float2(25,14))),2);
                float energy = border*(.7+.3*sin(t*7+uv.x*5)) + cross*warning + rune + impact*.7;
                fixed3 dark = half3(.055,.012,.12) + half3(.09,.025,.16)*saturate(cloud*.25+.4) + diamond*.012;
                fixed3 color = lerp(dark,half3(.66,.22,1),saturate(energy));
                color += half3(.35,.7,1)*pow(saturate(energy),5);
                float fade = smoothstep(0,.12,t)*(1-smoothstep(_Timing.z-.35,_Timing.z,t));
                fixed4 c = fixed4(color,lerp(.9,1,saturate(energy))*fade)*i.color;
                c.rgb *= c.a; return c;
            }
            ENDCG
        }
    }
}
