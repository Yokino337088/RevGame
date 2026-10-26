// 第二章：柔和的暖阳漫反射与圆润光斑缓慢起伏，不生成定向光束。
Shader "RevGame/Board/Chapter02_Village" { Properties { [PerRendererData] _MainTex ("Sprite",2D)="white"{}
_Color("Color",Color)=(1,1,1,1)
_Tint("底色",Color)=(.65,.6,.8,1)
_Glow("魔法光色",Color)=(.6,.5,1,1)
_Strength("整体光效强度",Range(0,1))=.42
_Speed("整体流动速度",Range(0,2))=.48
_SunlightStrength("柔和日光强度",Range(0,1))=.24
_DappleStrength("摇曳光斑强度",Range(0,1))=.26
_FireflyStrength("萤火亮度",Range(0,2))=.4
_PatternScale("光斑密度倍率",Range(.25,3))=1.2
_RimStrength("格子边缘微光",Range(0,1))=.2
_CellSize("格宽",Float)=1.15
_Origin("棋盘原点",Float)=-5.175
[HideInInspector] _PreviewTime("验收固定时间",Float)=-1 } SubShader { Tags { "Queue"="Transparent" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" } Cull Off Lighting Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha Pass { CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#include "BoardMagicCommon.cginc"
float _SunlightStrength, _DappleStrength, _FireflyStrength, _PatternScale, _RimStrength;
fixed4 frag(v2f i):SV_Target {
 float2 p=tileLocal(i.world); float t=clockTime(); float seed=hash21(tileId(i.world));
 float2 sway=float2(sin(t*.23),cos(t*.19))*.3;
 float sunlight=.45+.55*softNoise(i.world*_PatternScale*.27+sway*.4);
 float dapples=smoothstep(.2,.85,softNoise(i.world*_PatternScale+sway));
 float2 fly=float2(sin(t*.8+seed*17),cos(t*.57+seed*11))*.27;
 float firefly=mote(p,fly,.045)*step(.64,seed);
 float light=innerMask(p)*(sunlight*_SunlightStrength+dapples*_DappleStrength+firefly*_FireflyStrength)+rim(p)*_RimStrength;
 return finishTileShaded(i,light,(dapples-.5)*_DappleStrength*.3);
}
ENDCG } } }
