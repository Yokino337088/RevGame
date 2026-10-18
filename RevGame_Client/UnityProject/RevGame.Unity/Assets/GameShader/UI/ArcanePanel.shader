// ArcanePanel —— 保留原图与透明度，用共享材质实现轻量动态效果。
// 参数在GameRes/Material中编辑；UV1由ArcaneImageCoordinates提供，UV0始终用于原图采样。
Shader "RevGame/UI/ArcanePanel"
{
    Properties
    {
        [PerRendererData] _MainTex ("原图（由UI或Sprite提供）", 2D) = "white" {}
        _Color ("整体颜色", Color) = (1,1,1,1)
        _GlowColor ("魔法光泽颜色", Color) = (1,0.8,0.38,1)
        _Strength ("效果强度", Range(0,1)) = 0.22
        _Speed ("流动速度", Range(0,1)) = 0.15
        _ArtBlend ("背景保留原图比例", Range(0,1)) = 1
        // 以下与UGUI默认Shader一致，供Mask/RectMask2D及图集批处理使用。
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
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" "PreviewType"="Plane" }
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
            #pragma target 2.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #define ARCANE_PANEL
            #include "../Common/ArcaneUI.cginc"
            ENDCG
        }
    }
    Fallback "UI/Default"
}
