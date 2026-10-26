// 第五章：边缘凝霜与内部冰晶，冷光偶尔扫过裂面。
Shader "RevGame/Board/Chapter05_Ice" { Properties { [PerRendererData] _MainTex ("Sprite",2D)="white"{}
_Color("Color",Color)=(1,1,1,1)
_Tint("底色",Color)=(.72,.82,.88,1)
_Glow("魔法光色",Color)=(.7,.94,1,1)
_Strength("整体光效强度",Range(0,1))=.5
_Speed("整体流动速度",Range(0,2))=.68
_CrystalStrength("冰晶常亮强度",Range(0,1))=.33
_GlintStrength("冰晶流动冷光强度",Range(0,1))=.53
_GlintSpeed("流动冷光速度倍率",Range(0,3))=1
_CrystalScale("冰晶纹理密度倍率",Range(.5,2))=1
_CrackStrength("冰纹亮度",Range(0,1))=.32
_CrackWidth("冰纹线宽",Range(.001,.03))=.004
_FrostStrength("边缘凝霜强度",Range(0,1))=.43
_FrostWidth("凝霜向内厚度",Range(.02,.35))=.18
_FrostSpeed("凝霜变化速度倍率",Range(0,3))=1
_SnowStrength("雪点亮度",Range(0,1))=.56
_SnowDensity("雪点出现密度",Range(0,1))=1
_SnowSpeed("雪点飘落速度倍率",Range(0,3))=1
_SnowSize("雪点大小",Range(.005,.06))=.02
_ColdShade("冰面冷色压暗",Range(0,1))=.15
_IceContrast("冷光明暗起伏",Range(0,1))=.27
_RimStrength("格子边缘微光",Range(0,1))=.25
_CellSize("格宽",Float)=1.15
_Origin("棋盘原点",Float)=-5.175
[HideInInspector] _PreviewTime("验收固定时间",Float)=-1 } SubShader { Tags { "Queue"="Transparent" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" } Cull Off Lighting Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha Pass { CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#include "BoardMagicCommon.cginc"
float _CrystalStrength, _GlintStrength, _GlintSpeed, _CrystalScale, _CrackStrength, _CrackWidth;
float _FrostStrength, _FrostWidth, _FrostSpeed, _SnowStrength, _SnowDensity, _SnowSpeed, _SnowSize, _ColdShade, _IceContrast, _RimStrength;
fixed4 frag(v2f i):SV_Target {
 float2 p=tileLocal(i.world); float t=clockTime(); float2 id=tileId(i.world);
 float2 crystalLocal=p*_CrystalScale;
 float angle=atan2(p.y,p.x); float radius=length(crystalLocal);
 float crystal=pow(abs(cos(angle*4+hash21(id)*4)),34)*smoothstep(.06,.12,radius)*(1-smoothstep(.24,.39,radius));
 float branch=thin(abs(crystalLocal.x)+abs(crystalLocal.y)-.38,_CrackWidth)*_CrackStrength;
 // 凝霜厚度是格宽的比例；最小值大于0，避免smoothstep两端重合产生无效像素。
 float frost=smoothstep(.47-_FrostWidth,.47,max(abs(p.x),abs(p.y)))*(.29+.21*sin(angle*8+id.x+t*_FrostSpeed*.72));
 float sweep=pow(saturate(.5+.5*sin(i.world.x*.72+i.world.y*.88-t*_GlintSpeed*.82)),8);
 float snowTime=t*_SnowSpeed;
 float2 flake=float2(sin(snowTime*.5+id.x*7)*.24,.42-frac(snowTime*.12+hash21(id))*.8);
 float snow=mote(p,flake,_SnowSize)*step(1-_SnowDensity,hash21(id));
 float light=innerMask(p)*(crystal*(_CrystalStrength+_GlintStrength*sweep)+branch+snow*_SnowStrength)+frost*_FrostStrength+rim(p)*_RimStrength;
 return finishTileShaded(i,light,-_ColdShade+sweep*_IceContrast);
}
ENDCG } } }
