// ============================================================
// 本文件由 ABResPathGenerator 自动生成，请勿手改（重新生成会整份覆盖）。
//
// 内容：资源根目录 "Assets/GameRes" 下所有文件夹的「目录前缀」常量。
// 用法：目录前缀与资源名一起交给资源系统（框架内部拼成 "UI/Icon/Hero_1001"）
//       RevResManager.Load<GameObject>(RevResPath.UI_Icon, "Hero_1001", RevResGroup.UI);
//
// 为什么这样设计：
//   · 目录前缀很少变 —— 新增资源不用重新生成，随手写上资源名即可；
//   · const 是编译期常量：目录这一段有编译期保护，写错编译不过；
//   · 两段直接传参即可：框架按「两段增量哈希」算缓存键，
//     命中缓存时不拼字符串、零分配（见 RevResourceSystem\Core\RevResPathUtil.cs）；
//   · 注意："Hero_1001" 那一段仍是普通字符串 ——
//     写错不会编译报错，只会在运行时加载失败（建议从 Project 窗口复制名字）。
// ============================================================
namespace Revolution
{
    /// <summary>资源目录前缀常量（值均以 "/" 结尾；与资源名一起传给 RevResManager）</summary>
    public static class RevResPath
    {
        public const string Art = "Art/";
        public const string Art_Background = "Art/Background/";
        public const string Art_Board = "Art/Board/";
        public const string Art_Boss = "Art/Boss/";
        public const string Art_Boss_Chapter01_Swordman = "Art/Boss/Chapter01_Swordman/";
        public const string Art_Boss_Chapter02_Archer = "Art/Boss/Chapter02_Archer/";
        public const string Art_Boss_Chapter03_MageWater = "Art/Boss/Chapter03_MageWater/";
        public const string Art_Boss_Chapter04_Assassin = "Art/Boss/Chapter04_Assassin/";
        public const string Art_Boss_Chapter05_Shield = "Art/Boss/Chapter05_Shield/";
        public const string Art_Boss_Chapter06_Priest = "Art/Boss/Chapter06_Priest/";
        public const string Art_Combat = "Art/Combat/";
        public const string Art_Combat_Melee = "Art/Combat/Melee/";
        public const string Art_Combat_Projectiles = "Art/Combat/Projectiles/";
        public const string Art_Pieces = "Art/Pieces/";
        public const string Art_Pieces_Enemy = "Art/Pieces/Enemy/";
        public const string Art_Pieces_Enemy_Archer = "Art/Pieces/Enemy/Archer/";
        public const string Art_Pieces_Enemy_Assassin = "Art/Pieces/Enemy/Assassin/";
        public const string Art_Pieces_Enemy_Crossbow = "Art/Pieces/Enemy/Crossbow/";
        public const string Art_Pieces_Enemy_HeavySword = "Art/Pieces/Enemy/HeavySword/";
        public const string Art_Pieces_Enemy_MageFire = "Art/Pieces/Enemy/MageFire/";
        public const string Art_Pieces_Enemy_MageWater = "Art/Pieces/Enemy/MageWater/";
        public const string Art_Pieces_Enemy_Priest = "Art/Pieces/Enemy/Priest/";
        public const string Art_Pieces_Enemy_Shield = "Art/Pieces/Enemy/Shield/";
        public const string Art_Pieces_Enemy_Swordman = "Art/Pieces/Enemy/Swordman/";
        public const string Art_Pieces_Helper = "Art/Pieces/Helper/";
        public const string Art_Pieces_Helper_Storm = "Art/Pieces/Helper/Storm/";
        public const string Art_Pieces_Player = "Art/Pieces/Player/";
        public const string Art_Pieces_Player_Archer = "Art/Pieces/Player/Archer/";
        public const string Art_Pieces_Player_Assassin = "Art/Pieces/Player/Assassin/";
        public const string Art_Pieces_Player_Crossbow = "Art/Pieces/Player/Crossbow/";
        public const string Art_Pieces_Player_HeavySword = "Art/Pieces/Player/HeavySword/";
        public const string Art_Pieces_Player_MageFire = "Art/Pieces/Player/MageFire/";
        public const string Art_Pieces_Player_MageWater = "Art/Pieces/Player/MageWater/";
        public const string Art_Pieces_Player_Priest = "Art/Pieces/Player/Priest/";
        public const string Art_Pieces_Player_Shield = "Art/Pieces/Player/Shield/";
        public const string Art_Pieces_Player_Swordman = "Art/Pieces/Player/Swordman/";
        public const string Art_Status = "Art/Status/";
        public const string Art_Status_Encircled = "Art/Status/Encircled/";
        public const string Art_Status_EyeBlink = "Art/Status/EyeBlink/";
        public const string Art_Story = "Art/Story/";
        public const string Art_UI = "Art/UI/";
        public const string Atlases = "Atlases/";
        public const string Audio = "Audio/";
        public const string Audio_Bgm = "Audio/Bgm/";
        public const string Audio_Sfx = "Audio/Sfx/";
        public const string Audio_Sfx_Boss = "Audio/Sfx/Boss/";
        public const string Audio_Sfx_Boss_Chapter01 = "Audio/Sfx/Boss/Chapter01/";
        public const string Audio_Sfx_Boss_Chapter02 = "Audio/Sfx/Boss/Chapter02/";
        public const string Audio_Sfx_Boss_Chapter03 = "Audio/Sfx/Boss/Chapter03/";
        public const string Audio_Sfx_Boss_Chapter04 = "Audio/Sfx/Boss/Chapter04/";
        public const string Audio_Sfx_Boss_Chapter05 = "Audio/Sfx/Boss/Chapter05/";
        public const string Audio_Sfx_Boss_Chapter06 = "Audio/Sfx/Boss/Chapter06/";
        public const string Audio_Sfx_Boss_Common = "Audio/Sfx/Boss/Common/";
        public const string Audio_Sfx_Helper = "Audio/Sfx/Helper/";
        public const string Audio_Sfx_Helper_Helper = "Audio/Sfx/Helper/Helper/";
        public const string Board = "Board/";
        public const string Board_Indicator = "Board/Indicator/";
        public const string Board_Tile = "Board/Tile/";
        public const string ChapterBackground = "ChapterBackground/";
        public const string Config = "Config/";
        public const string Config_Art = "Config/Art/";
        public const string Config_Boss = "Config/Boss/";
        public const string Config_Story = "Config/Story/";
        public const string Data = "Data/";
        public const string Effects = "Effects/";
        public const string Effects_Combat = "Effects/Combat/";
        public const string Effects_Helper = "Effects/Helper/";
        public const string Fonts = "Fonts/";
        public const string Fonts_CJK = "Fonts/CJK/";
        public const string HelperTextures = "HelperTextures/";
        public const string Material = "Material/";
        public const string Material_Background = "Material/Background/";
        public const string Material_Board = "Material/Board/";
        public const string Material_BossDeath = "Material/BossDeath/";
        public const string Material_Cards = "Material/Cards/";
        public const string Material_Combat = "Material/Combat/";
        public const string Material_Helper = "Material/Helper/";
        public const string Material_Pieces = "Material/Pieces/";
        public const string Material_UI = "Material/UI/";
        public const string Pieces = "Pieces/";
        public const string Pieces_Boss = "Pieces/Boss/";
        public const string Pieces_Helper = "Pieces/Helper/";
        public const string Pieces_Unit = "Pieces/Unit/";
        public const string UI = "UI/";
        public const string UI_Item = "UI/Item/";
        public const string UI_Item_Card = "UI/Item/Card/";
        public const string UI_Item_Notice = "UI/Item/Notice/";
        public const string UI_Panel = "UI/Panel/";
        public const string UI_Panel_Diagnostics = "UI/Panel/Diagnostics/";
        public const string UI_Panel_Loading = "UI/Panel/Loading/";
        public const string UI_Panel_Settings = "UI/Panel/Settings/";
        public const string UI_Panel_Story = "UI/Panel/Story/";
        public const string UI_Panel_Victory = "UI/Panel/Victory/";
    }
}
