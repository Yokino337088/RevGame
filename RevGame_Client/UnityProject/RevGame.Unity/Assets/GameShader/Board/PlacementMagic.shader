// 落子提示：亮绿双环、角标和旋转光珠；只在业务允许的格子显示。
Shader "RevGame/Board/PlacementMagic" { Properties { [PerRendererData] _MainTex ("Sprite",2D)="white"{}
_Color("Color",Color)=(1,1,1,1)
_Tint("底色",Color)=(.65,.6,.8,1)
_Glow("魔法光色",Color)=(.6,.5,1,1)
_Strength("光纹强度",Range(0,1))=.4
_Speed("流动速度",Range(0,2))=.65
_CellSize("格宽",Float)=1.15
_Origin("棋盘原点",Float)=-5.175
[HideInInspector] _PreviewTime("验收固定时间",Float)=-1 } SubShader { Tags { "Queue"="Transparent" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" } Cull Off Lighting Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha Pass { CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#include "BoardMagicCommon.cginc"
fixed4 frag(v2f i):SV_Target {
 float2 p=tileLocal(i.world); float t=clockTime(); float seed=hash21(tileId(i.world));
 float square=max(abs(p.x),abs(p.y)); float radius=length(p); float angle=atan2(p.y,p.x);
 float pulse=.82+.18*sin(t*2.6+seed*5);
 float shadow=thin(square-.405,.033);
 float outer=thin(square-.405,.010);
 float halo=1-smoothstep(.025,.095,abs(square-.405));
 float sigilShadow=thin(radius-.29,.032);
 float sigil=thin(radius-.29,.013);
 float arc=pow(saturate(.5+.5*cos(angle*3-t*2.2)),10);
 float corners=thin(square-.35,.015)*step(.25,min(abs(p.x),abs(p.y)));
 float orb=mote(p,float2(cos(t*1.3+seed*6),sin(t*1.3+seed*6))*.29,.055);
 float fill=(1-smoothstep(.14,.39,radius))*.16;
 float alpha=saturate((shadow*.72+outer*.4+halo*.19+sigilShadow*.44+sigil*(.4+arc*.39)+corners*.4+orb*.85+fill)*pulse*(.7+_Strength*.3));
 // 深翡翠色底边在阳光、冰面和神殿的浅色格子上保留对比；绿芯仍承担魔法高亮。
 fixed3 core=lerp(fixed3(.015,.23,.07),fixed3(.12,1,.34),saturate(outer*.9+sigil*.72+corners*.7+orb));
 return fixed4(core,alpha*i.color.a);
}
ENDCG } } }
