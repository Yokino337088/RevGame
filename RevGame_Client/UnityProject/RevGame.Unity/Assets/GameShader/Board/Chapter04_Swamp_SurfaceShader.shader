// 第四章：黏稠泥流慢速回卷，偶发气泡上浮，保持偏暗的沼泽色调。
Shader "RevGame/Board/Chapter04_Swamp" { Properties {
 [PerRendererData] _BossPulse ("Boss技能格面脉冲", Float) = 0
 [PerRendererData] _BossTint ("Boss技能主色", Color) = (1,1,1,1)
 [PerRendererData] _MainTex ("Sprite",2D)="white"{}
_Color("Color",Color)=(1,1,1,1)
_Tint("底色",Color)=(.65,.55,.4,1)
_Glow("魔法光色",Color)=(.63,.76,.31,1)
_Strength("整体光效强度",Range(0,1))=.48
_Speed("整体流动速度",Range(0,2))=.62
_MudStrength("泥流亮纹强度",Range(0,1))=.72
_MudContrast("泥流明暗起伏",Range(0,1))=.31
_FlowSpeed("泥流速度倍率",Range(0,3))=1
_FlowWarp("泥流回卷幅度",Range(0,1))=.3
_PatternScale("泥流纹理密度倍率",Range(.25,3))=1
_EddyStrength("泥涡强度",Range(0,1))=.22
_EddyDensity("泥涡出现密度",Range(0,1))=.28
_BubbleStrength("气泡亮度",Range(0,1))=.56
_BubbleDensity("气泡出现密度",Range(0,1))=.42
_BubbleSpeed("气泡上浮速度倍率",Range(0,3))=1
_BubbleSize("气泡大小倍率",Range(.25,2))=1
_RimStrength("格子边缘微光",Range(0,1))=.24
_CellSize("格宽",Float)=1.15
_Origin("棋盘原点",Float)=-5.175
[HideInInspector] _PreviewTime("验收固定时间",Float)=-1 } SubShader { Tags { "Queue"="Transparent" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" } Cull Off Lighting Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha Pass { CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#include "BoardMagicCommon.cginc"
float _MudStrength, _MudContrast, _FlowSpeed, _FlowWarp, _PatternScale;
float _EddyStrength, _EddyDensity, _BubbleStrength, _BubbleDensity, _BubbleSpeed, _BubbleSize, _RimStrength;
fixed4 frag(v2f i):SV_Target {
 float2 p=tileLocal(i.world); float2 id=tileId(i.world); float t=clockTime();
 // 图案密度只缩放泥流坐标，不改变棋格坐标，避免调材质时落子位置和格边发生偏移。
 float2 w=i.world*_PatternScale; float flowTime=t*_FlowSpeed;
 float swirl=sin(w.y*2.4-flowTime*.42)*_FlowWarp;
 float mud=sin(w.x*4.1+swirl*2+flowTime*.55)+sin(w.y*4.6-w.x*1.6-flowTime*.33)*.55;
 float vein=pow(saturate(mud*.46+.32),4);
 float2 center=float2(sin(flowTime*.2+id.y),cos(flowTime*.24+id.x))*.12;
 float seed=hash21(id); float age=frac(t*_BubbleSpeed*.12+seed);
 // 泥涡只偶尔出现；连续弯曲的缓慢明暗带才是沼泽的主要运动。
 float eddy=thin(length(p-center)-(.17+.03*sin(flowTime*.8)),.006)*step(1-_EddyDensity,seed);
 float2 bubbleAt=float2(seed*.6-.3,-.34+age*.72);
 float bubble=thin(length(p-bubbleAt)-(.025+age*.035)*_BubbleSize,.005)*sin(age*3.14159)*step(1-_BubbleDensity,seed);
 float light=innerMask(p)*(vein*_MudStrength+eddy*_EddyStrength+bubble*_BubbleStrength)+rim(p)*_RimStrength;
 return finishTileShaded(i,light,mud*_MudContrast);
}
ENDCG } } }
