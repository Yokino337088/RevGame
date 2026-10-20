# Boss 技能特效素材来源

- 素材包：Kenney Particle Pack 1.1（作者 Kenney Vleugels，https://kenney.nl/assets/particle-pack ）
- 授权：CC0 公共领域，可商用、无需署名（授权原文见同目录 `KenneyParticlePack_LICENSE.txt`）
- 获取方式：GitHub 镜像 `Calinou/kenney-particle-pack`（master 分支）
- 处理：原图为 512px 白色发光贴图，统一缩放到 256px，保存为 RGBA PNG
- 存放：`Assets/GameRes/Art/Combat/BossFx_*.png`（进入 CombatFrames 图集）

| 文件 | 原图 | 用途 |
|---|---|---|
| BossFx_Glow | light_01 | 爆炸核心、蓄力光 |
| BossFx_Ring | circle_05 | 冲击波、预警圈 |
| BossFx_Rune | magic_02 | 传送门、召唤阵 |
| BossFx_Spark | spark_05 | 弹幕子弹 |
| BossFx_Beam | trace_01_rotated | 激光、狙击射线 |
| BossFx_Flame | flame_04 | 炸弹本体 |
| BossFx_Scorch | scorch_02 | 爆炸焦痕 |
| BossFx_Slash | slash_02 | 横扫刀光 |
| BossFx_Twirl | twirl_01 | 汲取、狂暴漩涡 |
| BossFx_Smoke | smoke_03 | 瞬移残影 |
| BossFx_Reticle | symbol_01 | 狙击准星 |
| BossFx_Star | star_06 | 命中闪光 |

这些是临时替代美术。正式美术到位后，保持文件名不变、直接替换图片即可，代码无需改动。
