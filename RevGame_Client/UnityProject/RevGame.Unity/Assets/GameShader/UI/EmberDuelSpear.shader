Shader "RevGame/UI/Ember Duel Spear"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
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
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" }
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "SpearGlow"
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            #include "DuelStrikeReveal.cginc"

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;

            // 保留 UGUI 提供的图集 UV。局部位置只用于遮罩和枪芒上的流动亮带。
            struct Attributes
            {
                float4 position : POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 reveal : TEXCOORD1;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 local : TEXCOORD1;
                float4 reveal : TEXCOORD2;
            };

            // 顶点颜色携带每次出招和命中独立的淡出，不需要给每个特效复制材质。
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.position);
                output.local = input.position.xy;
                output.uv = input.uv;
                output.reveal = input.reveal;
                output.color = input.color * _Color;
                return output;
            }

            // 只增强原图的亮红轮廓，亮带向枪尖推进。保留图集透明度和已有裁剪。
            half4 Frag(Varyings input) : SV_Target
            {
                half4 texel = tex2D(_MainTex, input.uv) + _TextureSampleAdd;
                half4 color = texel * input.color;
                half2 sweepMask = DuelStrikeReveal(input.reveal);
                color.a *= sweepMask.x;
                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(input.local, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001h);
                #endif
                half hot = smoothstep(0.24h, 0.85h, texel.r) * saturate((texel.r - texel.b) * 2.4h);
                float band = frac((input.local.x + input.local.y * 0.14) * 0.008 - _Time.y * 4.2);
                half sweep = 1 - smoothstep(0.02, 0.22, abs(band - 0.48));
                half heat = hot * (0.30h + sweep * 0.58h) * input.color.a;
                color.rgb = (color.rgb + half3(0.9h, 0.31h, 0.07h) * heat) * color.a;
                color.rgb += half3(1.0h, 0.44h, 0.12h) * sweepMask.y * color.a;
                return color;
            }
            ENDCG
        }
    }
}
