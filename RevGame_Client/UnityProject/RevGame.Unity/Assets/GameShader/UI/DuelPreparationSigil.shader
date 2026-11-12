Shader "RevGame/UI/Duel Preparation Sigil"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite", 2D) = "white" {}
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
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "CanUseSpriteAtlas"="True" }
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
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct Attributes { float4 pos : POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };
            struct Varyings { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; float2 local : TEXCOORD1; };
            sampler2D _MainTex;
            fixed4 _Color;
            float4 _ClipRect;

            // 扫光用几何坐标，采样仍用原 Sprite UV，图集换页或裁切不会改动光带方向。
            Varyings vert(Attributes input)
            {
                Varyings output;
                output.pos = UnityObjectToClipPos(input.pos);
                output.local = input.pos.xy;
                output.uv = input.uv;
                output.color = input.color * _Color;
                return output;
            }

            // 光只出现在符印原有的透明轮廓内。材质跨席共享，席位色由顶点颜色传入。
            fixed4 frag(Varyings input) : SV_Target
            {
                fixed4 pixel = tex2D(_MainTex, input.uv) * input.color;
                float streak = saturate(sin(dot(input.local, float2(.013, -.032)) - _Time.y * 2.25));
                pixel.rgb = lerp(pixel.rgb, fixed3(1, .94, .77), pow(streak, 8) * .62);
                pixel.a *= .72 + .28 * streak;
                #ifdef UNITY_UI_CLIP_RECT
                pixel.a *= UnityGet2DClipping(input.local, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(pixel.a - .001);
                #endif
                return pixel;
            }
            ENDCG
        }
    }
}
