// 共用UGUI顶点/透明/遮罩处理；所有效果保留原图alpha，不会把透明边缘点亮成矩形。
#include "UnityCG.cginc"
#include "UnityUI.cginc"
struct appdata
{
    float4 vertex : POSITION;
    float4 color : COLOR;
    float2 uv : TEXCOORD0;
    float4 effectUV : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};
struct v2f
{
    float4 vertex : SV_POSITION;
    fixed4 color : COLOR;
    float2 uv : TEXCOORD0;
    float4 effectUV : TEXCOORD1;
    float4 localPosition : TEXCOORD2;
    UNITY_VERTEX_OUTPUT_STEREO
};
sampler2D _MainTex;
fixed4 _Color, _TextureSampleAdd, _GlowColor;
float4 _ClipRect;
float _Strength, _Speed, _ArtBlend;
v2f vert(appdata input)
{
    v2f output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    output.localPosition = input.vertex;
    output.vertex = UnityObjectToClipPos(input.vertex);
    output.color = input.color * _Color;
    output.uv = input.uv;
    output.effectUV = input.effectUV;
    return output;
}
fixed4 FinishUI(fixed4 color, v2f input)
{
    color *= input.color;
    #ifdef UNITY_UI_CLIP_RECT
    color.a *= UnityGet2DClipping(input.localPosition.xy, _ClipRect);
    #endif
    #ifdef UNITY_UI_ALPHACLIP
    clip(color.a - 0.001);
    #endif
    return color;
}
fixed4 frag(v2f input) : SV_Target
{
    fixed4 color = tex2D(_MainTex, input.uv) + _TextureSampleAdd;
    float2 uv = input.effectUV.xy;
    float time = _Time.y * _Speed;
    #if defined(ARCANE_CARD)
    // 一道斜向宽高光加细亮边；每次经过后留一段安静时间，保留羊皮纸原色和卡牌文字的可读性。
    float sweep = frac(time + input.effectUV.z) * 3.4 - 1.2;
    float distance = abs(uv.x + uv.y * 0.48 - sweep);
    float band = 1.0 - smoothstep(0.015, 0.17, distance);
    float fine = 1.0 - smoothstep(0.0, 0.024, distance);
    float3 foil = lerp(_GlowColor.rgb, float3(0.48, 0.83, 0.78), uv.y * 0.35);
    color.rgb += foil * (band * 0.65 + fine * 0.35) * _Strength;
    #elif defined(ARCANE_PANEL)
    // 光只在边沿缓慢呼吸；不扭曲木牌/羊皮纸的底图，更不会给上层文字加模糊或彩色闪烁。
    float edgeDistance = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
    float edge = 1.0 - smoothstep(0.0, 0.11, edgeDistance);
    float wave = 0.5 + 0.5 * sin(uv.x * 8.0 + uv.y * 4.0 - time * 2.0 + input.effectUV.z * 6.28);
    color.rgb += _GlowColor.rgb * edge * (0.3 + wave * 0.7) * _Strength;
    #elif defined(ARCANE_BACKDROP)
    // 两层旋转波场形成低速“墨水/星雾”流动；不使用全屏模糊、GrabPass或额外RenderTexture。
    // UI无图时直接显示程序背景；Sprite背景则混合已有场景画，只让画面轻微流动。
    float2 p = (input.uv - 0.5) * float2(1.78, 1.0);
    float radius = length(p);
    float angle = atan2(p.y, p.x);
    float field = sin(radius * 10.0 - time + sin(angle * 3.0 + time * 0.4) * 1.6);
    float mist = 0.5 + 0.5 * sin(p.x * 5.0 + p.y * 4.0 + field * 1.8 + time * 0.32);
    float3 ink = lerp(float3(0.025, 0.075, 0.09), float3(0.09, 0.25, 0.23), mist);
    ink = lerp(ink, float3(0.25, 0.16, 0.115), (field * 0.5 + 0.5) * 0.3);
    float2 warp = float2(sin(p.y * 6.0 + time), cos(p.x * 5.0 - time * 0.7)) * _Strength * 0.012;
    fixed4 art = tex2D(_MainTex, saturate(input.uv + warp)) + _TextureSampleAdd;
    color.rgb = lerp(ink, art.rgb * (0.96 + mist * 0.04) + ink * _Strength * 0.1, _ArtBlend);
    color.a = lerp(1.0, art.a, _ArtBlend);
    #endif
    return FinishUI(color, input);
}
