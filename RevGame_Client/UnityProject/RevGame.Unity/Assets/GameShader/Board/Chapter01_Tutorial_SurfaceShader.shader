// 第一章：柔和星雾与缓慢漂移的星点；不用定向光束或细长条带，以免抢走棋子的注意力。
Shader "RevGame/Board/Chapter01_Tutorial" { Properties {
 [PerRendererData] _BossPulse ("Boss技能格面脉冲", Float) = 0
 [PerRendererData] _BossTint ("Boss技能主色", Color) = (1,1,1,1)
 [PerRendererData] _MainTex ("Sprite",2D)="white"{}
_Color("Color",Color)=(1,1,1,1)
_Tint("底色",Color)=(.65,.6,.8,1)
_Glow("魔法光色",Color)=(.6,.5,1,1)
_Strength("整体光效强度",Range(0,1))=.42
_Speed("整体流动速度",Range(0,2))=.45
_NebulaStrength("柔和星雾强度",Range(0,1))=.32
_StarStrength("星点亮度",Range(0,2))=.85
_StarDensity("星点密度",Range(0,1))=.35
_StarSize("星点大小",Range(.01,.1))=.04
_PatternScale("星雾与星点密度倍率",Range(.25,3))=1.15
_RimStrength("格子边缘微光",Range(0,1))=.2
_CellSize("格宽",Float)=1.15
_Origin("棋盘原点",Float)=-5.175
[HideInInspector] _PreviewTime("验收固定时间",Float)=-1 } SubShader { Tags { "Queue"="Transparent" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" } Cull Off Lighting Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha Pass { CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#include "BoardMagicCommon.cginc"
float _NebulaStrength, _StarStrength, _StarDensity, _StarSize, _PatternScale, _RimStrength;
fixed4 frag(v2f i):SV_Target {
 float2 p=tileLocal(i.world); float t=clockTime();
 float2 drift=i.world*_PatternScale+float2(-t*.07,t*.025);
 float2 field=floor(drift); float2 star=frac(drift)-.5;
 float2 origin=(float2(hash21(field+5),hash21(field+9))-.5)*.35;
 float twinkle=.72+.28*sin(t*.65+hash21(field)*6.28318);
 float glint=mote(star,origin,_StarSize)*step(1-_StarDensity,hash21(field))*twinkle;
 float2 sway=float2(sin(t*.12),cos(t*.1))*.3;
 float cloud=softNoise(i.world*_PatternScale*.45+sway)*.7+softNoise(i.world*_PatternScale*.9-sway*.5)*.3;
 float light=innerMask(p)*(glint*_StarStrength+cloud*_NebulaStrength)+rim(p)*_RimStrength;
 return finishTileShaded(i,light,(cloud-.5)*_NebulaStrength*.3);
}
ENDCG } } }
