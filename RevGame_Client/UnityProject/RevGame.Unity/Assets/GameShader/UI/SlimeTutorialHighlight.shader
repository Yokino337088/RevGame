// 史莱姆魔法引导：流动的软边、绕行光珠和呼吸闪烁。使用普通透明混合，保持文字与棋子可辨认。
Shader "RevGame/UI/SlimeTutorialHighlight"
{
    Properties
    {
        [PerRendererData] _MainTex ("UI Texture", 2D) = "white" {}
        _Color ("薄荷魔法颜色", Color) = (0.28,0.76,0.65,0.55)
        _Accent ("淡紫流光颜色", Color) = (0.64,0.56,0.83,1)
        _Width ("边线宽度（屏幕像素）", Range(1,8)) = 4.5
        _PulseDepth ("呼吸深度", Range(0,0.85)) = 0.35
        _PulseRate ("每秒呼吸次数", Range(0.2,2)) = 0.85
        _Fill ("中心提示透明度", Range(0,0.2)) = 0.04
        _Speed ("流动速度", Range(0,3)) = 0.8
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
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "CanUseSpriteAtlas"="False" }
        Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
        Cull Off Lighting Off ZWrite Off ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; float4 local : TEXCOORD1; fixed4 color : COLOR; UNITY_VERTEX_OUTPUT_STEREO };
            fixed4 _Color, _Accent;
            float _Width, _Speed, _PulseDepth, _PulseRate, _Fill;
            float4 _ClipRect;
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.local = v.vertex; o.vertex = UnityObjectToClipPos(v.vertex); o.uv = v.uv; o.color = v.color;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                // 用屏幕像素计算圆角矩形距离，手牌和扁按钮不会出现一边粗一边细。
                float2 size = 1.0 / max(fwidth(i.uv), float2(0.0001,0.0001));
                float2 p = (i.uv - 0.5) * size;
                float radius = min(10.0, min(size.x,size.y)*0.18);
                float2 q = abs(p) - max(size*0.5 - 6.0 - radius, 0.0);
                float d = length(max(q,0.0)) + min(max(q.x,q.y),0.0) - radius;
                float t = _Time.y * _Speed;
                float wave = sin(i.uv.x*18.0+i.uv.y*13.0-t*2.0)*1.2;
                float edge = abs(d + wave);
                float rim = 1.0-smoothstep(_Width*0.35,_Width+1.2,edge);
                float halo = (1.0-smoothstep(_Width,9.0,edge))*0.22;
                float flow = 0.5+0.5*sin(i.uv.x*12.0-i.uv.y*9.0+t);
                // 整圈明暗变化让落点具有清楚的闪烁节奏；最低仍保留轮廓，避免目标瞬间消失。
                float pulse = 1.0-_PulseDepth*(0.5+0.5*sin(_Time.y*_PulseRate*6.2831853));
                float beads = pow(saturate(sin(i.uv.x*24.0+i.uv.y*18.0-t*3.0)),12.0)*rim;
                float shadow = (1.0-smoothstep(_Width+0.6,_Width+2.0,edge))*0.48;
                float core = saturate(rim+halo+beads*0.3);
                float fill = (1.0-smoothstep(-1.0,1.0,d))*_Fill*pulse;
                float opacity = max(max(core*_Color.a*pulse,shadow*pulse),fill);
                float3 magic = lerp(_Color.rgb,_Accent.rgb,flow*0.5);
                magic = lerp(magic,float3(0.68,0.96,0.78),beads*0.4);
                // 深绿衬边保证浅色棋盘上也能看清；不使用加法叠光，不把整块目标刷成白色。
                float3 rgb = lerp(float3(0.07,0.22,0.19),magic,max(core,fill>0.0?1.0:0.0));
                fixed4 col = fixed4(rgb,opacity);
                col *= i.color;
                #ifdef UNITY_UI_CLIP_RECT
                col.a *= UnityGet2DClipping(i.local.xy,_ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(col.a-0.001);
                #endif
                return col;
            }
            ENDCG
        }
    }
}
