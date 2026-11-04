// 小屋原画的局部魔法动效：RGB遮罩分别限定天空/水面/晶石药瓶发光，屋体与人物站台保持稳定。
Shader "RevGame/Background/LobbyMagicCottage"
{
    Properties
    {
        _ChapterTex ("大厅小屋原画", 2D) = "white" {}
        _RegionMask ("R天空G水面B发光遮罩", 2D) = "black" {}
        _WaterAmount ("水面微波幅度", Range(0,.01)) = .002
        _MagicGlow ("魔法局部柔光", Range(0,1)) = .18
        _SkyGlow ("天空日照变化", Range(0,.15)) = .025
        _EffectTime ("时钟 -1跟随Unity", Float) = -1
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _ChapterTex, _RegionMask;
            float _WaterAmount, _MagicGlow, _SkyGlow, _EffectTime;
            struct app { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
            v2f vert(app v)
            {
                v2f o;
                o.vertex=UnityObjectToClipPos(v.vertex);
                o.uv=v.uv;
                return o;
            }
            fixed4 frag(v2f i):SV_Target
            {
                float t=_EffectTime>=0?_EffectTime:_Time.y;
                float3 mask=tex2D(_RegionMask,i.uv).rgb;
                // 水波只在G区域取样偏移，岸线以遮罩淡出，绝不让整栋小屋漂浮/扭转。
                float2 uv=i.uv;
                uv.x+=sin(uv.y*68+t*.8)*_WaterAmount*mask.g;
                uv.y+=sin(uv.x*46-t*.65)*_WaterAmount*.45*mask.g;
                fixed3 color=tex2D(_ChapterTex,saturate(uv)).rgb;
                float water=(.5+.5*sin(uv.y*90+sin(uv.x*28)+t*.9))*mask.g;
                color+=fixed3(.08,.16,.17)*water*.09;
                // 局部魔法光不扩大轮廓，只温柔呼吸；天空大面积变化保持低强度，避免看久眩晕。
                color+=fixed3(.25,.9,.48)*mask.b*(.62+.38*sin(t*1.15))*_MagicGlow;
                color+=fixed3(1,.88,.6)*mask.r*_SkyGlow*(.5+.5*sin(uv.x*7+t*.18));
                return fixed4(saturate(color),1);
            }
            ENDCG
        }
    }
}
