// 第六章：象牙金圣辉缓慢呼吸，规整的法阵与光束形成神殿秩序感。
Shader "RevGame/Board/Chapter06_Temple" { Properties {
 [PerRendererData] _BossPulse ("Boss技能格面脉冲", Float) = 0
 [PerRendererData] _BossTint ("Boss技能主色", Color) = (1,1,1,1)
 [PerRendererData] _MainTex ("Sprite",2D)="white"{}
_Color("Color",Color)=(1,1,1,1)
_Tint("底色",Color)=(.8,.71,.55,1)
_Glow("魔法光色",Color)=(1,.8,.36,1)
_Strength("整体光效强度",Range(0,1))=.53
_Speed("整体流动速度",Range(0,2))=.72
_RayStrength("圣辉放射光强度",Range(0,1))=.52
_HaloStrength("法阵光环强度",Range(0,1))=.48
_DiamondStrength("菱形符文强度",Range(0,1))=.25
_RuneSpeed("法阵旋转速度倍率",Range(0,3))=1
_RuneScale("法阵密度倍率",Range(.5,2))=1
_HaloRadius("法阵光环半径",Range(.12,.4))=.29
_ShaftStrength("神圣流光强度",Range(0,1))=.32
_ShaftSpeed("神圣流光速度倍率",Range(0,3))=1
_PatternScale("神圣流光密度倍率",Range(.25,3))=1
_BreathAmount("圣辉呼吸幅度",Range(0,.5))=.36
_BreathSpeed("圣辉呼吸速度",Range(0,3))=.85
_LightContrast("圣光明暗起伏",Range(0,1))=.25
_RimStrength("格子边缘微光",Range(0,1))=.24
_RimPulse("格边呼吸光强度",Range(0,1))=.12
_CellSize("格宽",Float)=1.15
_Origin("棋盘原点",Float)=-5.175
[HideInInspector] _PreviewTime("验收固定时间",Float)=-1 } SubShader { Tags { "Queue"="Transparent" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" } Cull Off Lighting Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha Pass { CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#include "BoardMagicCommon.cginc"
float _RayStrength, _HaloStrength, _DiamondStrength, _RuneSpeed, _RuneScale, _HaloRadius;
float _ShaftStrength, _ShaftSpeed, _PatternScale, _BreathAmount, _BreathSpeed, _LightContrast, _RimStrength, _RimPulse;
fixed4 frag(v2f i):SV_Target {
 float2 p=tileLocal(i.world); float t=clockTime();
 float2 runeLocal=p*_RuneScale; float runeTime=t*_RuneSpeed;
 float angle=atan2(p.y,p.x); float radius=length(runeLocal);
 // 呼吸幅度限制为0~0.5，亮度始终非负；设为0保持常亮，不影响法阵旋转。
 float breath=1-_BreathAmount+_BreathAmount*sin(t*_BreathSpeed);
 float ray=pow(saturate(.5+.5*cos(angle*8-runeTime*.16)),14)*smoothstep(.12,.22,radius)*(1-smoothstep(.38,.49,radius));
 float halo=thin(radius-_HaloRadius,.006)*(.53+.47*sin(angle*8-runeTime*.32));
 float diamond=thin(abs(runeLocal.x)+abs(runeLocal.y)-.36,.005);
 float2 w=i.world*_PatternScale;
 float shaft=pow(saturate(.5+.5*sin(w.x*.42-w.y*.68-t*_ShaftSpeed*.42)),8);
 float light=innerMask(p)*(ray*_RayStrength+halo*_HaloStrength+diamond*_DiamondStrength+shaft*_ShaftStrength)+rim(p)*(_RimStrength+breath*_RimPulse);
 return finishTileShaded(i,light*breath,shaft*_LightContrast);
}
ENDCG } } }
