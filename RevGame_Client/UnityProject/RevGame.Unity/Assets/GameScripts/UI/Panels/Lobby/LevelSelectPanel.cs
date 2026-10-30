// ============================================================
// LevelSelectPanel.cs —— 六章选关。
// 每个按钮固定提交章号；是否解锁由服务判定，禁用外观只是提示，不能替代规则校验。
// ============================================================
using RevGame.Flow;
using RevGame.Services;
using Revolution;

using UnityEngine.UI;

namespace RevGame.UI
{
    [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Normal)]
    public sealed class LevelSelectPanel : RevUIPanel
    {
        [RevBind]
        private Button _btnHome;
        [RevBind]
        private Text _hint;
        private Button[] _chapters;
        // 六个固定章节按钮由方法特性接线，数组只用于刷新可交互状态。
        protected override void OnBindView()
        {
            _chapters = new Button[Core.Consts.ChapterCount];
            for (int i = 0; i < _chapters.Length; i++)
            {
                _chapters[i] = transform.Find("chapter" + (i + 1)).GetComponent<Button>();
            }
        }
        protected override void OnRefreshView()
        {
            int unlocked = RevServices.GetRequired<IBattleService>().UnlockedChapter;
            for (int i = 0; i < _chapters.Length; i++)
            {
                _chapters[i].interactable = i < unlocked;
                _chapters[i].GetComponentInChildren<Text>().text = "第 " + (i + 1) + " 章\n" + (i < unlocked ? "开始挑战" : "通关前章解锁");
            }
            _hint.text = "每章 10 个普通回合 + 1 个 Boss 回合\n同章保留残局，失败重开本章";
        }
        private void OnChapter(int chapter)
        {
            var flow = RevServices.GetRequired<GameFlow>();

            if (!flow.EnterBattle(chapter))
            {
                _hint.text = flow.LastError;
            }
        }

        [RevButtonClick("chapter1")]
        private void OnChapter1()
        {
            OnChapter(1);
        }
        [RevButtonClick("chapter2")]
        private void OnChapter2()
        {
            OnChapter(2);
        }
        [RevButtonClick("chapter3")]
        private void OnChapter3()
        {
            OnChapter(3);
        }
        [RevButtonClick("chapter4")]
        private void OnChapter4()
        {
            OnChapter(4);
        }
        [RevButtonClick("chapter5")]
        private void OnChapter5()
        {
            OnChapter(5);
        }
        [RevButtonClick("chapter6")]
        private void OnChapter6()
        {
            OnChapter(6);
        }
        [RevButtonClick("btnHome")]
        private void OnHome()
        {
            RevServices.GetRequired<GameFlow>().ReturnHome();
        }
        [RevButtonClick("btnSettings")]
        private void OnSettings()
        {
            RevServices.GetRequired<GameFlow>().OpenAudioSettings();
        }
    }
}
