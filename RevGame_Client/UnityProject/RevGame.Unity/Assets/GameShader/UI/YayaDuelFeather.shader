Shader "RevGame/UI/Yaya Duel Feather"
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
            Name "Feather"
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

            // UV 直接来自 UGUI 的打包 Sprite。坐标用于裁剪，不重新映射图集区域。
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

            // 单次采样。只提亮玉色和金色像素，保留黑鸦轮廓、透明边缘和 UGUI 淡出。
            half4 Frag(Varyings input) : SV_Target
            {
                half4 color = (tex2D(_MainTex, input.uv) + _TextureSampleAdd) * input.color;
                half2 sweepMask = DuelStrikeReveal(input.reveal);
                color.a *= sweepMask.x;
                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(input.local, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001h);
                #endif
                half glow = saturate((color.g - color.b) * 2.5h) * smoothstep(0.16h, 0.7h, color.g);
                half pulse = .28h + .74h * DuelStrikeCurrent(input.reveal, _Time.y);
                color.rgb = (color.rgb + half3(0.43h, 0.90h, 0.23h) * glow * pulse
                    + half3(.68h, 1.0h, .36h) * sweepMask.y * .79h) * color.a;
                return color;
            }
            ENDCG
        }
    }
}
