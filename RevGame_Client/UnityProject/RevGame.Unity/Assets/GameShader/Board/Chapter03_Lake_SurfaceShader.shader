// 第三章：交叉水波形成不断移动的焦散和反光。只扰动光照，避免图集UV越界。
Shader "RevGame/Board/Chapter03_Lake" { Properties {
 [PerRendererData] _BossPulse ("Boss技能格面脉冲", Float) = 0
 [PerRendererData] _BossTint ("Boss技能主色", Color) = (1,1,1,1)
 [PerRendererData] _MainTex ("Sprite",2D)="white"{}
_Color("Color",Color)=(1,1,1,1)
_Tint("底色",Color)=(.65,.6,.8,1)
_Glow("魔法光色",Color)=(.6,.5,1,1)
_Strength("整体光效强度",Range(0,1))=.36
_Speed("整体流动速度",Range(0,2))=.5
_WaveStrength("水面明暗起伏",Range(0,1))=.22
_CausticStrength("波峰与焦散强度",Range(0,1))=.2
_ReflectionStrength("水面反光强度",Range(0,1))=.1
_RippleStrength("扩散波纹强度",Range(0,1))=.06
_PatternScale("水波密度倍率",Range(.25,3))=1
_RimStrength("格子边缘微光",Range(0,1))=.18
_CellSize("格宽",Float)=1.15
_Origin("棋盘原点",Float)=-5.175
[HideInInspector] _PreviewTime("验收固定时间",Float)=-1 } SubShader { Tags { "Queue"="Transparent" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" } Cull Off Lighting Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha Pass { CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#include "BoardMagicCommon.cginc"
float _WaveStrength, _CausticStrength, _ReflectionStrength, _RippleStrength, _PatternScale, _RimStrength;
fixed4 frag(v2f i):SV_Target {
 float2 p=tileLocal(i.world); float2 w=i.world*_PatternScale; float t=clockTime();
 float a=w.x*5.3+w.y*1.7-t*1.8;
 float b=w.x*-2.9+w.y*6.1+t*1.35;
 float height=sin(a+.45*sin(b))*.55+sin(b)*.45;
 float slope=cos(a+.45*sin(b))+cos(b)*.55;
 float crest=pow(saturate(slope*.48+.26),4);
 float caustic=pow(saturate(sin(a*.64+sin(b*.8))*.5+.5),8);
 float reflection=pow(saturate(.5+.5*sin(w.x*.9+w.y*1.8-t*.64)),9);
 // 波峰从棋盘外侧连续穿过多格，避免每个格子各自循环、看起来像静止花纹。
 float ripple=pow(saturate(.5+.5*cos(length(w-float2(-2.1,1.6))*5.5-t*2.3)),14);
 // 默认值只保留轻微水感；各分量可在材质中独立调低至0，不需要改Shader公式。
 float light=innerMask(p)*((crest*.56+caustic*.44)*_CausticStrength+reflection*_ReflectionStrength+ripple*_RippleStrength)+rim(p)*_RimStrength;
 return finishTileShaded(i,light,height*_WaveStrength);
}
ENDCG } } }
